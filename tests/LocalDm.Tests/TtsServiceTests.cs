using System.IO;
using System.Speech.Synthesis;
using LocalDm.Core.Domain;
using LocalDm.Infrastructure.Storage;
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
    public void Enumerable_voices_include_system_speech()
    {
        using var synth = new SpeechSynthesizer();
        var expected = synth.GetInstalledVoices()
            .Where(v => v.Enabled && v.VoiceInfo.Culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            .Select(v => v.VoiceInfo.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        using var tts = new TtsService();
        var actual = tts.GetChineseVoices().Select(v => v.Name).ToArray();

        Assert.All(expected, e => Assert.Contains(e, actual));
    }

    [Fact]
    public void Voice_list_keeps_edge_first_and_prefers_kangkang_over_yaoyao()
    {
        using var tts = new TtsService();
        var names = tts.GetChineseVoices().Select(v => v.Name).ToArray();

        // 神经音色排在最前：它们是默认选择。
        var edgeVoices = names.Where(n => n.StartsWith("zh-CN-", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (edgeVoices.Length > 0)
        {
            Assert.Equal(0, Array.IndexOf(names, edgeVoices[0]));
        }

        // 本地音色里 Kangkang 排在 Yaoyao 之前（沉稳男声优先）。
        var kangkang = Array.FindIndex(names, n => n.Contains("Kangkang", StringComparison.OrdinalIgnoreCase));
        var yaoyao = Array.FindIndex(names, n => n.Contains("Yaoyao", StringComparison.OrdinalIgnoreCase));
        if (kangkang >= 0 && yaoyao >= 0)
        {
            Assert.True(kangkang < yaoyao, "康康应当排在瑶瑶之前。");
        }
    }

    [Fact]
    public async Task Settings_store_round_trips_voice_and_hint_through_both_save_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "localdm-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            var store = new SettingsStore(settingsPath, Path.Combine(root, "secrets"));

            // 同步路径（主持台切音色）
            store.Save(new LocalDm.Core.Domain.ToolSettings
            {
                MagpieBaseUri = LocalDm.Core.Domain.ToolSettings.DefaultMagpieUri,
                VoiceId = "zh-CN-YunxiNeural",
                InputHint = "提示语",
            });

            // 异步路径（设置页保存）读回同一个文件：字段不能因为两套序列化而丢失。
            var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
            Assert.Equal("zh-CN-YunxiNeural", loaded.VoiceId);
            Assert.Equal("提示语", loaded.InputHint);

            await store.SaveAsync(loaded with { VoiceId = "Microsoft Kangkang" }, TestContext.Current.CancellationToken);
            var reread = store.Load();
            Assert.Equal("Microsoft Kangkang", reread.VoiceId);
            Assert.Equal("提示语", reread.InputHint);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Corrupted_settings_file_falls_back_to_defaults_instead_of_crashing()
    {
        var root = Path.Combine(Path.GetTempPath(), "localdm-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            File.WriteAllText(settingsPath, "{ this is not json ");
            var store = new SettingsStore(settingsPath, Path.Combine(root, "secrets"));

            var loaded = store.Load();

            Assert.Equal(LocalDm.Core.Domain.ToolSettings.Default.MagpieBaseUri, loaded.MagpieBaseUri);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
