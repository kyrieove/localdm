using System.Speech.Synthesis;
using LocalDm.Core.Domain;
using LocalDm.Infrastructure.Voice;

namespace LocalDm.Tests;

/// <summary>
/// T019：真实 System.Speech 声音枚举与失败上报。
/// 只验证本机已安装声音与错误映射；实际扬声器可听性属于 US1 实机证据。
/// </summary>
public sealed class TtsServiceTests
{
    [Fact]
    public void Enumerates_installed_chinese_voices_on_this_machine()
    {
        using var tts = new TtsService();
        var voices = tts.GetChineseVoices();

        Assert.NotEmpty(voices);
        Assert.All(voices, v => Assert.StartsWith("zh", v.Culture, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Enumerable_voices_match_system_speech()
    {
        using var synth = new SpeechSynthesizer();
        var expected = synth.GetInstalledVoices()
            .Where(v => v.Enabled && v.VoiceInfo.Culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            .Select(v => v.VoiceInfo.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        using var tts = new TtsService();
        var actual = tts.GetChineseVoices().Select(v => v.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Empty_text_is_rejected_before_playback()
    {
        using var tts = new TtsService();
        var item = new SpeechItem
        {
            SpeechId = "s1",
            RequestId = "r1",
            GenerationId = "g1",
            SafeText = " ",
        };

        Assert.Throws<DomainRuleException>(() => item.Validate());
    }

    [Fact]
    public async Task Stop_before_speak_does_not_throw()
    {
        using var tts = new TtsService();
        tts.Stop();
        await Task.CompletedTask;
        Assert.False(tts.IsSpeaking);
    }
}
