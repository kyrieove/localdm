# tablet-always-on — 平板服务随软件常驻、固定端口、换剧本热切换；旧包启动后台补平板材料

## Goal
- 软件启动后平板服务自动起来（`App.OnStartup` 调 `StartTabletAsync`），端口优先 5188，被占才换，网址固定；没打开剧本时平板看到等待页。
- 在剧本库打开/继续/导入一本真实剧本：平板先显示「材料准备中」，备好后自动变成该剧本的选角色页；换剧本时认领清空。演示剧本不给平板材料。
- 没有平板清单的旧包由 `TabletMaterials.Ensure` 现场补（读旁路文件，没有才准备），软件启动后在后台给全部已有剧本包补一遍。
- `TabletJoinViewModel` 给界面提供：二维码、网址（多网卡可选）、`Headline`（一句话状态标题）、`Status`、`ErrorText`、`JoinedSummary`（已加入哪些角色）。

## Risk
medium

## Parameters
- 首选端口 `5188`，绑定 `http://0.0.0.0:5188`；该端口起不来（任何异常）就改用 `http://0.0.0.0:0`，并在 `ErrorText` 写「端口 5188 被别的程序占用，已改用 {端口}。网址变了：平板需要重新扫码一次。」；连 0 端口也起不来时 `ErrorText` 写「平板服务没有启动：{异常类型名}。请检查防火墙与网络，或重启软件。」，`IsRunning` 为 false。
- `Headline` 取值：服务没起 →「平板服务没有启动」；准备中 →「正在准备平板材料」；其余 →「平板可以加入」。
- 演示剧本判定：`package.PackageId == LocalDm.App.Demo.DemoPackage.PackageId && package.Tablet is null`。
- `JoinedSummary` 格式：没剧本或没人 → 空串；否则 `已加入 {n} / {总角色数}：{角色名用、连接}`。
- `ToggleLabel` 文案保持 `开启平板连接` / `关闭平板连接`（偏好设置里作为高级开关保留）。

## Files
may change: `src/LocalDm.App/ViewModels/TabletJoinViewModel.cs`, `src/LocalDm.App/AppComposition.cs`, `src/LocalDm.App/App.xaml.cs`, `src/LocalDm.App/ViewModels/LibraryViewModel.cs`, `tests/LocalDm.UiTests/TabletJoinTests.cs` / read-only: `stories/`, `.appdata/`, `.local/`

