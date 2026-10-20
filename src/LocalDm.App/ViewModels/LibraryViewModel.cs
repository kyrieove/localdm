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
    private bool _hasResumableSession;
    private string _resumableTitle = string.Empty;
    private string _resumableDetail = string.Empty;

    public LibraryViewModel(string packagesDir)
    {
        _packagesDir = packagesDir;
        OpenCommand = new RelayCommand(OpenSelectedAsync, () => _selected?.CanStart == true);
        ContinueCommand = new RelayCommand(ContinueAsync, () => _hasResumableSession);
        LoadDemoCommand = new RelayCommand(LoadDemoAsync);
        ImportCommand = new RelayCommand(
            () => { ImportRequested?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; },
            () => !IsImporting);
    }

    /// <summary>打开导入向导。导入进行中再点也是回到向导，不会开第二个。</summary>
    public event EventHandler? ImportRequested;

    /// <summary>「重新整理流程」：带着这一行回到向导弹 ④，不重新识别。</summary>
    public event EventHandler<LibraryPackageRow>? FlowRefineRequested;

    /// <summary>正在导入时列表里那行字：第 N/M 页。空表示没有导入在跑。</summary>
    public string ImportingText
    {
        get => _importingText;
        private set
        {
            if (SetProperty(ref _importingText, value))
            {
                OnPropertyChanged(nameof(IsImporting));
                OpenCommand.RaiseCanExecuteChanged();
                ImportCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private string _importingText = string.Empty;

    public bool IsImporting => !string.IsNullOrEmpty(ImportingText);

    /// <summary>向导报进度时调用；传空表示这次导入结束。</summary>
    public void SetImporting(string? text) => ImportingText = text ?? string.Empty;

    public RelayCommand ImportCommand { get; }

    /// <summary>「重新整理流程」：带着这一行回到向导第 ④ 步，不重新识别。</summary>
    public void RequestFlowRefine(LibraryPackageRow row)
    {
        if (row is not null && !string.IsNullOrEmpty(row.FilePath))
        {
            FlowRefineRequested?.Invoke(this, row);
        }
    }

    /// <summary>用户选定一个剧本包后触发；参数是包本身。</summary>
    public event EventHandler<ScriptPackage>? PackageOpened;

    /// <summary>主持人点了「继续这一局」；参数是要接着玩的包。</summary>
    public event EventHandler<ScriptPackage>? ContinueRequested;

    /// <summary>主持人想先试试内置演示剧本。</summary>
    public event EventHandler? DemoRequested;

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

    public RelayCommand ContinueCommand { get; }

    /// <summary>装载内置演示剧本。第一次用的人不该只看到一句「没有剧本」。</summary>
    public RelayCommand LoadDemoCommand { get; }

    /// <summary>有没有一局没走完的存档。</summary>
    public bool HasResumableSession
    {
        get => _hasResumableSession;
        private set
        {
            if (SetProperty(ref _hasResumableSession, value))
            {
                ContinueCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>没走完的那一局的剧本名。</summary>
    public string ResumableTitle
    {
        get => _resumableTitle;
        private set => SetProperty(ref _resumableTitle, value);
    }

    /// <summary>没走完的那一局停在哪里。</summary>
    public string ResumableDetail
    {
        get => _resumableDetail;
        private set => SetProperty(ref _resumableDetail, value);
    }

    /// <summary>
    /// 登记一局没走完的存档。
    /// 重启后主持人不该自己重新翻包找进度 —— 桌面上的软件要记得上一局。
    /// </summary>
    public void SetResumable(ScriptPackage package, Session session)
    {
        var phase = package.Phases.FirstOrDefault(p => p.PhaseId == session.CurrentPhaseId);
        _resumablePackage = package;
        ResumableTitle = package.Title;
        ResumableDetail = $"停在「{phase?.Title ?? "当前环节"}」";
        HasResumableSession = true;
    }

    /// <summary>
    /// 存档还在、但它绑定的冻结包不在了（通常是重新准备时流程被改写）时的说明。
    /// 这种情况必须明说「不能恢复」，不能静默忽略，也不能拿新包顶上去跳过环节。
    /// </summary>
    public string? OrphanNotice
    {
        get => _orphanNotice;
        private set
        {
            if (SetProperty(ref _orphanNotice, value))
            {
                OnPropertyChanged(nameof(HasOrphanNotice));
            }
        }
    }

    private string? _orphanNotice;

    public bool HasOrphanNotice => !string.IsNullOrWhiteSpace(OrphanNotice);

    public void SetOrphanNotice(string? text) => OrphanNotice = text;

    /// <summary>
    /// 清除「上一局还没走完」的入口。玩下去之后那张卡片就是过期信息，
    /// 留着会让人以为还有一局没走完。
    /// </summary>
    public void ClearResumable()
    {
        _resumablePackage = null;
        ResumableTitle = string.Empty;
        ResumableDetail = string.Empty;
        HasResumableSession = false;
    }

    private Task ContinueAsync()
    {
        if (_resumablePackage is not null)
        {
            ContinueRequested?.Invoke(this, _resumablePackage);
            Status = $"继续「{_resumablePackage.Title}」。";
        }

        return Task.CompletedTask;
    }

    private Task LoadDemoAsync()
    {
        DemoRequested?.Invoke(this, EventArgs.Empty);
        Status = "正在装载演示剧本。";
        return Task.CompletedTask;
    }

    private ScriptPackage? _resumablePackage;

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
            // 流程声明与包放在同一个目录，但它不是剧本包：跳过，别把它当成坏包。
            if (file.EndsWith(".flow.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

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
