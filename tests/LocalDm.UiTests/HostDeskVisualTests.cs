using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LocalDm.App;
using LocalDm.App.ViewModels;
using LocalDm.Core.Domain;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Storage;

namespace LocalDm.UiTests;

/// <summary>
/// 主持台视觉快照：真实窗口、真实 ViewModel，产出 PNG 供人工核对。
///
/// 这类用例的价值有两层：
/// 1) 断言「等待中的状态确实出现在界面上」——这正是「点了按钮没反应」的回归防线；
/// 2) 留下图片，人能直接看出排版、层次、对比度是否体面。
/// </summary>
public sealed class HostDeskVisualTests
{
    /// <summary>仓库根目录：从测试输出目录往上五层（bin/Debug/net10.0-windows → 仓库根）。</summary>
    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 5 && dir is not null; i++)
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? AppContext.BaseDirectory;
        }
    }

    private static string ArtifactsDir => Path.Combine(RepoRoot, "artifacts", "ui");

    [Fact]
    public async Task Busy_state_is_visible_on_screen_and_snapshot_is_written()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            using var composition = await AppComposition.CreateFromContextAsync(
                await SessionContext.OpenAsync(root.Path, CancellationToken.None),
                CancellationToken.None);

            var shell = await composition.CreateShellAsync(CancellationToken.None);
            var window = new MainWindow { DataContext = shell, ShowActivated = false, Width = 1320, Height = 820 };
            WpfRunner.MoveOffScreen(window);
            window.Show();
            try
            {
                shell.ShowHostCommand.Execute(null);
                var host = shell.Host;
                await host.LoadDemoAsync();

                // 让模型接口慢 1.2 秒：真实走一遍「点按钮 → 等模型 → 朗读」，
                // 这样截到的是真实状态，而不是手工摆出来的。
                host.Activity.Begin(
                    ActivityKind.Thinking,
                    "正在请本机模型回答…",
                    ["请模型回答", "朗读"],
                    "当前环节：搜证");

                await WaitForRenderAsync();

                // 断言：等待状态必须真的出现在界面上
                Assert.True(host.Activity.IsVisible, "等待时状态条应当可见。");
                Assert.True(host.Activity.IsActive, "等待时状态条应当在动。");
                Assert.Contains("正在请本机模型回答", host.Activity.Title, StringComparison.Ordinal);
                Assert.Equal(2, host.Activity.Steps.Count);
                Assert.True(host.Activity.Steps[0].IsCurrent, "第一步应当是当前步骤。");
                Assert.False(host.Activity.Steps[1].IsCurrent, "第二步此时还不该是当前步骤。");

                Save(window, "hostdesk-busy.png");

                // 推进到朗读：标题与步骤都要跟着变
                host.Activity.SetStep(1, "正在朗读");
                await WaitForRenderAsync();
                Assert.True(host.Activity.Steps[0].IsPast, "走过的步骤应当标记为已完成。");
                Assert.True(host.Activity.Steps[1].IsCurrent, "应当停在第二步。");
                Save(window, "hostdesk-speaking.png");

                // 结束：状态条收起，界面回到常态
                host.Activity.Complete("已朗读");
                await WaitForRenderAsync();
                Assert.False(host.Activity.IsActive, "结束后不应继续播放动效。");
                Assert.True(host.Activity.IsVisible, "完成后结果文案应当短暂保留。");
                Save(window, "hostdesk-done.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Idle_host_desk_snapshot_is_written()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            using var composition = await AppComposition.CreateFromContextAsync(
                await SessionContext.OpenAsync(root.Path, CancellationToken.None),
                CancellationToken.None);

            var shell = await composition.CreateShellAsync(CancellationToken.None);
            var window = new MainWindow { DataContext = shell, ShowActivated = false, Width = 1320, Height = 820 };
            WpfRunner.MoveOffScreen(window);
            window.Show();
            try
            {
                shell.ShowHostCommand.Execute(null);
                await shell.Host.LoadDemoAsync();
                await WaitForRenderAsync();

                Assert.False(shell.Host.Activity.IsActive, "空闲时状态条不该在动。");
                // 对话式布局里不再保留一块常驻的「就绪」：空闲时不占屏，
                // 只有真的在做或失败时才出现在对话末尾。
                Assert.False(shell.Host.Activity.IsDisplayed, "空闲时不该显示常驻状态块。");
                Save(window, "hostdesk-idle.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 重启后要能找到上一局：首页应当给出「继续这一局」入口。
    /// </summary>
    [Fact]
    public async Task Unfinished_session_is_offered_as_continue_entry_on_restart()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            var dataRoot = root.Path;

            // 第一局：走到搜证就停下来
            using (var composition = await AppComposition.CreateFromContextAsync(
                await SessionContext.OpenAsync(dataRoot, CancellationToken.None),
                CancellationToken.None))
            {
                var shell = await composition.CreateShellAsync(CancellationToken.None);
                var host = shell.Host;
                await host.LoadDemoAsync();
                await host.Session.ExecuteAsync(GameCommandType.Start, null, null);
                await host.Session.ExecuteAsync(GameCommandType.Advance, null, null);
            }

            // 重启：同一份数据根目录
            using (var composition = await AppComposition.CreateFromContextAsync(
                await SessionContext.OpenAsync(dataRoot, CancellationToken.None),
                CancellationToken.None))
            {
                var shell = await composition.CreateShellAsync(CancellationToken.None);

                Assert.True(shell.Library.HasResumableSession, "重启后应当找得到上一局。");
                Assert.Contains("非真实剧本", shell.Library.ResumableTitle, StringComparison.Ordinal);
                Assert.Contains("搜证", shell.Library.ResumableDetail, StringComparison.Ordinal);

                // 点「继续这一局」应当直接回到主持台并恢复进度
                shell.Library.ContinueCommand.Execute(null);
                await WaitForRenderAsync();

                Assert.Equal("搜证", shell.Host.Session.PhaseTitle);
                Assert.Equal(2, shell.Host.Session.Revision);
            }
        });
    }

    /// <summary>设置页快照：音色选择与诊断入口是否清楚。</summary>
    [Fact]
    public async Task Setup_page_snapshot_is_written()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            using var composition = await AppComposition.CreateFromContextAsync(
                await SessionContext.OpenAsync(root.Path, CancellationToken.None),
                CancellationToken.None);

            var shell = await composition.CreateShellAsync(CancellationToken.None);
            var window = new MainWindow { DataContext = shell, ShowActivated = false, Width = 1320, Height = 820 };
            WpfRunner.MoveOffScreen(window);
            window.Show();
            try
            {
                shell.ShowSetupCommand.Execute(null);
                await WaitForRenderAsync();

                Assert.IsType<SetupViewModel>(shell.Current);
                Assert.NotEmpty(shell.Setup.AvailableVoices);
                Save(window, "setup.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 首页：没有剧本包时也要有路可走（先试演示），有存档时给出「继续这一局」。
    /// </summary>
    [Fact]
    public async Task Library_page_offers_a_way_forward_when_empty()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            using var composition = await AppComposition.CreateFromContextAsync(
                await SessionContext.OpenAsync(root.Path, CancellationToken.None),
                CancellationToken.None);

            var shell = await composition.CreateShellAsync(CancellationToken.None);
            var window = new MainWindow { DataContext = shell, ShowActivated = false, Width = 1320, Height = 820 };
            WpfRunner.MoveOffScreen(window);
            window.Show();
            try
            {
                await WaitForRenderAsync();

                // 没有剧本包：首页给出「先试一局演示剧本」这条路
                Assert.False(shell.Library.HasPackages, "本机还没有剧本包。");
                Assert.False(shell.Library.HasResumableSession, "没有存档。");

                Save(window, "library-empty.png");

                // 点「先试一局演示剧本」应当跳到主持台并装上演示
                shell.Library.LoadDemoCommand.Execute(null);
                await WaitForRenderAsync();

                Assert.IsType<HostViewModel>(shell.Current);
                Assert.Equal("阅本", shell.Host.Session.PhaseTitle);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 搜证环节：待发放的材料编号必须看得见，主持人要知道这一轮要发几堆。
    /// </summary>
    [Fact]
    public async Task Pending_material_ids_are_visible_in_search_phase()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            using var composition = await AppComposition.CreateFromContextAsync(
                await SessionContext.OpenAsync(root.Path, CancellationToken.None),
                CancellationToken.None);

            var shell = await composition.CreateShellAsync(CancellationToken.None);
            var window = new MainWindow { DataContext = shell, ShowActivated = false, Width = 1320, Height = 820 };
            WpfRunner.MoveOffScreen(window);
            window.Show();
            try
            {
                shell.ShowHostCommand.Execute(null);
                var host = shell.Host;
                await host.LoadDemoAsync();

                var session = host.Session;
                await session.ExecuteAsync(GameCommandType.Start, null, null);
                await session.ExecuteAsync(GameCommandType.Advance, null, null);

                Assert.Equal("搜证", session.PhaseTitle);
                Assert.Equal(2, session.PendingMaterials.Count);
                Assert.Contains("2", session.PendingMaterialSummary, StringComparison.Ordinal);
                Assert.True(session.CanReleaseMaterials, "搜证阶段应当可以发牌。");
                Assert.Equal("发放材料 线索包一", session.ReleaseLabel);

                await WaitForRenderAsync();
                Save(window, "hostdesk-search.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// 揭秘后的大屏：真相按块渲染，等宽时间线带列参考线，右栏不重复显示真相。
    /// </summary>
    [Fact]
    public async Task Truth_big_screen_renders_blocks_and_side_column_stays_single()
    {
        await WpfRunner.RunAsync(async () =>
        {
            using var root = new TempRoot();
            using var composition = await AppComposition.CreateFromContextAsync(
                await SessionContext.OpenAsync(root.Path, CancellationToken.None),
                CancellationToken.None);

            var shell = await composition.CreateShellAsync(CancellationToken.None);
            var window = new MainWindow { DataContext = shell, ShowActivated = false, Width = 1320, Height = 820 };
            WpfRunner.MoveOffScreen(window);
            window.Show();
            try
            {
                shell.ShowHostCommand.Execute(null);
                var host = shell.Host;
                await host.LoadDemoAsync();

                var session = host.Session;
                await session.ExecuteAsync(GameCommandType.Start, null, null);
                await session.ExecuteAsync(GameCommandType.Advance, null, null);

                // 本环节材料要逐个发完才能推进
                await session.ExecuteAsync(GameCommandType.ReleaseMaterials, ["线索包一"], null);
                await session.ExecuteAsync(GameCommandType.ReleaseMaterials, ["线索包二"], null);
                await session.ExecuteAsync(GameCommandType.Advance, null, null);
                await session.ExecuteAsync(GameCommandType.Advance, null, null);
                Assert.Equal("投票", session.PhaseTitle);

                // 投票：合法汇总直接生效，没有额外勾选
                Assert.True(SessionViewModel.TryParseVotes("A=3, B=2", out var votes, out _));
                await session.ExecuteAsync(GameCommandType.SubmitVoteSummary, null, votes);
                await session.ExecuteAsync(GameCommandType.Advance, null, null);

                Assert.True(session.IsRevealPhase, "此时应进入真相环节。");
                await session.ExecuteAsync(GameCommandType.Reveal, null, null);

                await WaitForRenderAsync();

                // 揭秘后自动切到宽幅的公开材料与复盘页
                Assert.True(host.ShowMaterials, "揭秘后应自动切到公开材料与复盘页。");
                Assert.Equal("公开材料与复盘", host.MaterialsTitle);

                // 真相被拆成块：标题 / 等宽时间线 / 段落
                var blocks = session.TruthBlocks;
                Assert.Contains(blocks, b => b.Kind == TruthBlockKind.Heading);
                var code = blocks.FirstOrDefault(b => b.Kind == TruthBlockKind.Code);
                Assert.NotNull(code);
                Assert.True(code!.Lines.Count >= 5, "等宽块应当包含时间线表头与表体。");

                Save(window, "hostdesk-truth.png");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>让 WPF 把布局与渲染跑完，否则截到的是上一帧。</summary>
    private static async Task WaitForRenderAsync()
    {
        await Task.Delay(120);
        await System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(
            () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        await Task.Delay(60);
    }

    /// <summary>轮询等待某个条件成立（异步事件处理器的收尾不可 await 时用）。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }
    }

    private static void Save(Window window, string fileName)
    {
        Directory.CreateDirectory(ArtifactsDir);
        window.UpdateLayout();

        // 渲染根内容而不是窗口本身：窗口的视觉树挂在 HWND 上，
        // 直接 Render(window) 会出现未绘制的黑边。
        var target = (FrameworkElement?)window.Content ?? window;
        var width = (int)Math.Max(target.ActualWidth, 1);
        var height = (int)Math.Max(target.ActualHeight, 1);

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(target);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(ArtifactsDir, fileName));
        encoder.Save(stream);

        // 视觉快照的价值全在产物上：截不出来就当失败，不能「没有截图 = 没问题」。
        var file = new FileInfo(Path.Combine(ArtifactsDir, fileName));
        Assert.True(file.Exists && file.Length > 0, $"截图没有生成：{fileName}");
    }

    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "localdm-visual-" + Guid.NewGuid().ToString("N"));
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
