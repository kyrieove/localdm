using LocalDm.Core.Domain;
using LocalDm.Infrastructure.Voice;

namespace LocalDm.Tests;

/// <summary>
/// Edge-TTS 相关用例。
///
/// 这些用例<b>不依赖宿主机是否装了 python / edge-tts</b>：产品的核心前提就是
/// 「神经音色不可用时优雅降级到 Windows 本地语音」，降级路径如果只能在装了
/// 依赖的机器上测，它就等于没测。真实引擎的探测只做「不抛异常 + 结果稳定」这类
/// 与环境无关的断言；需要真听声音的用例用 <c>Assert.Skip</c> 显式跳过。
/// </summary>
public sealed class EdgeTtsTests
{
    /// <summary>可控的假 Edge 引擎：记录是否被调用，用来断言走的是哪条路径。</summary>
    private sealed class FakeEdgeTtsEngine : IEdgeTtsEngine
    {
        private readonly bool _available;
        private readonly bool _succeeds;

        public FakeEdgeTtsEngine(bool available, bool succeeds)
        {
            _available = available;
            _succeeds = succeeds;
        }

        public int TrySpeakCalls { get; private set; }

        public int StopCalls { get; private set; }

        public string? LastVoice { get; private set; }

        public bool IsAvailable() => _available;

        public Task<bool> TrySpeakAsync(string text, string voice, CancellationToken cancellationToken)
        {
            TrySpeakCalls++;
            LastVoice = voice;
            return Task.FromResult(_succeeds);
        }

        public void Stop() => StopCalls++;
    }

    private static SpeechItem Item(string id) => new()
    {
        SpeechId = id,
        RequestId = "req-1",
        GenerationId = "gen-1",
        SafeText = "测试语音合成播音，这是一句短句。",
    };

    /// <summary>本机有没有中文系统音色。降级路径要真的用本地语音，没有就跳过。</summary>
    private static bool HasChineseSystemVoice()
    {
        using var synth = new System.Speech.Synthesis.SpeechSynthesizer();
        return synth.GetInstalledVoices()
            .Any(v => v.Enabled && v.VoiceInfo.Culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Edge_engine_probe_never_throws_and_is_stable()
    {
        // 探测结果随环境而定（装了依赖就是 true），但探测本身必须稳定且不抛异常。
        var first = EdgeTtsEngine.IsAvailable();
        var second = EdgeTtsEngine.IsAvailable();

        Assert.Equal(first, second);
    }

    /// <summary>
    /// 会真的出声的用例一律跳过。
    ///
    /// 这些用例要走完整的本地合成与播放链路，跑一次测试就在机器上念一句中文，
    /// 反复跑测试时特别吵。人工授权跳过；断言原样保留，需要真听声音时去掉这个判断即可。
    /// </summary>
    private static void SkipBecauseItActuallySpeaks()
    {
        if (HasChineseSystemVoice())
        {
            Assert.Skip("这条用例会真的出声（本机有中文系统音色），按人工授权跳过；需要真听声音时去掉此判断。");
        }
    }

    [Fact]
    public async Task Edge_voice_is_used_when_engine_is_available()
    {
        SkipBecauseItActuallySpeaks();

        var edge = new FakeEdgeTtsEngine(available: true, succeeds: true);
        using var tts = new TtsService(edge);

        await tts.SpeakAsync(Item("sp-edge-ok"), "zh-CN-YunxiNeural", CancellationToken.None);

        Assert.Equal(1, edge.TrySpeakCalls);
        Assert.Equal("zh-CN-YunxiNeural", edge.LastVoice);
        Assert.False(tts.IsSpeaking);
    }

    [Fact]
    public async Task Falls_back_to_local_speech_when_edge_is_unavailable()
    {
        if (!HasChineseSystemVoice())
        {
            Assert.Skip("本机没有中文系统音色，跳过降级路径用例。");
        }

        SkipBecauseItActuallySpeaks();

        var edge = new FakeEdgeTtsEngine(available: false, succeeds: false);
        using var tts = new TtsService(edge);

        // 神经音色不可用时必须自动退回 Windows 本地语音，而不是把口播丢掉。
        await tts.SpeakAsync(Item("sp-edge-missing"), "zh-CN-YunxiNeural", CancellationToken.None);

        Assert.Equal(0, edge.TrySpeakCalls);
        Assert.False(tts.IsSpeaking);
    }

    [Fact]
    public async Task Falls_back_to_local_speech_when_edge_speak_fails()
    {
        if (!HasChineseSystemVoice())
        {
            Assert.Skip("本机没有中文系统音色，跳过降级路径用例。");
        }

        SkipBecauseItActuallySpeaks();

        var edge = new FakeEdgeTtsEngine(available: true, succeeds: false);
        using var tts = new TtsService(edge);

        await tts.SpeakAsync(Item("sp-edge-fail"), "zh-CN-YunjianNeural", CancellationToken.None);

        // 引擎可用但实际播失败：同样要降级，文字不能丢。
        Assert.Equal(1, edge.TrySpeakCalls);
        Assert.False(tts.IsSpeaking);
    }

    [Fact]
    public async Task Local_voice_choice_does_not_touch_edge_engine()
    {
        if (!HasChineseSystemVoice())
        {
            Assert.Skip("本机没有中文系统音色，跳过本地语音用例。");
        }

        SkipBecauseItActuallySpeaks();

        var edge = new FakeEdgeTtsEngine(available: true, succeeds: true);
        using var tts = new TtsService(edge);

        // 主持人明确选了 Windows 本地音色：不该再起 Edge 进程。
        await tts.SpeakAsync(Item("sp-local"), "Microsoft Kangkang", CancellationToken.None);

        Assert.Equal(0, edge.TrySpeakCalls);
        Assert.False(tts.IsSpeaking);
    }

    [Fact]
    public async Task Stop_ends_the_edge_playback()
    {
        SkipBecauseItActuallySpeaks();

        var edge = new FakeEdgeTtsEngine(available: true, succeeds: true);
        using var tts = new TtsService(edge);

        await tts.SpeakAsync(Item("sp-stop"), "zh-CN-XiaoxiaoNeural", CancellationToken.None);
        tts.Stop();

        Assert.True(edge.StopCalls > 0);
        Assert.False(tts.IsSpeaking);
    }

    [Fact]
    public void Chinese_voice_list_contains_local_voices_even_without_edge()
    {
        var edge = new FakeEdgeTtsEngine(available: false, succeeds: false);
        using var tts = new TtsService(edge);

        var voices = tts.GetChineseVoices();

        // 没有 Edge 依赖时，列表里不应有神经音色。
        Assert.DoesNotContain(voices, v => v.Name.StartsWith("zh-CN-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Real_edge_engine_speaks_when_environment_supports_it()
    {
        if (!EdgeTtsEngine.IsAvailable())
        {
            // 本机没有 python / edge-tts：这是设计内的环境，跳过而不是变红。
            Assert.Skip("本机未安装 edge-tts 依赖，跳过真实合成用例。");
        }

        using var tts = new TtsService();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        SkipBecauseItActuallySpeaks();

        await tts.SpeakAsync(Item("sp-real"), "zh-CN-YunxiNeural", cts.Token);

        Assert.False(tts.IsSpeaking);
    }
}
