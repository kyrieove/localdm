using System.Diagnostics;
using System.Net.Http;
using System.Net;
using System.Text;
using LocalDm.Core.Domain;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Magpie;

namespace LocalDm.Tests;

/// <summary>T015：Magpie loopback 契约、未知 model、非空响应、取消与晚到结果。</summary>
public sealed class MagpieGatewayTests
{
    private const string ModelsJson = """
        {"data":[{"id":"group/auto-deepseek-v4-1-flash"},{"id":"codex/gpt-6.1-sol"}]}
        """;

    [Fact]
    public async Task Lists_model_ids_from_loopback_payload()
    {
        var gateway = new MagpieGateway(Stub(ModelsJson))
        {
            BaseUri = new Uri("http://127.0.0.1:3425/"),
        };

        var ids = await gateway.ListModelsAsync(TestContext.Current.CancellationToken);

        Assert.Contains("group/auto-deepseek-v4-1-flash", ids);
        Assert.Equal(2, ids.Count);
    }

    [Fact]
    public async Task Health_404_does_not_mean_offline_but_models_failure_is_reported()
    {
        var gateway = new MagpieGateway(Stub(null, HttpStatusCode.NotFound))
        {
            BaseUri = new Uri("http://127.0.0.1:3425/"),
        };

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => gateway.ListModelsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(DomainErrorCode.MODEL_FAILED, ex.Code);
        Assert.Contains("404", ex.PublicReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_model_returns_public_failure_not_silent_success()
    {
        var gateway = new MagpieGateway(Stub(string.Empty, HttpStatusCode.BadRequest))
        {
            BaseUri = new Uri("http://127.0.0.1:3425/"),
        };

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => gateway.CompleteAsync(
            new MagpieCompletionRequest
            {
                ModelId = "does-not-exist",
                Messages = [new MagpieMessage { Role = "user", Content = "合成输入" }],
            },
            TestContext.Current.CancellationToken));
        Assert.Equal(DomainErrorCode.MODEL_FAILED, ex.Code);
    }

    [Fact]
    public async Task Empty_content_is_rejected_not_spoken()
    {
        var gateway = new MagpieGateway(Stub("""{"choices":[{"message":{"content":"   "}}]}"""))
        {
            BaseUri = new Uri("http://127.0.0.1:3425/"),
        };

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => gateway.CompleteAsync(
            new MagpieCompletionRequest
            {
                ModelId = "group/auto-deepseek-v4-1-flash",
                Messages = [new MagpieMessage { Role = "user", Content = "合成输入" }],
            },
            TestContext.Current.CancellationToken));
        Assert.Equal(DomainErrorCode.MODEL_FAILED, ex.Code);
    }

