using System.IO;
using LocalDm.Core.Domain;
using LocalDm.Core.Game;
using LocalDm.Core.Hosting;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace LocalDm.Tests;

/// <summary>
/// T063 故障矩阵：模型断线、TTS 故障、强退与重启。
/// 断言的是「已提交进度不丢失、恢复不自动播音」，不是界面观感。
/// </summary>
public sealed class FaultMatrixTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "localdm-fault-" + Guid.NewGuid().ToString("N"));
    private readonly ScriptPackage _package = SyntheticPackage.Create();

    public FaultMatrixTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Model_offline_does_not_change_committed_progress()
    {
        var store = new SessionStore(Path.Combine(_root, "sessions.db"));
        using var _ = store;
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        await store.SaveSessionAsync(SyntheticPackage.NewSession(), TestContext.Current.CancellationToken);
        var dispatcher = new CommandDispatcher(store);
        await dispatcher.DispatchAsync(
            SyntheticPackage.Command(GameCommandType.Start, 0, "op-start", groupReady: true),
            _package,
            TestContext.Current.CancellationToken);

        var tts = new ThrowingTts(throwOnSpeak: false);
        var director = new HostDirector(new OfflineGateway(), tts);
        var turn = await director.SpeakPublicAsync("model", "合成输入", null, TestContext.Current.CancellationToken);

        Assert.Equal(SpeechState.Error, turn.State);
        Assert.NotNull(turn.PublicReason);
        Assert.Empty(tts.Spoken);

        var session = await store.LoadSessionAsync("session-1", TestContext.Current.CancellationToken);
        Assert.Equal(1, session!.Revision);
        Assert.Equal(SessionState.Playing, session.State);
    }

    [Fact]
    public async Task Tts_failure_keeps_text_and_does_not_touch_progress()
    {
        var store = new SessionStore(Path.Combine(_root, "sessions.db"));
        using var _ = store;
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        await store.SaveSessionAsync(SyntheticPackage.NewSession(), TestContext.Current.CancellationToken);
        var dispatcher = new CommandDispatcher(store);
        await dispatcher.DispatchAsync(
            SyntheticPackage.Command(GameCommandType.Start, 0, "op-start", groupReady: true),
            _package,
            TestContext.Current.CancellationToken);

        var director = new HostDirector(new EchoGateway(), new ThrowingTts(throwOnSpeak: true));
        var turn = await director.SpeakPublicAsync("model", "合成输入", null, TestContext.Current.CancellationToken);

        Assert.Equal(SpeechState.Error, turn.State);
        Assert.Contains("文字已保留", turn.PublicReason!, StringComparison.Ordinal);

        var session = await store.LoadSessionAsync("session-1", TestContext.Current.CancellationToken);
        Assert.Equal(1, session!.Revision);
        Assert.Single(await store.ReadEventsAsync("session-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Hard_exit_after_commit_restores_last_committed_state_without_auto_speech()
    {
        var dbPath = Path.Combine(_root, "sessions.db");

        using (var store = new SessionStore(dbPath))
        {
            await store.InitializeAsync(TestContext.Current.CancellationToken);
            await store.SaveSessionAsync(SyntheticPackage.NewSession(), TestContext.Current.CancellationToken);
            var dispatcher = new CommandDispatcher(store);

            await dispatcher.DispatchAsync(
                SyntheticPackage.Command(GameCommandType.Start, 0, "op-start", groupReady: true),
                _package,
                TestContext.Current.CancellationToken);
            await dispatcher.DispatchAsync(
                SyntheticPackage.Command(GameCommandType.Advance, 1, "op-adv-1", groupReady: true),
                _package,
                TestContext.Current.CancellationToken);
            await dispatcher.DispatchAsync(
                SyntheticPackage.Command(GameCommandType.Advance, 2, "op-adv-2", groupReady: true),
                _package,
                TestContext.Current.CancellationToken);
        }

        SqliteConnection.ClearAllPools();

        using var reopened = new SessionStore(dbPath);
        var tts = new ThrowingTts(throwOnSpeak: true);
        var director = new HostDirector(new EchoGateway(), tts);

        var result = await new SessionRecovery(reopened).RecoverAsync(_package, "session-1", TestContext.Current.CancellationToken);

        Assert.True(result.Recovered);
        Assert.Equal(3, result.Session!.Revision);
        Assert.Equal(SpeechState.Idle, director.State);
        Assert.Equal(SpeechState.Idle, SessionRecovery.RestoredSpeechState);
        Assert.Empty(tts.Spoken);
    }

    [Fact]
    public async Task Crash_during_commit_never_loses_already_committed_history()
    {
        var dbPath = Path.Combine(_root, "sessions.db");
        using var store = new SessionStore(dbPath);
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        await store.SaveSessionAsync(SyntheticPackage.NewSession(), TestContext.Current.CancellationToken);
        var dispatcher = new CommandDispatcher(store);

        await dispatcher.DispatchAsync(
            SyntheticPackage.Command(GameCommandType.Start, 0, "op-1", groupReady: true),
            _package,
            TestContext.Current.CancellationToken);

        store.FaultInjector = point =>
        {
            if (point == TransactionFaultPoint.AfterEventWrite)
            {
                throw new InvalidOperationException("simulated crash");
            }
        };
        var crashed = await dispatcher.DispatchAsync(
            SyntheticPackage.Command(GameCommandType.Advance, 1, "op-2", groupReady: true),
            _package,
            TestContext.Current.CancellationToken);
        store.FaultInjector = null;

        Assert.Equal(DispatchOutcome.Rejected, crashed.Outcome);
        var session = await store.LoadSessionAsync("session-1", TestContext.Current.CancellationToken);
        Assert.Equal(1, session!.Revision);
        Assert.Single(await store.ReadEventsAsync("session-1", TestContext.Current.CancellationToken));
    }

    private sealed class OfflineGateway : IMagpieGateway
    {
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken) =>
            throw new DomainRuleException(DomainErrorCode.MODEL_FAILED, "无法连接本机 Magpie。");

        public Task<string> CompleteAsync(MagpieCompletionRequest request, CancellationToken cancellationToken) =>
            throw new DomainRuleException(DomainErrorCode.MODEL_FAILED, "无法连接本机 Magpie。");
    }

    private sealed class EchoGateway : IMagpieGateway
    {
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["model"]);

        public Task<string> CompleteAsync(MagpieCompletionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult("合成公开主持文字。");
    }

    private sealed class ThrowingTts : ITtsService
    {
        private readonly bool _throwOnSpeak;

        public ThrowingTts(bool throwOnSpeak) => _throwOnSpeak = throwOnSpeak;

        public List<SpeechItem> Spoken { get; } = [];

        public bool IsSpeaking => false;

        public IReadOnlyList<TtsVoice> GetChineseVoices() =>
            [new TtsVoice { Name = "合成音色", Culture = "zh-CN" }];

        public Task SpeakAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken)
        {
            if (_throwOnSpeak)
            {
                throw new DomainRuleException(DomainErrorCode.TTS_UNAVAILABLE, "本机没有可用的中文声音，文字已保留。");
            }

            Spoken.Add(item);
            return Task.CompletedTask;
        }

        public Task ReplayAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken) =>
            SpeakAsync(item, voiceId, cancellationToken);

        public void Stop()
        {
        }
    }
}
