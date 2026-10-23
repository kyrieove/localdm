using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Media.Imaging;
using LocalDm.Core.Domain;
using LocalDm.Core.Import;
using LocalDm.Infrastructure.Import;
using LocalDm.Core.Tablet;
using LocalDm.Tablet;
using Microsoft.AspNetCore.Builder;
using QRCoder;

namespace LocalDm.App.ViewModels;

public sealed class TabletJoinViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IPublicFeedSource _feed;
    private readonly string _webRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ScriptPackage? _package;
    private string? _packagePath;
    private WebApplication? _server;
    private string _status = "先从剧本库打开一本剧本，再让平板加入。";
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
    }

    public RelayCommand ToggleCommand { get; }
    public string ToggleLabel => IsRunning ? "关闭平板连接" : "开启平板连接";
    public RelayCommand OpenCommand { get; }
    public RelayCommand CopyCommand { get; }
    public ObservableCollection<string> Addresses { get; } = [];
    public bool IsRunning => _server is not null;
    public string LocalUrl { get; private set; } = string.Empty;
    public string StoryTitle => _package?.Title ?? "尚未选择剧本";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
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

    public async Task SetPackageAsync(ScriptPackage? package, string? packagePath, Func<Task>? loadDesktop = null)
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) { return; }
            await StopCoreAsync();
            _package = null;
            _packagePath = null;
            // 桌面切局和网页绑定共用这把锁；期间的加入请求必须等到切局完成。
            if (loadDesktop is not null) { await loadDesktop(); }
            _package = package;
            _packagePath = packagePath;
            OnPropertyChanged(nameof(StoryTitle));
            Status = package?.Tablet is null
                ? "开启连接时会自动准备这本剧本的平板材料，不会改用演示材料。"
                : "已选择本局真实剧本。开启连接后，平板即可扫码加入。";
        }
        finally { _gate.Release(); }
    }

    public async Task StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) { return; }
            if (_server is not null) { return; }
            if (_package is null || string.IsNullOrEmpty(_packagePath))
            {
                Status = "还没有可用的平板清单：请先从剧本库打开一本真实剧本，再开启平板连接。";
                return;
            }

            if (_package.Tablet is null)
            {
                if (_package.PackageId == LocalDm.App.Demo.DemoPackage.PackageId)
                {
                    Status = "演示剧本没有平板清单，也不会用演示材料冒充。请先从剧本库打开一本真实剧本。";
                    return;
                }

                Status = "正在准备平板材料（首次需要几秒，只在本机处理）…";
                _package = await PrepareTabletAsync(_package, _packagePath);
            }
            if (_package.Tablet is not { } tablet)
            {
                return;
            }

            var content = new PackageTabletContent(_package, TabletAssetRoot.Resolve(_packagePath, tablet));
            var server = TabletWebHost.Create(content, _feed, ["--urls", "http://0.0.0.0:0"], _webRoot);
            try { await server.StartAsync(); }
            catch { await server.DisposeAsync(); throw; }
            _server = server;
            var port = new Uri(server.Urls.Single()).Port;
            LocalUrl = $"http://127.0.0.1:{port}/";
            foreach (var address in LanAddresses()) { Addresses.Add($"http://{address}:{port}/"); }
            if (Addresses.Count == 0) { Addresses.Add(LocalUrl); }
            SelectedAddress = Addresses[0];
            Status = $"正在提供「{_package.Title}」与本局公开对话。平板需连接同一 Wi-Fi；首次联网请允许 Windows 专用网络访问。";
            NotifyRunning();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[Tablet] " + ex.GetType().Name);
            Status = "平板服务启动失败，请确认网页资源齐全且局域网连接可用后重试。";
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// 旧包没有平板清单时现场补：只在内存里补，不写新版本，当前这一局的存档与包标识不变。
    /// ponytail: 按「一张卡一个文件」整组确认，配不唯一或文件对不上的组仍由准备器暂停；
    /// 整版多卡的剧本需要人逐组确认版式，那一步还没有界面。
    /// </summary>
    private static Task<ScriptPackage> PrepareTabletAsync(ScriptPackage package, string packagePath) => Task.Run(() =>
    {
        var assetsDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(packagePath))!, "..", "assets"));
        var confirmed = package.Materials.Select(m => m.MaterialId).ToHashSet(StringComparer.Ordinal);
        var manifest = TabletPackagePreparer.Prepare(package, assetsDir, confirmed);
        return RealScriptPackageBuilder.WithTabletManifest(package, manifest);
    });

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try { await StopCoreAsync(); Status = "平板连接已关闭。电脑端主持不受影响。"; }
        finally { _gate.Release(); }
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