## Steps (N = 5)
1. `src/LocalDm.Tablet/SwitchableTabletContent.cs` 与 `src/LocalDm.Infrastructure/Import/TabletMaterials.cs` 已由前两张卡合入（类型 `SwitchableTabletContent`：`SetNone()`、`SetPreparing(string)`、`Set(ITabletContent)`、`Roles`；`TabletMaterials.Ensure(ScriptPackage, string)` 返回带清单的包）。本步不改文件，只确认它们存在；不存在就停下报告。
2. 整个替换 `src/LocalDm.App/ViewModels/TabletJoinViewModel.cs` 为下面的内容（保留 `CreateQrCode` 与 `LanAddresses` 原样，已在下方给出）：
```csharp
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Media.Imaging;
using LocalDm.Core.Domain;
using LocalDm.Core.Tablet;
using LocalDm.Infrastructure.Import;
using LocalDm.Tablet;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using QRCoder;

namespace LocalDm.App.ViewModels;

/// <summary>
/// 平板入口：服务随软件常驻、网址固定；剧本后装，换剧本时平板自动跟随。
/// 没打开剧本时平板看到等待页，不会用演示材料冒充。
/// </summary>
public sealed class TabletJoinViewModel : ObservableObject, IAsyncDisposable
{
    public const int PreferredPort = 5188;

    private readonly IPublicFeedSource _feed;
    private readonly string _webRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SwitchableTabletContent _content = new();
    private ScriptPackage? _package;
    private WebApplication? _server;
    private string _status = "平板入口还没有启动。";
    private string _headline = "平板服务没有启动";
    private string? _errorText;
    private string _joinedSummary = string.Empty;
    private string? _selectedAddress;
    private BitmapImage? _qrCode;
    private volatile bool _disposed;

    public TabletJoinViewModel(IPublicFeedSource feed, string? contentRoot = null)
    {
        _feed = feed;
        _webRoot = contentRoot ?? AppContext.BaseDirectory;
        ToggleCommand = new RelayCommand(() => IsRunning ? StopAsync() : StartAsync());
        OpenCommand = new RelayCommand(() =>
        {
            Process.Start(new ProcessStartInfo(LocalUrl) { UseShellExecute = true });
            return Task.CompletedTask;
        }, () => IsRunning);
        CopyCommand = new RelayCommand(() =>
        {
            Clipboard.SetText(SelectedAddress ?? LocalUrl);
            return Task.CompletedTask;
        }, () => IsRunning);
        TabletSessions.Changed += RefreshJoined;
    }

    public RelayCommand ToggleCommand { get; }
    public string ToggleLabel => IsRunning ? "关闭平板连接" : "开启平板连接";
    public RelayCommand OpenCommand { get; }
    public RelayCommand CopyCommand { get; }
    public ObservableCollection<string> Addresses { get; } = [];
    public bool IsRunning => _server is not null;
    public string LocalUrl { get; private set; } = string.Empty;
    public string StoryTitle => _package?.Title ?? "尚未打开剧本";
    public string Headline { get => _headline; private set => SetProperty(ref _headline, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => _errorText is not null;
    public string JoinedSummary { get => _joinedSummary; private set => SetProperty(ref _joinedSummary, value); }
    public BitmapImage? QrCode { get => _qrCode; private set => SetProperty(ref _qrCode, value); }
    public string? SelectedAddress
    {
        get => _selectedAddress;
        set
        {
            if (SetProperty(ref _selectedAddress, value))
            {
                QrCode = value is null ? null : CreateQrCode(value);
            }
        }
    }

    /// <summary>
    /// 换剧本：先让平板离开旧剧本，再切桌面会话，最后给平板装新剧本的材料。
    /// 平板材料由 <see cref="TabletMaterials.Ensure"/> 保证（读旁路文件，没有才准备）。
    /// </summary>
    public async Task SetPackageAsync(ScriptPackage? package, string? packagePath, Func<Task>? loadDesktop = null)
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) { return; }
            _package = null;
            _content.SetNone();
            // 桌面切局和网页绑定共用这把锁；期间的加入请求只会看到等待页。
            if (loadDesktop is not null) { await loadDesktop(); }
            _package = package;
            OnPropertyChanged(nameof(StoryTitle));
            if (package is null || string.IsNullOrEmpty(packagePath))
            {
                SetReady("电脑上还没有打开真实剧本，平板会先看到等待页。");
                return;
            }

            if (package.Tablet is null && package.PackageId == LocalDm.App.Demo.DemoPackage.PackageId)
            {
                SetReady("演示剧本没有平板材料，也不会用演示材料冒充。请先从剧本库打开一本真实剧本。");
                return;
            }

            _content.SetPreparing(package.Title);
            Headline = "正在准备平板材料";
            Status = $"正在准备《{package.Title}》的平板材料（首次需要几秒，只在本机处理）。";
            try
            {
                var ready = await Task.Run(() => TabletMaterials.Ensure(package, packagePath));
                _content.Set(new PackageTabletContent(ready, TabletAssetRoot.Resolve(packagePath, ready.Tablet!)));
                SetReady($"平板扫码后选角色，当前剧本：《{package.Title}》。");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                Debug.WriteLine("[Tablet] 备材料失败：" + ex.GetType().Name);
                _content.SetNone();
                SetReady("这本剧本的平板材料没有备好，这一局请用纸质材料。电脑端主持不受影响。");
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>软件启动时调一次：起服务，端口优先 5188。</summary>
    public async Task StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed || _server is not null) { return; }
            ErrorText = null;
            WebApplication? started = null;
            var fellBack = false;
            foreach (var port in new[] { PreferredPort, 0 })
            {
                var server = TabletWebHost.Create(_content, _feed, ["--urls", $"http://0.0.0.0:{port}"], _webRoot);
                try
                {
                    await server.StartAsync();
                    started = server;
                    fellBack = port == 0;
                    break;
                }
                catch (Exception ex)
                {
                    await server.DisposeAsync();
                    if (port == 0)
                    {
                        Debug.WriteLine("[Tablet] " + ex.GetType().Name);
                        ErrorText = $"平板服务没有启动：{ex.GetType().Name}。请检查防火墙与网络，或重启软件。";
                    }
                }
            }

            if (started is null)
            {
                Headline = "平板服务没有启动";
                Status = "平板服务没有启动。电脑端主持不受影响。";
                return;
            }

            _server = started;
            var actualPort = new Uri(started.Urls.First()).Port;
            LocalUrl = $"http://127.0.0.1:{actualPort}/";
            Addresses.Clear();
            foreach (var address in LanAddresses()) { Addresses.Add($"http://{address}:{actualPort}/"); }
            if (Addresses.Count == 0) { Addresses.Add(LocalUrl); }
            SelectedAddress = Addresses[0];
            if (fellBack)
            {
                ErrorText = $"端口 {PreferredPort} 被别的程序占用，已改用 {actualPort}。网址变了：平板需要重新扫码一次。";
            }

            SetReady(_package is null ? "电脑上还没有打开真实剧本，平板会先看到等待页。" : $"平板扫码后选角色，当前剧本：《{_package.Title}》。");
            NotifyRunning();
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();
            Headline = "平板服务已关闭";
            Status = "平板连接已关闭。电脑端主持不受影响。";
        }
        finally { _gate.Release(); }
    }

    private void SetReady(string status)
    {
        Headline = IsRunning ? "平板可以加入" : "平板服务没有启动";
        Status = status;
        RefreshJoined();
    }

    private void RefreshJoined()
    {
        var server = _server;
        if (server is null)
        {
            JoinedSummary = string.Empty;
            return;
        }

        var claimed = server.Services.GetRequiredService<TabletSpikeState>().ClaimedRoles;
        var total = _content.Roles.Count;
        JoinedSummary = total == 0 || claimed.Count == 0
            ? string.Empty
            : $"已加入 {claimed.Count} / {total}：{string.Join("、", claimed)}";
    }

    private async Task StopCoreAsync()
    {
        if (_server is { } server)
        {
            _server = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await server.StopAsync(timeout.Token); }
            finally { await server.DisposeAsync(); }
        }
        SelectedAddress = null;
        Addresses.Clear();
        LocalUrl = string.Empty;
        JoinedSummary = string.Empty;
        NotifyRunning();
    }

    private void NotifyRunning()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(ToggleLabel));
        OnPropertyChanged(nameof(LocalUrl));
        ToggleCommand.RaiseCanExecuteChanged();
        OpenCommand.RaiseCanExecuteChanged();
        CopyCommand.RaiseCanExecuteChanged();
    }

    private static IEnumerable<IPAddress> LanAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up
            && n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet)
        .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
        .Distinct();

    public static BitmapImage CreateQrCode(string url)
    {
        var bytes = PngByteQRCodeHelper.GetQRCode(url, QRCodeGenerator.ECCLevel.M, 8);
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        TabletSessions.Changed -= RefreshJoined;
        // 关闭应用时只释放宿主，不从退出线程修改仍绑定到 WPF 的集合。
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_server is { } server)
            {
                _server = null;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await server.StopAsync(timeout.Token).ConfigureAwait(false); }
                finally { await server.DisposeAsync().ConfigureAwait(false); }
            }
        }
        finally { _gate.Release(); }
    }
}
```
3. `src/LocalDm.App/ViewModels/LibraryViewModel.cs`：在 `public IReadOnlyList<LibraryPackageRow> AllPackages { get; private set; } = [];` 之后加一行 `public TabletJoinViewModel? Tablet { get; set; }`（供剧本库页的「平板加入」卡片绑定）。
4. `src/LocalDm.App/AppComposition.cs`：(a) 在 `_tablet = tablet;` 这一行之后加 `library.Tablet = tablet;`，并把 `library` 存到新字段：在类里 `private TabletJoinViewModel? _tablet;` 之后加 `private LibraryViewModel? _library;`，在 `library.Tablet = tablet;` 之后加 `_library = library;`。(b) 在 `public Task StopTabletAsync() => ...` 之前加方法：
```csharp
    /// <summary>软件启动后调一次：起平板服务，并在后台给已有剧本包补平板材料（读旁路文件，没有才准备）。</summary>
    public async Task StartTabletAsync()
    {
        if (_tablet is null) { return; }
        await _tablet.StartAsync();
        var rows = _library?.AllPackages.ToArray() ?? [];
        _ = Task.Run(() =>
        {
            foreach (var row in rows.Where(r => r.Package.Tablet is null && !string.IsNullOrEmpty(r.FilePath)))
            {
                try { LocalDm.Infrastructure.Import.TabletMaterials.Ensure(row.Package, row.FilePath); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Tablet] 后台补材料失败：" + ex.GetType().Name); }
            }
        });
    }
```
   (c) 演示入口里 `await tablet.SetPackageAsync(null, null, () => host.LoadDemoAsync());` 保持不变。
