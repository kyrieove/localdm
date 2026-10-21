using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalDm.Core.Domain;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Diagnostics;

namespace LocalDm.Infrastructure.Magpie;

/// <summary>
/// Windows Magpie 最小契约：GET /v1/models 与非流式 POST /v1/chat/completions。
/// 仅允许 loopback；/health 返回 404 不代表离线。
/// </summary>
public sealed class MagpieGateway : IMagpieGateway, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public MagpieGateway(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
        _http.Timeout = TimeSpan.FromSeconds(120);
    }

    public Uri? BaseUri { get; set; }

    public string? CallerKey { get; set; }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var uri = Resolve("/v1/models");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        ApplyAuthorization(request);

        using var response = await SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new DomainRuleException(
                DomainErrorCode.MODEL_FAILED,
                $"读取模型列表失败（HTTP {(int)response.StatusCode}）。");
        }

        var payload = await response.Content.ReadFromJsonAsync<ModelsResponse>(cancellationToken);
        var ids = payload?.Data?
            .Select(m => m.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray() ?? [];

        if (ids.Length == 0)
        {
            throw new DomainRuleException(DomainErrorCode.MODEL_FAILED, "模型列表为空，未找到可用 model ID。");
        }

        return ids;
    }

    public async Task<string> CompleteAsync(MagpieCompletionRequest request, CancellationToken cancellationToken) =>
        (await CompleteWithUsageAsync(request, cancellationToken)).Content;

    /// <summary>
    /// 非流式生成，并解析响应里的 token 用量。
    /// 没有 usage（或缺 prompt_tokens）时用量为 null：不是错误，只是这一轮只能按估算计量。
    /// </summary>
    public async Task<MagpieCompletion> CompleteWithUsageAsync(
        MagpieCompletionRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ModelId))
        {
            throw new DomainRuleException(DomainErrorCode.INVALID_INPUT, "model ID 不能为空。");
        }

        if (request.Stream)
        {
            throw new DomainRuleException(DomainErrorCode.UNSUPPORTED, "首版只支持非流式生成。");
        }

        var uri = Resolve("/v1/chat/completions");
        using var message = new HttpRequestMessage(HttpMethod.Post, uri);
        ApplyAuthorization(message);
        message.Content = JsonContent.Create(new CompletionRequest
        {
            Model = request.ModelId,
            Stream = false,
            Messages = request.Messages.Select(m => new MessageDto { Role = m.Role, Content = m.Content }).ToArray(),
        });

        using var response = await SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new DomainRuleException(
                DomainErrorCode.MODEL_FAILED,
                $"模型请求失败（HTTP {(int)response.StatusCode}）。");
        }

        CompletionResponse? payload;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<CompletionResponse>(cancellationToken);
        }
        catch (JsonException)
        {
            throw new DomainRuleException(DomainErrorCode.MODEL_FAILED, "模型返回了无法解析的 JSON。");
        }

        var content = payload?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new DomainRuleException(DomainErrorCode.MODEL_FAILED, "模型返回了空内容。");
        }

        return new MagpieCompletion(content.Trim(), ParseUsage(payload));
    }

    /// <summary>
    /// 解析 OpenAI 兼容的 usage（prompt_tokens / completion_tokens），
    /// 以及 DeepSeek 可选的 prompt_cache_hit_tokens。
    /// 缺 prompt_tokens 就当作没有用量：宁可退回估算，也不要用 0 冒充真实占用。
    /// </summary>
    private static MagpieUsage? ParseUsage(CompletionResponse? payload)
    {
        var usage = payload?.Usage;
        if (usage?.PromptTokens is not { } promptTokens)
        {
            return null;
        }

        return new MagpieUsage(
            promptTokens,
            usage.CompletionTokens ?? 0,
            usage.PromptCacheHitTokens);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DomainRuleException(DomainErrorCode.MODEL_FAILED, "模型请求超时。");
        }
        catch (HttpRequestException ex)
        {
            throw new DomainRuleException(
                DomainErrorCode.MODEL_FAILED,
                "无法连接本机 Magpie：" + RedactedLog.Redact(ex.Message));
        }
    }

    private void ApplyAuthorization(HttpRequestMessage request)
    {
        // caller key 只来自本软件明确配置处；不从 Magpie 供应商配置抓取
        if (!string.IsNullOrWhiteSpace(CallerKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CallerKey);
        }
    }

    private Uri Resolve(string path)
    {
        var settings = new ToolSettings
        {
            MagpieBaseUri = BaseUri ?? ToolSettings.DefaultMagpieUri,
        };
        settings.Validate();
        return new Uri(settings.MagpieBaseUri, path.TrimStart('/'));
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    private sealed class ModelsResponse
    {
        [JsonPropertyName("data")]
        public List<ModelDto>? Data { get; set; }
    }

    private sealed class ModelDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }

    private sealed class CompletionRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("messages")]
        public MessageDto[] Messages { get; set; } = [];

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }
    }

    private sealed class MessageDto
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    private sealed class CompletionResponse
    {
        [JsonPropertyName("choices")]
        public List<ChoiceDto>? Choices { get; set; }

        [JsonPropertyName("usage")]
        public UsageDto? Usage { get; set; }
    }

    private sealed class UsageDto
    {
        [JsonPropertyName("prompt_tokens")]
        public int? PromptTokens { get; set; }

        [JsonPropertyName("completion_tokens")]
        public int? CompletionTokens { get; set; }

        /// <summary>DeepSeek 的可选字段：命中前缀缓存的 prompt token 数。</summary>
        [JsonPropertyName("prompt_cache_hit_tokens")]
        public int? PromptCacheHitTokens { get; set; }
    }

    private sealed class ChoiceDto
    {
        [JsonPropertyName("message")]
        public MessageDto? Message { get; set; }
    }
}
