using System.IO;
using LocalDm.Core.Domain;
using LocalDm.Infrastructure.Storage;

namespace LocalDm.Tests;

/// <summary>
/// 设置的读写：新增的上下文长度要能落盘，旧设置文件没有这一项时按默认值处理。
/// </summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "localdm-settings-" + Guid.NewGuid().ToString("N"));

    public SettingsStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响断言结论
        }
    }

    [Fact]
    public async Task Context_window_tokens_round_trip_through_the_settings_file()
    {
        var store = NewStore();
        var settings = ToolSettings.Default with
        {
            ModelId = "deepseek-chat",
            ContextWindowTokens = 32_768,
        };

        await store.SaveAsync(settings, TestContext.Current.CancellationToken);
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(32_768, loaded.ContextWindowTokens);
        Assert.Equal("deepseek-chat", loaded.ModelId);
    }

    [Fact]
    public async Task Settings_file_without_the_field_falls_back_to_the_default()
    {
        // 旧设置文件没有这一项：不能让主持台因为读不到上下文长度就开不了局。
        var path = Path.Combine(_root, "settings.json");
        await File.WriteAllTextAsync(
            path,
            """{"MagpieBaseUri":"http://127.0.0.1:3425/","ModelId":"deepseek-chat"}""",
            TestContext.Current.CancellationToken);

        var loaded = NewStore().Load();

        Assert.Equal(ToolSettings.DefaultContextWindowTokens, loaded.ContextWindowTokens);
        Assert.Equal("deepseek-chat", loaded.ModelId);
    }

    [Fact]
    public void Context_window_outside_the_allowed_range_is_rejected()
    {
        var tooSmall = ToolSettings.Default with { ContextWindowTokens = 100 };
        var tooLarge = ToolSettings.Default with { ContextWindowTokens = 3_000_000 };

        Assert.Equal(
            DomainErrorCode.INVALID_INPUT,
            Assert.Throws<DomainRuleException>(() => tooSmall.Validate()).Code);
        Assert.Equal(
            DomainErrorCode.INVALID_INPUT,
            Assert.Throws<DomainRuleException>(() => tooLarge.Validate()).Code);
    }

    [Fact]
    public async Task Ui_scale_and_chat_width_round_trip_through_the_settings_file()
    {
        var store = NewStore();
        var settings = ToolSettings.Default with { UiScale = 1.5, ChatWidthMode = ChatWidthMode.Wide, LightTheme = true };

        await store.SaveAsync(settings, TestContext.Current.CancellationToken);
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1.5, loaded.UiScale);
        Assert.Equal(ChatWidthMode.Wide, loaded.ChatWidthMode);
        Assert.True(loaded.LightTheme);
    }

    [Fact]
    public async Task Settings_file_without_ui_fields_falls_back_to_defaults()
    {
        // 旧设置文件没有这两项：保持原来的观感（1.0 倍、标准宽度）。
        var path = Path.Combine(_root, "settings.json");
        await File.WriteAllTextAsync(
            path,
            """{"MagpieBaseUri":"http://127.0.0.1:3425/","ModelId":"deepseek-chat"}""",
            TestContext.Current.CancellationToken);

        var loaded = NewStore().Load();

        Assert.Equal(ToolSettings.DefaultUiScale, loaded.UiScale);
        Assert.Equal(ChatWidthMode.Standard, loaded.ChatWidthMode);
        Assert.False(loaded.LightTheme);
    }

    [Fact]
    public void Ui_scale_and_chat_width_outside_the_allowed_range_are_rejected()
    {
        foreach (var scale in new[] { 0.5, 2.5, double.NaN })
        {
            var settings = ToolSettings.Default with { UiScale = scale };
            Assert.Equal(
                DomainErrorCode.INVALID_INPUT,
                Assert.Throws<DomainRuleException>(() => settings.Validate()).Code);
        }

        var badMode = ToolSettings.Default with { ChatWidthMode = (ChatWidthMode)9 };
        Assert.Equal(
            DomainErrorCode.INVALID_INPUT,
            Assert.Throws<DomainRuleException>(() => badMode.Validate()).Code);
    }

    [Fact]
    public void Chat_width_tiers_map_to_the_agreed_widths()
    {
        Assert.Equal(980, ToolSettings.ChatMaxWidth(ChatWidthMode.Standard));
        Assert.Equal(1320, ToolSettings.ChatMaxWidth(ChatWidthMode.Wide));
        Assert.Null(ToolSettings.ChatMaxWidth(ChatWidthMode.Full));

        // 玩家气泡按同一比例放宽（标准档 620 / 980）。
        Assert.Equal(620, ToolSettings.ChatBubbleMaxWidth(ChatWidthMode.Standard));
        Assert.Equal(620 * 1320 / 980.0, ToolSettings.ChatBubbleMaxWidth(ChatWidthMode.Wide));
        Assert.Null(ToolSettings.ChatBubbleMaxWidth(ChatWidthMode.Full));
    }

    private SettingsStore NewStore() =>
        new(Path.Combine(_root, "settings.json"), Path.Combine(_root, "secrets"));
}