5. `src/LocalDm.App/App.xaml.cs`：在 `window.Show();` 之后加 `await _composition.StartTabletAsync();`（`OnStartup` 已是 async）。
6. `tests/LocalDm.UiTests/TabletJoinTests.cs`：按新行为更新现有 4 个测试，不删测试、不加 Skip：
   - 凡是 `await join.SetPackageAsync(...)` 后接 `await join.StartAsync()` 的用例，改成**先** `await join.StartAsync();` 再 `await join.SetPackageAsync(...)`，随后断言 `join.IsRunning`；
   - `Unprepared_package_never_starts_demo_service`：改名为 `Demo_package_gets_no_tablet_materials`，先 StartAsync，再 SetPackageAsync(DemoPackage.Create(), "unused.json")，断言 `join.Status` 含「平板材料」且含「演示剧本」，并用 `HttpClient` 请求 `join.LocalUrl + "tablet/catalog"` 断言 JSON 里 `roles` 数组长度为 0；
   - `Settings_connection_button_shows_current_status_without_a_popup`：服务不再因点击而「启动失败」。改为：点击前 `button.Content == "开启平板连接"`；`Execute` 后 `await Task.Delay(1500)` 再断言 `shell.Setup.Tablet!.IsRunning` 为 true 且 `ToggleLabel == "关闭平板连接"`，再点一次后 IsRunning 为 false；保留 `Assert.Null(new HostView().FindName("TabletJoinButton"))`；
   - 另加一个 `Port_busy_falls_back_and_tells_the_user`：先用 `TcpListener` 占住 5188 端口（`new TcpListener(IPAddress.Any, 5188)`，Start，用完 Stop；若占不上就用 `Assert.Skip` 之外的办法——直接把这条测试里的 `listener.Start()` 放进 try，失败则 return，不用 Skip），再 `StartAsync`，断言 `IsRunning` 为 true、`HasError` 为 true、`ErrorText` 含「5188」且 `LocalUrl` 不含 `:5188/`。

