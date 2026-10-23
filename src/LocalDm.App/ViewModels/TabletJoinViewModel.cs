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
            Headline = IsRunning ? "正在准备平板材料" : "平板服务没有启动";
            Status = $"正在准备《{package.Title}》的平板材料（首次需要几秒，只在本机处理）。";
            try
            {
                var ready = await Task.Run(() => TabletMaterials.Ensure(package, packagePath));
                _content.Set(new PackageTabletContent(ready, TabletAssetRoot.Resolve(packagePath, ready.Tablet!)));
                SetReady($"平板扫码后选角色，当前剧本：《{package.Title}》。");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
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
                WebApplication? server = null;
                try
                {
                    server = TabletWebHost.Create(_content, _feed, ["--urls", $"http://0.0.0.0:{port}"], _webRoot);
                    await server.StartAsync();
                    started = server;
                    fellBack = port == 0;
                    break;
                }
                catch (Exception ex)
                {
                    if (server is not null) { await server.DisposeAsync(); }
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
            Headline = "平板服务没有启动";
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
