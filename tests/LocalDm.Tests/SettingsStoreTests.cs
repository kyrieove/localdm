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

    private SettingsStore NewStore() =>
        new(Path.Combine(_root, "settings.json"), Path.Combine(_root, "secrets"));
}
