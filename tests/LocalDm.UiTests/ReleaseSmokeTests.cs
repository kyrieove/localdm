using System.IO;
using LocalDm.App;
using LocalDm.App.ViewModels;
using LocalDm.Core.Domain;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Storage;

namespace LocalDm.UiTests;

/// <summary>
/// T056：在真实 WPF 应用里走通完整一局。
/// 使用真实 MainWindow、真实 ViewModel、真实 SQLite 存档与真实组合根；
/// 只把外部「模型」和「扬声器」换成确定性测试实现（它们不是本任务要验证的部分）。
/// 断言的是界面状态，而不是命令返回值。
/// </summary>
public sealed class ReleaseSmokeTests
{
    [Fact]
    public async Task Real_window_walks_full_session_and_reveal_is_gated()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            using var composition = await AppComposition.CreateFromContextAsync(
                await SessionContext.OpenAsync(root.Path, CancellationToken.None),
                CancellationToken.None);

            var shell = await composition.CreateShellAsync(CancellationToken.None);
            var window = new MainWindow { DataContext = shell, ShowActivated = false };
            window.Show();
            try
            {
                shell.ShowHostCommand.Execute(null);
                Assert.IsType<HostViewModel>(shell.Current);

                var host = shell.Host;
                await host.LoadDemoAsync();
                var session = host.Session;

                Assert.Equal("Prepared", session.SessionStateText);
                Assert.Contains("原本条件", string.Join(' ', session.PhaseConditions), StringComparison.Ordinal);

                // 没有勾选框：点「开始这一局」就是确认，一下点到 Playing。
                Assert.True(session.IsStartVisible, "待开局时应给出开始动作。");
                await session.ExecuteAsync(GameCommandType.Start, null, null);
                Assert.Equal("Playing", session.SessionStateText);
                Assert.Equal(1, session.Revision);

                Assert.Equal("进入搜证", session.AdvanceLabel);
                await session.ExecuteAsync(GameCommandType.Advance, null, null);
                Assert.Equal("搜证", session.PhaseTitle);

                // 本环节两份材料必须逐个发完才能推进：这是机器条件，不是勾选。
                Assert.Equal("发放材料 线索包一", session.ReleaseLabel);
                await session.ExecuteAsync(GameCommandType.ReleaseMaterials, ["线索包一"], null);
                Assert.Contains("线索包一", session.ReleasedMaterials);

                await session.ExecuteAsync(GameCommandType.Advance, null, null);
                Assert.Equal("搜证", session.PhaseTitle);
                Assert.Contains("没有发放", session.Status, StringComparison.Ordinal);

                await session.ExecuteAsync(GameCommandType.ReleaseMaterials, ["线索包二"], null);
                await session.ExecuteAsync(GameCommandType.Advance, null, null);
                Assert.Equal("讨论", session.PhaseTitle);

                await session.ExecuteAsync(GameCommandType.Advance, null, null);
                Assert.Equal("投票", session.PhaseTitle);

                // 投票入口只在投票环节出现，且点「确认提交汇总」即确认
                Assert.True(session.IsVoteVisible, "投票环节应给出录入入口。");
                Assert.True(SessionViewModel.TryParseVotes("A=3, B=2", out var votes, out _));
                await session.ExecuteAsync(GameCommandType.SubmitVoteSummary, null, votes);
                Assert.Equal(5, session.Projection!.VoteSummary!.TotalVotes);
                Assert.False(session.IsVoteVisible, "汇总生效后不再重复录入。");

                await session.ExecuteAsync(GameCommandType.Advance, null, null);
                Assert.Equal("真相", session.PhaseTitle);
                Assert.Null(session.Projection.RevealedTexts);

                // 揭秘也是点一下：不再勾「确认这是公开揭秘」
                Assert.True(session.IsRevealVisible, "真相环节应给出公开动作。");
                await session.ExecuteAsync(GameCommandType.Reveal, null, null);
                Assert.Equal("Revealed", session.SessionStateText);
                Assert.NotNull(session.Projection.RevealedTexts);

                await session.ExecuteAsync(GameCommandType.Complete, null, null);
                Assert.Equal("Completed", session.SessionStateText);

                // 恢复后不自动播音
                await host.ResumeAsync();
                Assert.Equal(SpeechState.Idle, host.SpeechState);
                Assert.Equal("Completed", host.Session.SessionStateText);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Session_survives_application_restart_in_the_same_database()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            var dbPath = Path.Combine(root.Path, "sessions.db");

            using (var store = new SessionStore(dbPath))
            {
                await store.InitializeAsync(CancellationToken.None);
                var host = NewHost(store);
                await host.LoadDemoAsync();
                await host.Session.ExecuteAsync(GameCommandType.Start, null, null);
                Assert.Equal(1, host.Session.Revision);
            }

            // 强退：清空连接池后重新打开
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            using (var reopened = new SessionStore(dbPath))
            {
                await reopened.InitializeAsync(CancellationToken.None);
                var host = NewHost(reopened);
                await host.ResumeAsync();

                Assert.Equal(1, host.Session.Revision);
                Assert.Equal("Playing", host.Session.SessionStateText);
                Assert.Equal(SpeechState.Idle, host.SpeechState);
            }
        });
    }

    private static HostViewModel NewHost(ISessionStore store)
    {
        var tts = new SilentTts();
        return new HostViewModel(
            new LocalDm.Core.Hosting.HostDirector(new EchoGateway(), tts),
            () => ToolSettings.Default with { ModelId = "test-model" },
            new LocalDm.Core.Hosting.HostIntentRouter(),
            new LocalDm.Core.Game.CommandDispatcher(store),
            store);
    }

    private sealed class EchoGateway : LocalDm.Core.Ports.IMagpieGateway
    {
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["test-model"]);

        public Task<string> CompleteAsync(
            LocalDm.Core.Ports.MagpieCompletionRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult("这是测试用的公开主持文字。");
    }

    private sealed class SilentTts : LocalDm.Core.Ports.ITtsService
    {
        public bool IsSpeaking => false;

        public IReadOnlyList<LocalDm.Core.Ports.TtsVoice> GetChineseVoices() =>
            [new LocalDm.Core.Ports.TtsVoice { Name = "测试音色", Culture = "zh-CN" }];

        public Task SpeakAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ReplayAsync(SpeechItem item, string? voiceId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void Stop()
        {
        }
    }

    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "localdm-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