    [Fact]
    public async Task Invalid_json_is_rejected()
    {
        var gateway = new MagpieGateway(Stub("not json"))
        {
            BaseUri = new Uri("http://127.0.0.1:3425/"),
        };

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => gateway.CompleteAsync(
            new MagpieCompletionRequest
            {
                ModelId = "group/auto-deepseek-v4-1-flash",
                Messages = [new MagpieMessage { Role = "user", Content = "合成输入" }],
            },
            TestContext.Current.CancellationToken));
        Assert.Equal(DomainErrorCode.MODEL_FAILED, ex.Code);
    }

    [Fact]
    public async Task Non_loopback_base_uri_is_refused()
    {
        var gateway = new MagpieGateway(Stub(ModelsJson))
        {
            BaseUri = new Uri("http://example.com/"),
        };

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => gateway.ListModelsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(DomainErrorCode.CONSENT_REQUIRED, ex.Code);
    }

    [Fact]
    public async Task Cancellation_surfaces_as_cancelled_request()
    {
        // 服务器永不响应：取消必须立即结束，不等待响应
        var handler = new BlockingHandler();
        var gateway = new MagpieGateway(new HttpClient(handler))
        {
            BaseUri = new Uri("http://127.0.0.1:3425/"),
        };
        using var cts = new CancellationTokenSource();
        var task = gateway.CompleteAsync(
            new MagpieCompletionRequest
            {
                ModelId = "group/auto-deepseek-v4-1-flash",
                Messages = [new MagpieMessage { Role = "user", Content = "合成输入" }],
            },
            cts.Token);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Request_payload_sends_stream_false_and_caller_key_when_configured()
    {
        var handler = new InspectingHandler("""{"choices":[{"message":{"content":"连接成功"}}]}""");
        var gateway = new MagpieGateway(new HttpClient(handler))
        {
            BaseUri = new Uri("http://127.0.0.1:3425/"),
            CallerKey = "sk-test-caller-key-123456",
        };

        var text = await gateway.CompleteAsync(
            new MagpieCompletionRequest
            {
                ModelId = "group/auto-deepseek-v4-1-flash",
                Messages = [new MagpieMessage { Role = "user", Content = "请只回复四个字：连接成功。" }],
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("连接成功", text);
        Assert.Contains("\"stream\":false", handler.LastBody, StringComparison.Ordinal);
        Assert.Equal("Bearer", handler.LastAuthorizationScheme);
        Assert.Equal("/v1/chat/completions", handler.LastPath);
    }

    [Fact]
    public async Task Usage_is_parsed_from_the_completion_payload()
    {
        var gateway = new MagpieGateway(Stub("""
            {"choices":[{"message":{"content":"好"}}],"usage":{"prompt_tokens":1234,"completion_tokens":56,"prompt_cache_hit_tokens":1000}}
            """))
        {
            BaseUri = new Uri("http://127.0.0.1:3425/"),
        };

        var completion = await gateway.CompleteWithUsageAsync(
            new MagpieCompletionRequest
            {
                ModelId = "group/auto-deepseek-v4-1-flash",
                Messages = [new MagpieMessage { Role = "user", Content = "合成输入" }],
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("好", completion.Content);
        Assert.NotNull(completion.Usage);
        Assert.Equal(1234, completion.Usage!.PromptTokens);
        Assert.Equal(56, completion.Usage.CompletionTokens);
        Assert.Equal(1000, completion.Usage.CacheHitTokens);
    }

    [Fact]
    public async Task Missing_usage_is_not_an_error()
    {
        // 服务端没给 usage 时不能报错：这一轮退回按估算计量。
        var gateway = new MagpieGateway(Stub("""{"choices":[{"message":{"content":"好"}}]}"""))
        {
            BaseUri = new Uri("http://127.0.0.1:3425/"),
        };

        var completion = await gateway.CompleteWithUsageAsync(
            new MagpieCompletionRequest
            {
                ModelId = "group/auto-deepseek-v4-1-flash",
                Messages = [new MagpieMessage { Role = "user", Content = "合成输入" }],
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("好", completion.Content);
        Assert.Null(completion.Usage);
    }

    [Fact]
    public async Task Complete_async_keeps_returning_only_the_text()
    {
        var gateway = new MagpieGateway(Stub("""
            {"choices":[{"message":{"content":"连接成功"}}],"usage":{"prompt_tokens":10,"completion_tokens":2}}
            """))
        {
            BaseUri = new Uri("http://127.0.0.1:3425/"),
        };

        var text = await gateway.CompleteAsync(
            new MagpieCompletionRequest
            {
                ModelId = "group/auto-deepseek-v4-1-flash",
                Messages = [new MagpieMessage { Role = "user", Content = "合成输入" }],
            },
            TestContext.Current.CancellationToken);

        Assert.Equal("连接成功", text);
    }

    private static HttpClient Stub(string? body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new FixedHandler(body, status));

    private sealed class FixedHandler : HttpMessageHandler
    {
        private readonly string? _body;
        private readonly HttpStatusCode _status;

        public FixedHandler(string? body, HttpStatusCode status)
        {
            _body = body;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = new HttpResponseMessage(_status);
            if (_body is not null)
            {
                response.Content = new StringContent(_body, Encoding.UTF8, "application/json");
            }

            return Task.FromResult(response);
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        }
    }

    private sealed class InspectingHandler : HttpMessageHandler
    {
        private readonly string _body;

        public InspectingHandler(string body) => _body = body;

        public string LastBody { get; private set; } = string.Empty;

        public string? LastAuthorizationScheme { get; private set; }

        public string? LastPath { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
