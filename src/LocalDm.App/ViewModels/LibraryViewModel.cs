using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using LocalDm.Core.Domain;

namespace LocalDm.App.ViewModels;

/// <summary>
/// 首页：已注册剧本列表、空状态和开局入口。
/// 只读已冻结包文件；默认不发起任何模型或文档工具请求。
/// </summary>
public sealed class LibraryViewModel : ObservableObject
{
    private readonly string _packagesDir;
    private string _emptyStateText = "还没有可用的冻结剧本包。请先准备一份剧本。";
    private bool _hasPackages;
    private string _status = string.Empty;
    private LibraryPackageRow? _selected;

    public LibraryViewModel(string packagesDir)
    {
        _packagesDir = packagesDir;
        OpenCommand = new RelayCommand(OpenSelectedAsync, () => _selected?.CanStart == true);
    }

    /// <summary>用户选定一个剧本包后触发；参数是包本身。</summary>
    public event EventHandler<ScriptPackage>? PackageOpened;

    public ObservableCollection<LibraryPackageRow> Packages { get; } = [];

    public LibraryPackageRow? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OpenCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanOpen));
            }
        }
    }

    public bool CanOpen => _selected?.CanStart == true;

    public bool HasPackages
    {
        get => _hasPackages;
        private set => SetProperty(ref _hasPackages, value);
    }

    public string EmptyStateText
    {
        get => _emptyStateText;
        private set => SetProperty(ref _emptyStateText, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public RelayCommand OpenCommand { get; }

    public Task LoadAsync(CancellationToken cancellationToken)
    {
        Packages.Clear();
        if (!Directory.Exists(_packagesDir))
        {
            HasPackages = false;
            return Task.CompletedTask;
        }

        foreach (var file in Directory.GetFiles(_packagesDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            ScriptPackage? package;
            try
            {
                package = JsonSerializer.Deserialize<ScriptPackage>(File.ReadAllText(file));
            }
            catch (JsonException)
            {
                // 损坏的包不进入列表，也不静默当成可用
                continue;
            }

            if (package is null)
            {
                continue;
            }

            Packages.Add(new LibraryPackageRow(
                package.PackageId,
                package.Title,
                package.PlayerCount,
                package.Version,
                package.Validation == PackageValidationState.Ready ? "可以开局" : "需要复核",
                package.Validation == PackageValidationState.Ready,
                package.Spans.Count,
                file,
                package));
        }

        HasPackages = Packages.Count > 0;
        Selected = Packages.FirstOrDefault(p => p.CanStart) ?? Packages.FirstOrDefault();
        Status = HasPackages
            ? $"已注册 {Packages.Count} 个剧本包。"
            : "还没有可用的冻结剧本包。";
        return Task.CompletedTask;
    }

    private Task OpenSelectedAsync()
    {
        if (_selected is { CanStart: true, Package: { } package })
        {
            PackageOpened?.Invoke(this, package);
            Status = $"已选择「{package.Title}」，可以在主持台开局。";
        }

        return Task.CompletedTask;
    }

    /// <summary>兼容旧调用：直接设置一个包列表。</summary>
    public void SetPackages(IEnumerable<ScriptPackage> packages)
    {
        Packages.Clear();
        foreach (var package in packages)
        {
            Packages.Add(new LibraryPackageRow(
                package.PackageId,
                package.Title,
                package.PlayerCount,
                package.Version,
                package.Validation == PackageValidationState.Ready ? "可以开局" : "需要复核",
                package.Validation == PackageValidationState.Ready,
                package.Spans.Count,
                string.Empty,
                package));
        }

        HasPackages = Packages.Count > 0;
        Selected = Packages.FirstOrDefault();
    }
}

public sealed record LibraryPackageRow(
    string PackageId,
    string Title,
    int PlayerCount,
    string Version,
    string Readiness,
    bool CanStart,
    int PageCount,
    string FilePath,
    ScriptPackage Package)
{
    public string Summary => $"{PlayerCount} 人 · {PageCount} 页来源 · {Readiness}";
}
