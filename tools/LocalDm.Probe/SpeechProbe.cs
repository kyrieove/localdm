using System.Diagnostics;
using System.Speech.Synthesis;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalDm.Core.Domain;
using LocalDm.Core.Hosting;
using LocalDm.Infrastructure.Magpie;
using LocalDm.Infrastructure.Voice;

namespace LocalDm.Infrastructure.Probe;

/// <summary>
/// 合成口播探针：对真实本机 Magpie + 真实 Windows TTS 发起一次合成请求。
/// 只发送合成公开文字，不含任何真实剧本内容。输出脱敏 JSON 供 docs/evidence/us1 引用。
/// 用法：dotnet run --project tools/LocalDm.Probe -- --out docs/evidence/us1/
/// </summary>
public static class SpeechProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        var outDir = DmAcceptanceProbe.ArgumentValue(args, "--out") ?? "docs/evidence/us1";
        var modelId = DmAcceptanceProbe.ArgumentValue(args, "--model") ?? "group/auto-deepseek-v4-1-flash";
        var voiceOverride = DmAcceptanceProbe.ArgumentValue(args, "--voice");
        Directory.CreateDirectory(outDir);

        var result = new ProbeResult
        {
            ObservedAt = DateTimeOffset.Now.ToString("O"),
            ModelId = modelId,
        };

        using var gateway = new MagpieGateway { BaseUri = ToolSettings.DefaultMagpieUri };

        try
        {
            var sw = Stopwatch.StartNew();
            var models = await gateway.ListModelsAsync(CancellationToken.None);
            sw.Stop();
            result.ModelsEndpoint = new EndpointResult
            {
                Path = "/v1/models",
                Ok = true,
                ElapsedMs = sw.ElapsedMilliseconds,
                ModelCount = models.Count,
                SelectedPresent = models.Contains(modelId, StringComparer.Ordinal),
            };
        }
        catch (DomainRuleException ex)
        {
            result.ModelsEndpoint = new EndpointResult { Path = "/v1/models", Ok = false, ErrorCode = ex.Code.ToString(), ErrorReason = ex.PublicReason };
        }

        using var tts = new TtsService();
        var voices = tts.GetChineseVoices();
        result.ChineseVoices = voices.Select(v => $"{v.Name} ({v.Culture})").ToArray();

        if (result.ModelsEndpoint?.Ok == true && voices.Count > 0)
        {
            var director = new HostDirector(gateway, tts);
            var sw = Stopwatch.StartNew();
            var turn = await director.SpeakPublicAsync(
                modelId,
                "用一句话欢迎五位侦探来到灯塔，然后提示他们开始阅读第一幕。这是合成主持测试。",
                voiceOverride,
                CancellationToken.None);
            sw.Stop();

            result.Speech = new SpeechResult
            {
                State = turn.State.ToString(),
                Late = turn.Late,
                ElapsedMs = sw.ElapsedMilliseconds,
                Text = turn.Item?.SafeText,
                PublicReason = turn.PublicReason,
            };

            // 重播：不请求模型、不写状态
            if (turn.Item is not null)
            {
                var replay = await director.ReplayAsync(voiceOverride, CancellationToken.None);
                result.Replay = new SpeechResult
                {
                    State = replay.State.ToString(),
                    Late = replay.Late,
                    Text = replay.Item?.SafeText,
                    PublicReason = replay.PublicReason,
                };
            }
        }

        var path = Path.Combine(outDir, "desktop-speech-probe.json");
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(result, JsonOptions),
            System.Text.Encoding.UTF8);
        Console.WriteLine("wrote " + path);
        return 0;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class ProbeResult
    {
        [JsonPropertyName("observedAt")]
        public string ObservedAt { get; set; } = string.Empty;

        [JsonPropertyName("modelId")]
        public string ModelId { get; set; } = string.Empty;

        [JsonPropertyName("modelsEndpoint")]
        public EndpointResult? ModelsEndpoint { get; set; }

        [JsonPropertyName("chineseVoices")]
        public string[] ChineseVoices { get; set; } = [];

        [JsonPropertyName("speech")]
        public SpeechResult? Speech { get; set; }

        [JsonPropertyName("replay")]
        public SpeechResult? Replay { get; set; }

        [JsonPropertyName("limits")]
        public string[] Limits { get; set; } =
        [
            "Only synthetic public text was sent",
            "Magpie downstream may be remote",
            "Real speaker audibility must be confirmed by a human at the target machine",
        ];
    }

    private sealed class EndpointResult
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("ok")]
        public bool Ok { get; set; }

        [JsonPropertyName("elapsedMs")]
        public long ElapsedMs { get; set; }

        [JsonPropertyName("modelCount")]
        public int ModelCount { get; set; }

        [JsonPropertyName("selectedPresent")]
        public bool SelectedPresent { get; set; }

        [JsonPropertyName("errorCode")]
        public string? ErrorCode { get; set; }

        [JsonPropertyName("errorReason")]
        public string? ErrorReason { get; set; }
    }

    private sealed class SpeechResult
    {
        [JsonPropertyName("state")]
        public string State { get; set; } = string.Empty;

        [JsonPropertyName("late")]
        public bool Late { get; set; }

        [JsonPropertyName("elapsedMs")]
        public long ElapsedMs { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("publicReason")]
        public string? PublicReason { get; set; }
    }
}
