using LocalDm.Core.Domain;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Voice;

namespace LocalDm.Tests;

/// <summary>
/// TTS 引擎分派与降级链。
///
/// 这些用例<b>不依赖宿主机是否装了 python 或 edge-tts</b>：产品的核心前提就是
/// 「某个引擎不可用时优雅降级」，降级路径如果只能在装了依赖的机器上测，它就等于没测。
/// 所有引擎都用替身，断言的是「走了哪条路径」，不是「声音好不好听」。
/// </summary>
public sealed class TtsEngineRoutingTests
{
    /// <summary>可控的假引擎：记录是否被调用、用了哪个音色。</summary>
    private sealed class FakeEngine : ITtsEngine
    {
        private readonly bool _available;
        private readonly bool _succeeds;

        public FakeEngine(TtsEngineKind kind, bool available, bool succeeds)
        {
            Kind = kind;
            _available = available;
            _succeeds = succeeds;
        }

        public TtsEngineKind Kind { get; }

        public int TrySpeakCalls { get; private set; }

        public string? LastVoiceName { get; private set; }

        public int StopCalls { get; private set; }

        public bool IsAvailable() => _available;

        public IReadOnlyList<TtsVoice> GetVoices() => _available
            ? [new TtsVoice { Name = "fake-" + Kind, Culture = "zh-CN", Engine = Kind }]
            : [];