## Check
`dotnet test tests/LocalDm.UiTests --filter-class "LocalDm.UiTests.TabletJoinTests"`

## Don't
- No commits, no other files, no refactors beyond the steps.
- Reuse existing functions; don't copy existing logic.
- Run scripts in the foreground and wait; before replying, stop any background task still running.
- No rm, no git push, no git checkout . / restore . / reset --hard.

## Acceptance
- verdict: accepted
- files: src/LocalDm.App/App.xaml.cs, src/LocalDm.App/AppComposition.cs, src/LocalDm.App/ViewModels/LibraryViewModel.cs, src/LocalDm.App/ViewModels/TabletJoinViewModel.cs, tests/LocalDm.UiTests/TabletJoinTests.cs
- check: `dotnet test tests/LocalDm.UiTests --filter-class "LocalDm.UiTests.TabletJoinTests"` → exit 0 (run by run-card)
- tokens:
  - round 1: exec custom-local:antigravity/gemini-3.8-flash: fresh 164k, cached 1722k, out 28k  (agy-tablet-always-on.jsonl) · review gpt-6.1-sol: fresh 89k, cached 467k, out 2k  (codex-review-tablet-always-on.jsonl)
- review findings: 2 real / 0 false positive (scope 0, card 0, wrong 0) / 0 missed (medium, gpt-6.1-sol)
- claude fix: TabletWebHost.Create 移进受保护的 try；停止与未运行时 Headline 统一为「平板服务没有启动」，准备中标题按 IsRunning 条件（review 两条 P1）
