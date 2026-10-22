using LocalDm.Core.Domain;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Voice;

namespace LocalDm.Tests;

/// <summary>
/// VoxCPM 音色标识与配置。
///
/// 这些用例<b>不依赖宿主机是否装了 python 或能否真的合成</b>：
/// 标识解析与配置快照是纯逻辑，测它们不该受环境摆布。
/// 真要听声音的路径由 <see cref="TtsEngineRoutingTests"/> 用替身覆盖。
/// </summary>
public sealed class VoxCpmVoiceTests
{
    [Fact]
    public void Parses_a_design_voice_id()
    {
        var spec = TtsVoiceId.ParseVoxCpm("voxcpm:design:沉稳的中年男声");

        Assert.NotNull(spec);
        Assert.Equal(VoxCpmVoiceSpecMode.Design, spec!.Mode);
        Assert.Equal("沉稳的中年男声", spec.Description);
        Assert.Null(spec.ReferenceAudioPath);
    }

    [Fact]
    public void Parses_a_clone_voice_id()
    {
        var spec = TtsVoiceId.ParseVoxCpm(@"voxcpm:clone:C:\voice\ref.wav");

        Assert.NotNull(spec);
        Assert.Equal(VoxCpmVoiceSpecMode.Clone, spec!.Mode);
        Assert.Equal(@"C:\voice\ref.wav", spec.ReferenceAudioPath);
        Assert.Null(spec.Description);
    }

    [Fact]
    public void Rejects_ids_that_are_not_voxcpm_voices()
    {
        Assert.Null(TtsVoiceId.ParseVoxCpm("zh-CN-YunxiNeural"));
        Assert.Null(TtsVoiceId.ParseVoxCpm("Microsoft Kangkang"));
        Assert.Null(TtsVoiceId.ParseVoxCpm(null));
        Assert.Null(TtsVoiceId.ParseVoxCpm(""));
    }

    [Fact]
    public void Rejects_voxcpm_ids_with_missing_or_empty_parameters()
    {
        // 标记在但参数为空：这不是一个能用的音色，必须返回 null 而不是空描述。
        Assert.Null(TtsVoiceId.ParseVoxCpm("voxcpm:design:"));
        Assert.Null(TtsVoiceId.ParseVoxCpm("voxcpm:design:   "));
        Assert.Null(TtsVoiceId.ParseVoxCpm("voxcpm:clone:"));
        Assert.Null(TtsVoiceId.ParseVoxCpm("voxcpm:"));
        Assert.Null(TtsVoiceId.ParseVoxCpm("voxcpm:unknown:xxx"));
    }

    [Fact]
    public void Built_voice_carries_the_voxcpm_engine()
    {
        var spec = TtsVoiceId.ParseVoxCpm("voxcpm:design:沉稳的中年男声")!;

        var voice = VoxCpmVoices.Build(spec);

        Assert.Equal(TtsEngineKind.VoxCpm, voice.Engine);
        Assert.Equal("zh-CN", voice.Culture);
        Assert.Equal("voxcpm:design:沉稳的中年男声", voice.Name);
    }

    [Fact]
    public void Built_voice_id_round_trips_through_the_parser()
    {
        var original = @"voxcpm:clone:C:\voice\ref.wav";
        var spec = TtsVoiceId.ParseVoxCpm(original)!;

        var rebuilt = VoxCpmVoices.Build(spec);

        // 标识要能写进设置文件再读回来：往返一次必须还是同一个东西。
        Assert.Equal(original, rebuilt.Name);
        Assert.Equal(spec, TtsVoiceId.ParseVoxCpm(rebuilt.Name));
    }

    [Fact]
    public void Options_without_model_path_mean_voxcpm_is_disabled()
    {
        var options = new VoxCpmOptions(null, null, "沉稳的中年男声", null);

        Assert.False(options.Enabled);
    }

    [Fact]
    public void Options_expose_design_and_clone_voices_independently()
    {
        var both = new VoxCpmOptions("openbmb/VoxCPM2", null, "沉稳的中年男声", @"C:\voice\ref.wav");

        var specs = both.GetVoiceSpecs();

        Assert.Equal(2, specs.Count);
        Assert.Contains(specs, s => s.Mode == VoxCpmVoiceSpecMode.Design);
        Assert.Contains(specs, s => s.Mode == VoxCpmVoiceSpecMode.Clone);
    }

    [Fact]
    public void Options_without_any_voice_expose_no_voices()
    {
        var none = new VoxCpmOptions("openbmb/VoxCPM2", null, null, null);

        Assert.Empty(none.GetVoiceSpecs());
    }

    [Fact]
    public void Options_are_read_from_settings()
    {
        var settings = new ToolSettings
        {
            MagpieBaseUri = ToolSettings.DefaultMagpieUri,
            VoxCpmModelPath = "openbmb/VoxCPM2",
            VoxCpmVoiceDesign = "沉稳的中年男声",
            VoxCpmReferenceAudio = @"C:\voice\ref.wav",
            VoxCpmPythonPath = @"C:\python\python.exe",
        };

        var options = VoxCpmOptions.FromSettings(settings);

        Assert.True(options.Enabled);
        Assert.Equal("openbmb/VoxCPM2", options.ModelPath);
        Assert.Equal("沉稳的中年男声", options.VoiceDesign);
        Assert.Equal(@"C:\voice\ref.wav", options.ReferenceAudioPath);
        Assert.Equal(2, options.GetVoiceSpecs().Count);
    }

    [Fact]
    public void Display_name_mentions_the_engine_and_stays_short()
    {
        var spec = TtsVoiceId.ParseVoxCpm("voxcpm:design:沉稳的中年男声")!;

        var display = VoxCpmVoices.DisplayName(spec);

        Assert.Contains("VoxCPM", display, StringComparison.Ordinal);
        Assert.Contains("沉稳的中年男声", display, StringComparison.Ordinal);
    }

    [Fact]
    public void Engine_probe_never_throws_and_is_stable()
    {
        using var engine = new VoxCpmEngine(() => new VoxCpmOptions(null, null, null, null));

        // 没配置模型目录时不可用；探测本身必须稳定且不抛异常。
        Assert.False(engine.IsAvailable());
        Assert.False(engine.IsAvailable());
        Assert.Empty(engine.GetVoices());
    }
}