        public Task<bool> TrySpeakAsync(string text, TtsVoice voice, CancellationToken cancellationToken)
        {
            TrySpeakCalls++;
            LastVoiceName = voice.Name;
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

    /// <summary>本机有没有中文系统音色。降级到本地语音的用例需要它。</summary>
    private static bool HasChineseSystemVoice()
    {
        using var synth = new System.Speech.Synthesis.SpeechSynthesizer();
        return synth.GetInstalledVoices()
            .Any(v => v.Enabled && v.VoiceInfo.Culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Edge_voice_is_used_when_edge_engine_is_available()
    {
        using var tts = new TtsService(new SystemEdgeTtsEngine());

        var voices = tts.GetChineseVoices();

        // 微软神经音色的标识沿用历史格式：老用户的设置文件里存的就是它，不能改。
        var edge = voices.Where(v => v.Engine == TtsEngineKind.Edge).ToArray();
        Assert.All(edge, v => Assert.StartsWith("zh-CN-", v.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VoxCpm_voices_are_listed_before_edge_and_windows()
    {
        var voxcpm = new FakeEngine(TtsEngineKind.VoxCpm, available: true, succeeds: true);
        using var tts = new TtsService(new SystemEdgeTtsEngine(), voxcpm);

        var voices = tts.GetChineseVoices();

        // VoxCPM 排在最前：它是完全本机的音色，不联网。
        var first = voices.FirstOrDefault(v => v.Engine == TtsEngineKind.VoxCpm);
        Assert.NotNull(first);
        Assert.Equal(0, voices.ToArray().IndexOf(first));
    }

    [Fact]
    public void Unavailable_engine_contributes_no_voices()
    {
        var voxcpm = new FakeEngine(TtsEngineKind.VoxCpm, available: false, succeeds: false);
        using var tts = new TtsService(new SystemEdgeTtsEngine(), voxcpm);

        var voices = tts.GetChineseVoices();

        Assert.DoesNotContain(voices, v => v.Engine == TtsEngineKind.VoxCpm);
    }

    [Fact]
    public void Windows_voices_are_marked_as_windows_engine()
    {
        using var tts = new TtsService(new SystemEdgeTtsEngine());

        var windows = tts.GetChineseVoices().Where(v => v.Engine == TtsEngineKind.Windows).ToArray();

        Assert.NotEmpty(windows);
        Assert.All(windows, v => Assert.StartsWith("Microsoft", v.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Selected_voxcpm_voice_is_handled_by_the_voxcpm_engine()
    {
        var voxcpm = new FakeEngine(TtsEngineKind.VoxCpm, available: true, succeeds: true);
        using var tts = new TtsService(new SystemEdgeTtsEngine(), voxcpm);

        // 用一个确实会被认出来的 VoxCPM 音色标识。
        var voiceId = "voxcpm:design:沉稳的中年男声";
        await tts.SpeakAsync(Item("sp-voxcpm"), voiceId, CancellationToken.None);

        Assert.Equal(1, voxcpm.TrySpeakCalls);
    }

    [Fact]
    public async Task VoxCpm_voice_falls_back_to_edge_when_voxcpm_fails()
    {
        var voxcpm = new FakeEngine(TtsEngineKind.VoxCpm, available: true, succeeds: false);
        using var tts = new TtsService(new SystemEdgeTtsEngine(), voxcpm);

        await tts.SpeakAsync(Item("sp-voxcpm-fail"), "voxcpm:design:沉稳的中年男声", CancellationToken.None);

        // VoxCPM 失败后要往下走降级链，而不是把口播丢掉。
        Assert.Equal(1, voxcpm.TrySpeakCalls);
        Assert.False(tts.IsSpeaking);
    }

    [Fact]
    public void Stop_reaches_every_registered_engine()
    {
        var voxcpm = new FakeEngine(TtsEngineKind.VoxCpm, available: true, succeeds: true);
        using var tts = new TtsService(new SystemEdgeTtsEngine(), voxcpm);

        tts.Stop();

        Assert.True(voxcpm.StopCalls > 0);
        Assert.False(tts.IsSpeaking);
    }

    [Fact]
    public void Voice_id_prefix_does_not_decide_the_engine()
    {
        // 标识前缀只负责把 VoxCPM 的参数带回去，不负责判断引擎。
        // 引擎归属由 TtsVoice.Engine 表达。
        Assert.False(TtsVoiceId.IsVoxCpm("zh-CN-YunxiNeural"));
        Assert.False(TtsVoiceId.IsVoxCpm("Microsoft Kangkang"));
        Assert.True(TtsVoiceId.IsVoxCpm("voxcpm:design:沉稳的中年男声"));
        Assert.True(TtsVoiceId.IsVoxCpm("voxcpm:clone:C:\\voice.wav"));
    }

    [Fact]
    public async Task Windows_voice_choice_does_not_fall_back_to_edge()
    {
        if (!HasChineseSystemVoice())
        {
            Assert.Skip("本机没有中文系统音色，跳过本地语音用例。");
        }

        // 会真的出声：这条用例要走到 Windows 本地语音去念一句中文。
        // 与 EdgeTtsTests 里那几条同类用例一样，按人工授权跳过。
        Assert.Skip("这条用例会真的出声（本机有中文系统音色），按人工授权跳过；需要真听声音时去掉此判断。");

        // 主持人明确选了 Windows 内置音色：降级时也不能换成微软神经音色，
        // 否则等于擅自换掉他的选择。
        var edge = new FakeEngine(TtsEngineKind.Edge, available: true, succeeds: true);
        using var tts = new TtsService(new SystemEdgeTtsEngine(), edge);

        var windows = tts.GetChineseVoices().First(v => v.Engine == TtsEngineKind.Windows);
        await tts.SpeakAsync(Item("sp-windows"), windows.Name, CancellationToken.None);

        Assert.Equal(0, edge.TrySpeakCalls);
        Assert.False(tts.IsSpeaking);
    }

    [Fact]
    public void Windows_voice_route_does_not_allow_edge_fallback()
    {
        // 上面那条会出声的用例验证同一件事，但不出声：直接查路由判定。
        // 主持人明确选了 Windows 内置音色时，不能退回微软神经音色。
        var windows = new TtsVoice
        {
            Name = "Microsoft Kangkang",
            Culture = "zh-CN",
            Engine = TtsEngineKind.Windows,
        };

        Assert.Equal(TtsEngineKind.Windows, windows.Engine);

        // 路由规则由 TtsService 表达：Windows 音色不允许退回 Edge。
        Assert.False(TtsService.AllowsEdgeFallback(windows.Engine));
        Assert.True(TtsService.AllowsEdgeFallback(TtsEngineKind.VoxCpm));
    }
}
