using System.IO;
using LocalDm.App.Input;
using LocalDm.Core.Hosting;
using LocalDm.Infrastructure.Storage;

namespace LocalDm.Tests;

/// <summary>T050：回车发送、Shift+Enter 换行、发送只一次、状态意图只提案、DPAPI 不落明文。</summary>
public sealed class TextSubmissionTests
{
    [Fact]
    public void Plain_enter_sends_once()
    {
        Assert.True(HostInputController.ShouldSendOnEnterArgs(System.Windows.Input.Key.Enter, composing: false, shift: false));
    }

    [Fact]
    public void Shift_enter_inserts_newline()
    {
        Assert.False(HostInputController.ShouldSendOnEnterArgs(System.Windows.Input.Key.Enter, composing: false, shift: true));
    }

    [Fact]
    public void Ime_composition_never_sends_on_enter()
    {
        Assert.False(HostInputController.ShouldSendOnEnterArgs(System.Windows.Input.Key.Enter, composing: true, shift: false));
    }

    [Fact]
    public void Other_keys_never_send()
    {
        Assert.False(HostInputController.ShouldSendOnEnterArgs(System.Windows.Input.Key.A, composing: false, shift: false));
    }

    [Fact]
    public void State_intent_is_only_a_proposal()
    {
        var controller = new HostInputController(new HostIntentRouter());

        var intent = controller.ResolveIntent("请进入下一环节");

        Assert.Equal(HostIntentKind.ProposeCommand, intent.Kind);
        Assert.NotNull(intent.ProposedAction);
    }

    [Fact]
    public void Normal_question_is_a_public_request()
    {
        var controller = new HostInputController(new HostIntentRouter());

        var intent = controller.ResolveIntent("这间房间有什么线索");

        Assert.Equal(HostIntentKind.PublicQuestion, intent.Kind);
    }

    [Fact]
    public async Task Caller_key_is_never_written_in_plain_text()
    {
        var root = Path.Combine(Path.GetTempPath(), "localdm-secrets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            var store = new SettingsStore(settingsPath, Path.Combine(root, "secrets"));
            const string plainKey = "sk-plain-text-should-never-appear-1234567890";

            var keyRef = store.SaveCallerKey(plainKey);
            await store.SaveAsync(
                new LocalDm.Core.Domain.ToolSettings
                {
                    MagpieBaseUri = LocalDm.Core.Domain.ToolSettings.DefaultMagpieUri,
                    CallerKeyRef = keyRef,
                },
                TestContext.Current.CancellationToken);

            Assert.False(SettingsStore.IsSecretMaterialPersistedAsPlainText(settingsPath, plainKey));
            var saved = await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(plainKey, saved, StringComparison.Ordinal);

            // 密文可用 DPAPI 还原，但只在内存中
            Assert.Equal(plainKey, store.LoadCallerKey(keyRef));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
