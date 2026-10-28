using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalDm.Core.Domain;
using LocalDm.Core.Import;
using LocalDm.Infrastructure.Import;

namespace LocalDm.App.ViewModels;

/// <summary>向导的五步。任何一步都能退回上一步改。</summary>
public enum ImportWizardStep
{
    /// <summary>① 选文件夹。</summary>
    Folder = 0,

    /// <summary>② 分类确认。</summary>
    Groups = 1,

    /// <summary>③ 识别。</summary>
    Extracting = 2,

    /// <summary>④ 流程确认。</summary>
    Flow = 3,

    /// <summary>⑤ 完成。</summary>
    Done = 4,
}

/// <summary>下拉里的一个类别选项：值配中文名。界面上只说「组织者手册」「角色本」「线索卡」。</summary>
public sealed record ImportKindOption(string Label, ScriptGroupKind Value);

/// <summary>环节类别的中文名：对齐剧本里玩家看到的说法。</summary>
public sealed record ImportStepKindOption(string Label, ScriptStepKind Value);

/// <summary>
/// ② 页的一行：分组名、相对路径、页数、文件类型、猜的类别与依据。
///
/// 这里**没有**任何识别文字或图片：导入的人可能就是这局的玩家。
/// </summary>
public sealed class ImportGroupRow : ObservableObject
{
    private ScriptGroupKind _kind;

    public event EventHandler? Changed;

    public ImportGroupRow(ScriptGroup group)
    {
        Name = group.Name;
        RelativePath = group.RelativePath;
        PageCount = group.PageCount;
        FileTypeLabel = group.FileTypeLabel;
        GuessReason = group.GuessReason;
        _kind = group.Kind;
    }

    public string Name { get; }

    public string RelativePath { get; }

    public int PageCount { get; }

    public string FileTypeLabel { get; }

    /// <summary>软件为什么这么猜，例如「路径含“线索”」。只说名字与路径。</summary>
    public string GuessReason { get; }

    public IReadOnlyList<ImportKindOption> KindOptions { get; } =
    [
        new("组织者手册", ScriptGroupKind.Organizer),
        new("角色本", ScriptGroupKind.Role),
        new("线索卡", ScriptGroupKind.Material),
        new("不进包", ScriptGroupKind.Ignore),
    ];

    public ScriptGroupKind Kind
    {
        get => _kind;
        set
        {
            if (SetProperty(ref _kind, value))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string Summary => $"{RelativePath} · {PageCount} 页 · {FileTypeLabel}";
}

/// <summary>
/// ④ 页的一行：标题、类别、提示、条件、依据页。
///
/// 提示被剧透检查命中时，界面上只显示「已隐藏，请重写」，原文不上屏。
/// </summary>
public sealed class ImportFlowStepRow : ObservableObject
{
    private string _title = string.Empty;
    private ScriptStepKind _kind = ScriptStepKind.Talk;
    private string _hint = string.Empty;
    private string _conditionsText = string.Empty;
    private bool _isHidden;
    private string? _sourceRef;

    public event EventHandler? Changed;

    /// <summary>
    /// 这一行的来源环节：AI 起草出来的，或从流程文件读出来的。
    ///
    /// 以它为底，只覆盖界面上能改的字段（标题、类别、提示、条件、依据页），
    /// 其余字段（步骤 ID、发放材料、按钮文字、是否提供「请主持人开场」）原样保留：
    /// 界面上没有这些字段的编辑入口，确认一次就清空它们，等于把已有流程改坏了。
    /// 确认页新增的行没有来源，为 null。
    /// </summary>
    public ScriptFlowStep? Source { get; init; }

    public string Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public ScriptStepKind Kind
    {
        get => _kind;
        set
        {
            if (SetProperty(ref _kind, value))
            {
                OnPropertyChanged(nameof(KindLabel));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public string KindLabel => Kind.ToString();

    /// <summary>类别下拉：值是枚举，显示中文名。</summary>
    public IReadOnlyList<ImportStepKindOption> KindOptions { get; } =
    [
        new("阅本", ScriptStepKind.Read),
        new("开场说明", ScriptStepKind.Intro),
        new("搜证", ScriptStepKind.Search),
        new("讨论", ScriptStepKind.Talk),
        new("投票", ScriptStepKind.Vote),
        new("揭秘", ScriptStepKind.Reveal),
        new("复盘", ScriptStepKind.Review),
    ];

    /// <summary>提示原文。被命中时只留在内存里给测试看，界面绑定的是 <see cref="DisplayHint"/>。</summary>
    public string Hint
    {
        get => _hint;
        set
        {
            if (SetProperty(ref _hint, value))
            {
                // 改了提示就要重新判定：不能靠上一次的结论一直停着。
                IsHidden = false;
                OnPropertyChanged(nameof(DisplayHint));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>界面上显示的提示：命中过就只说「已隐藏」，不显示原文。</summary>
    public string DisplayHint => IsHidden ? "这条提示可能含剧情，已隐藏，请重写" : Hint;

    public bool IsHidden
    {
        get => _isHidden;
        internal set
        {
            if (SetProperty(ref _isHidden, value))
            {
                OnPropertyChanged(nameof(DisplayHint));
                OnPropertyChanged(nameof(IsEditable));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>被隐藏的条目要重写或删掉才能继续；这里只用于界面提示。</summary>
    public bool IsEditable => !IsHidden;

    /// <summary>条件：每行一条，界面用多行文本框编辑。</summary>
    public string ConditionsText
    {
        get => _conditionsText;
        set => SetProperty(ref _conditionsText, value);
    }

    /// <summary>依据页：只显示「手册第 N 页」，不显示那一页的任何内容。</summary>
    public string SourceRefDisplay =>
        string.IsNullOrWhiteSpace(_sourceRef) ? "缺依据" : _sourceRef!.Replace("组织者手册", "手册");

    public string? SourceRef
    {
        get => _sourceRef;
        internal set
        {
            if (SetProperty(ref _sourceRef, value))
            {
                OnPropertyChanged(nameof(SourceRefDisplay));
            }
        }
    }

    public IReadOnlyList<string> Conditions =>
        ConditionsText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// 这一行 → 流程里的一个环节。以来源环节为底，只用界面能改的字段覆盖；
    /// 没有来源的新行按序号取 ID（重复时由 <see cref="ImportWizardViewModel.BuildFlow"/> 补后缀）。
    /// </summary>
    public ScriptFlowStep ToStep(int index)
    {
        var source = Source;
        return new ScriptFlowStep
        {
            StepId = string.IsNullOrWhiteSpace(source?.StepId) ? $"step-{index + 1}" : source!.StepId,
            Title = Title.Trim(),
            Kind = Kind,
            Hint = string.IsNullOrWhiteSpace(Hint) ? null : Hint.Trim(),
            Conditions = Conditions,
            SourceRef = string.IsNullOrWhiteSpace(SourceRef) ? null : SourceRef,
            AdvanceLabel = source?.AdvanceLabel,
            RevealLabel = source?.RevealLabel,
            MaterialIds = source?.MaterialIds ?? [],
            RequiresAllMaterials = source?.RequiresAllMaterials ?? false,
            PublicSegmentIds = source?.PublicSegmentIds ?? [],
            PublicGroups = source?.PublicGroups ?? [],
            OffersOpeningSpeech = source?.OffersOpeningSpeech ?? false,
        };
    }
}

/// <summary>
/// 手册分层的一行：只显示页号与它属于哪一层，不显示这一页的任何内容。
/// </summary>
public sealed class ImportLayeringPageRow : ObservableObject
{
    private string _layer;

    public event EventHandler? Changed;

    public ImportLayeringPageRow(int pageNumber, string layer)
    {
        PageNumber = pageNumber;
        _layer = layer;
    }

    public int PageNumber { get; }

    /// <summary>这一页没有识别到文字；只有人把它改成「不入包」才算确认过。</summary>
    public bool HasNoText { get; init; }

    public string PageLabel => $"第 {PageNumber} 页";

    public IReadOnlyList<string> LayerOptions { get; } = ["真相", "主持人专用", "不入包"];

    /// <summary>真相 / 主持人专用 / 不入包。切换只改分层，不显示内容。</summary>
    public string Layer
    {
        get => _layer;
        set
        {
            if (SetProperty(ref _layer, value))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}

/// <summary>
/// 剧本导入向导。
///
/// 全程不显示剧本正文、卡面、真相或缩略图：② 页只有分组信息，
/// ④ 页只有流程与页号，带剧情的提示一律显示「已隐藏」。
/// </summary>
public sealed class ImportWizardViewModel : ObservableObject
{
    private readonly string _packagesDir;
    private readonly Func<ScriptImportService> _serviceFactory;
    private readonly FlowDrafter _drafter;
    private readonly Func<string?> _modelId;

    private ImportWizardStep _step = ImportWizardStep.Folder;
    private string _folderPath = string.Empty;
    private string _title = string.Empty;
    private int _playerCount = 5;
    private string _status = string.Empty;
    private string _progressText = string.Empty;
    private string _estimatedRemainingText = string.Empty;
    private string _failedPagesText = string.Empty;
    private bool _blankFilesConfirmed;
    private bool _busy;
    private ScriptScanResult? _scan;
    private ScriptExtraction? _extraction;
    private IReadOnlyList<string> _truthPageTexts = [];
    private string _layeringSummary = string.Empty;
    private string _readinessText = string.Empty;
    private bool _canStartAfterBuild;
    private string _importingText = string.Empty;

    public ImportWizardViewModel(
        string dataRoot,
        string packagesDir,
        Func<ScriptImportService> serviceFactory,
        FlowDrafter drafter,
        Func<string?> modelId)
    {
        _packagesDir = packagesDir;
        _serviceFactory = serviceFactory;
        _drafter = drafter;
        _modelId = modelId;

        NextCommand = new RelayCommand(NextAsync, () => CanGoNext);
        BackCommand = new RelayCommand(BackAsync, () => CanGoBack);
        CancelCommand = new RelayCommand(() => { CancelExtraction(); return Task.CompletedTask; }, () => _step == ImportWizardStep.Extracting, allowReentry: true);
        ScanCommand = new RelayCommand(ScanAsync, () => !Busy && Directory.Exists(FolderPath));
        AddStepCommand = new RelayCommand(() => { AddStep(); return Task.CompletedTask; });
        BuildCommand = new RelayCommand(BuildAsync, () => CanBuild);
        OpenBuiltCommand = new RelayCommand(OpenBuiltAsync, () => _canStartAfterBuild);
    }

    /// <summary>本次导入建好的包可以打开了。</summary>
    public event EventHandler<ScriptPackage>? PackageBuilt;

    /// <summary>回到剧本库（关掉向导）。</summary>
    public event EventHandler? CloseRequested;

    public ObservableCollection<ImportGroupRow> Groups { get; } = [];

    public ObservableCollection<ImportFlowStepRow> FlowSteps { get; } = [];

    public ObservableCollection<ImportLayeringPageRow> LayeringPages { get; } = [];

    public ImportWizardStep Step
    {
        get => _step;
        private set
        {
            if (SetProperty(ref _step, value))
            {
                RaiseStepFlags();
            }
        }
    }

    public bool IsFolderStep => Step == ImportWizardStep.Folder;

    public bool IsGroupsStep => Step == ImportWizardStep.Groups;

    public bool IsExtractingStep => Step == ImportWizardStep.Extracting;

    public bool IsFlowStep => Step == ImportWizardStep.Flow;

    public bool IsDoneStep => Step == ImportWizardStep.Done;

    public string StepLabel => Step switch
    {
        ImportWizardStep.Folder => "第 1 步 / 共 5 步：选文件夹",
        ImportWizardStep.Groups => "第 2 步 / 共 5 步：确认分类",
        ImportWizardStep.Extracting => "第 3 步 / 共 5 步：识别",
        ImportWizardStep.Flow => "第 4 步 / 共 5 步：确认流程",
        _ => "第 5 步 / 共 5 步：完成",
    };

    public string FolderPath
    {
        get => _folderPath;
        set
        {
            if (SetProperty(ref _folderPath, value))
            {
                // 「下一步」能不能点取决于文件夹是否存在：只刷新 ScanCommand 的话，
                // 选完文件夹后「下一步」会一直是灰的。
                ScanCommand.RaiseCanExecuteChanged();
                NextCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public int PlayerCount
    {
        get => _playerCount;
        set
        {
            if (SetProperty(ref _playerCount, value))
            {
                NextCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>③ 页的进度：第 N / 共 M 页。不含任何识别文字。</summary>
    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public string EstimatedRemainingText
    {
        get => _estimatedRemainingText;
        private set => SetProperty(ref _estimatedRemainingText, value);
    }

    public string FailedPagesText
    {
        get => _failedPagesText;
        private set => SetProperty(ref _failedPagesText, value);
    }

    /// <summary>剧本库列表里那行「正在导入：第 N/M 页」。</summary>
    public string ImportingText
    {
        get => _importingText;
        private set => SetProperty(ref _importingText, value);
    }

    public bool IsImporting => !string.IsNullOrEmpty(ImportingText);

    /// <summary>④ 页顶部常驻的那一行：提醒可能读到剧情。</summary>
    public string FlowNotice => "流程由 AI 起草，如果你在提示里读到了剧情，请直接删掉那一条。";

    /// <summary>手册分层摘要：只有页数与页号范围。</summary>
    public string LayeringSummary
    {
        get => _layeringSummary;
        private set => SetProperty(ref _layeringSummary, value);
    }

    /// <summary>没识别到文字、还没确认的手册页；全部确认后为空。</summary>
    public string LayeringNotice
    {
        get
        {
            var pending = LayeringPages
                .Where(page => page.HasNoText && page.Layer != "不入包")
                .Select(page => page.PageNumber)
                .ToArray();
            return pending.Length == 0
                ? string.Empty
                : $"{RangeText(pending)}没有识别到文字：确认没有内容的话，把它改成「不入包」，否则这本不能开局。";
        }
    }

    /// <summary>⑤ 页的就绪结论：只列类别与分组名。</summary>
    public string ReadinessText
    {
        get => _readinessText;
        private set => SetProperty(ref _readinessText, value);
    }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool CanGoBack => Step is ImportWizardStep.Groups or ImportWizardStep.Flow && !Busy;

    public bool CanGoNext => !Busy && Step switch
    {
        ImportWizardStep.Folder => Directory.Exists(FolderPath) && PlayerCount is >= 4 and <= 6,
        ImportWizardStep.Groups => Groups.Count(group => group.Kind != ScriptGroupKind.Ignore) > 0
                                   && Groups.Any(group => group.Kind == ScriptGroupKind.Organizer)
                                   && Groups.Count(group => group.Kind == ScriptGroupKind.Organizer) == 1,
        ImportWizardStep.Flow => CanBuild,
        _ => false,
    };

    /// <summary>④ 页能不能建包：没有隐藏条目、有标题、有且只有一个揭示环节。</summary>
    public bool CanBuild =>
        !Busy
        && FlowSteps.Count > 0
        && FlowSteps.All(row => !row.IsHidden && !string.IsNullOrWhiteSpace(row.Title))
        && FlowSteps.Count(row => row.Kind == ScriptStepKind.Reveal) == 1
        && _extraction?.FailedPages.Count is null or 0
        && (_extraction?.BlankFiles.Count is null or 0 || BlankFilesConfirmed);

    /// <summary>④ 页上方的识别问题：工具报错的文件挡住建包；没识别出文字的文件要人确认是插图或空白页。</summary>
    public string ExtractionIssueText
    {
        get
        {
            if (_extraction is null)
            {
                return string.Empty;
            }

            var lines = new List<string>();
            if (_extraction.FailedPages.Count > 0)
            {
                lines.Add($"{_extraction.FailedPages.Count} 个文件识别出错，内容没进包，不能建包：{string.Join("、", _extraction.FailedPages)}。请回上一步重新识别，或把它们移出剧本文件夹。");
            }

            if (_extraction.BlankFiles.Count > 0)
            {
                lines.Add($"{_extraction.BlankFiles.Count} 个文件没有识别出文字，不会进包：{string.Join("、", _extraction.BlankFiles)}。");
            }

            return string.Join("\n", lines);
        }
    }

    public bool HasBlankFiles => _extraction?.BlankFiles.Count > 0;

    /// <summary>主持人确认这些没字的文件确实是插图或空白页；没确认前不能建包。</summary>
    public bool BlankFilesConfirmed
    {
        get => _blankFilesConfirmed;
        set
        {
            if (SetProperty(ref _blankFilesConfirmed, value))
            {
                OnPropertyChanged(nameof(CanBuild));
                RaiseCommandStates();
            }
        }
    }

    private void SetExtraction(ScriptExtraction? extraction)
    {
        _extraction = extraction;
        _blankFilesConfirmed = false;
        OnPropertyChanged(nameof(BlankFilesConfirmed));
        OnPropertyChanged(nameof(ExtractionIssueText));
        OnPropertyChanged(nameof(HasBlankFiles));
        OnPropertyChanged(nameof(CanBuild));
    }

    public RelayCommand NextCommand { get; }

    public RelayCommand BackCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand ScanCommand { get; }

    public RelayCommand AddStepCommand { get; }

    public RelayCommand BuildCommand { get; }

    public RelayCommand OpenBuiltCommand { get; }

    private CancellationTokenSource? _extractionCts;

    /// <summary>从剧本库进入：回到第一步。</summary>
    public void Start()
    {
        Step = ImportWizardStep.Folder;
        ClearGroups();
        ClearFlowSteps();
        LayeringPages.Clear();
        _scan = null;
        SetExtraction(null);
        _truthPageTexts = [];
        Status = "选一个剧本文件夹，软件会先看目录结构，不读内容。";
        ProgressText = string.Empty;
        EstimatedRemainingText = string.Empty;
        FailedPagesText = string.Empty;
        ReadinessText = string.Empty;
    }

    /// <summary>选好文件夹后由界面调用：默认标题取文件夹名，人数先在 4–6 里取 5。</summary>
    public void SetFolder(string folder)
    {
        FolderPath = folder;
        if (string.IsNullOrWhiteSpace(Title))
        {
            Title = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
    }

    /// <summary>上一步：④ → ②，② → ①。</summary>
    public async Task BackAsync()
    {
        if (Step == ImportWizardStep.Flow)
        {
            Step = ImportWizardStep.Groups;
            return;
        }

        if (Step == ImportWizardStep.Groups)
        {
            Step = ImportWizardStep.Folder;
        }

        await Task.CompletedTask;
    }

    /// <summary>下一步：② 只扫描，③ 才开始识别。④ 不在这里做（由 Build 直接完成）。</summary>
    private async Task NextAsync()
    {
        switch (Step)
        {
            case ImportWizardStep.Folder:
                await ScanAsync();
                break;
            case ImportWizardStep.Groups:
                await ExtractAndDraftAsync();
                break;
            case ImportWizardStep.Flow:
                await BuildAsync();
                break;
        }
    }

    /// <summary>扫描文件夹，填 ② 页。只读目录结构。</summary>
    public async Task ScanAsync()
    {
        try
        {
            Busy = true;
            var scan = _serviceFactory().Scan(FolderPath);
            _scan = scan;
            ClearGroups();
            foreach (var group in scan.Groups)
            {
                AddGroup(new ImportGroupRow(group));
            }

            Step = ImportWizardStep.Groups;
            Status = BuildGroupWarnings(scan);
        }
        catch (Exception ex) when (ex is IOException or DirectoryNotFoundException or ArgumentException)
        {
            Status = "读不了这个文件夹：" + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>② 页的提醒：缺组织者手册、多个候选、角色本数与人数不一致。只提分组名。</summary>
    private string BuildGroupWarnings(ScriptScanResult scan)
    {
        var warnings = new List<string>();
        if (scan.NeedsOrganizerChoice)
        {
            warnings.Add("没有分组像是组织者手册：请把其中一组改成「组织者手册」。");
        }

        if (scan.HasMultipleOrganizerCandidates)
        {
            warnings.Add("有多个分组像是组织者手册：只能保留一个。");
        }

        var roleCount = Groups.Count(group => group.Kind == ScriptGroupKind.Role);
        if (roleCount != PlayerCount)
        {
            warnings.Add($"角色本分组有 {roleCount} 个，这本是 {PlayerCount} 人；确认一下有没有分错。");
        }

        return warnings.Count == 0 ? "分类看起来没问题，可以开始识别。" : string.Join(" ", warnings);
    }

    /// <summary>③ 识别 → 起草流程 → 剧透检查 → ④。</summary>
    public async Task ExtractAndDraftAsync()
    {
        if (_scan is null)
        {
            return;
        }

        var confirmed = Groups
            .Select(row => new ScriptGroup
            {
                Name = row.Name,
                RelativePath = row.RelativePath,
                Files = [],
                Kind = row.Kind,
                GuessReason = row.GuessReason,
            })
            .ToArray();

        // 文件清单从扫描结果拿回：界面行不带文件路径，免得路径与正文混在一起显示。
        var filesByName = _scan.Groups.ToDictionary(group => group.Name, group => group.Files, StringComparer.Ordinal);
        confirmed = confirmed
            .Select(row => row.Files.Count == 0 && filesByName.TryGetValue(row.Name, out var files)
                ? row with { Files = files }
                : row)
            .ToArray();

        Busy = true;
        ImportingText = "正在导入：准备识别";
        _extractionCts = new CancellationTokenSource();
        try
        {
            Step = ImportWizardStep.Extracting;
            ProgressText = "准备识别…";
            CancelCommand.RaiseCanExecuteChanged();

            var progress = new Progress<ImportProgress>(report =>
            {
                ProgressText = $"第 {report.Completed} / 共 {report.Total} 页";
                ImportingText = $"正在导入：第 {report.Completed}/{report.Total} 页";
                FailedPagesText = report.FailedCount == 0 ? string.Empty : $"失败 {report.FailedCount} 页";
                EstimatedRemainingText = report.EstimatedRemaining is { } left
                    ? $"预计还要 {(int)left.TotalMinutes} 分 {left.Seconds} 秒"
                    : string.Empty;
            });

            var service = _serviceFactory();
            var extraction = await service.ExtractAsync(FolderPath, confirmed, progress, _extractionCts.Token);
            if (extraction.IsBlocked)
            {
                Status = "识别没有通过隐私预检或资料授权，已停下：" + extraction.BlockedReason;
                ImportingText = string.Empty;
                Step = ImportWizardStep.Groups;
                return;
            }

            SetExtraction(extraction);
            _truthPageTexts = extraction.Pages
                .Where(page => string.Equals(page.Group, OrganizerGroupName, StringComparison.Ordinal))
                .Select(page => page.Text)
                .ToArray();

            await DraftFlowAsync();
            Step = ImportWizardStep.Flow;
            Status = DraftFailureReason is null
                ? "流程由 AI 起草，请逐条确认；带剧情的提示会显示「已隐藏」。"
                : "AI 没能起草流程：" + DraftFailureReason + " 请自行填写环节。";
            ImportingText = string.Empty;
        }
        catch (OperationCanceledException)
        {
            Status = "已取消识别。已经识别完的页会留在缓存里，下次接着来。";
            ImportingText = string.Empty;
            Step = ImportWizardStep.Groups;
        }
        catch (Exception ex) when (ex is DomainRuleException or IOException)
        {
            Status = "识别失败：" + ex.Message;
            ImportingText = string.Empty;
            Step = ImportWizardStep.Groups;
        }
        finally
        {
            _extractionCts?.Dispose();
            _extractionCts = null;
            Busy = false;
            RaiseCommandStates();
        }
    }

    /// <summary>起草流程并按剧透检查标出要重写的条目。</summary>
    private async Task DraftFlowAsync()
    {
        ClearFlowSteps();
        DraftFailureReason = null;
        DraftAttempts = 0;
        if (_extraction is null)
        {
            return;
        }

        var input = new FlowDraftInput
        {
            Title = Title,
            PlayerCount = PlayerCount,
            OrganizerGroupName = OrganizerGroupName ?? string.Empty,
            OrganizerPages = _extraction.Pages
                .Where(page => string.Equals(page.Group, OrganizerGroupName, StringComparison.Ordinal))
                .Select(page => new FlowDraftPage(page.PageNumber, page.Text))
                .ToArray(),
            MaterialGroupNames = _extraction.Pages
                .Where(page => page.GroupKind == RealScriptPackageBuilder.ExtractedGroupKind.Material)
                .Select(page => page.Group)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            RoleGroupNames = _extraction.Pages
                .Where(page => page.GroupKind == RealScriptPackageBuilder.ExtractedGroupKind.Role)
                .Select(page => page.Group)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
        };

        var modelId = _modelId();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            DraftFailureReason = "还没有选择主持模型";
            Status = "还没有选择主持模型：流程要等模型起草。请先在偏好设置里选模型。";
            AddBlankStep();
            UpdateLayeringPages(truthPages: [], ignoredPages: []);
            return;
        }

        var result = await _drafter.DraftAsync(input, modelId, CancellationToken.None);
        DraftAttempts = result.Attempts;
        if (!result.Succeeded)
        {
            // 起草失败就给空白环节表，不套默认流程。
            DraftFailureReason = result.FailureReason;
            Status = "AI 没能起草流程：" + result.FailureReason + " 请自行填写环节。";
            AddBlankStep();
            UpdateLayeringPages(truthPages: [], ignoredPages: []);
            return;
        }

        foreach (var step in result.Flow!.Steps)
        {
            AddFlowStep(new ImportFlowStepRow
            {
                Source = step,
                Title = step.Title,
                Kind = step.Kind,
                Hint = step.Hint ?? string.Empty,
                ConditionsText = string.Join('\n', step.Conditions),
                SourceRef = step.SourceRef,
            });
        }

        UpdateLayeringPages(result.Flow.Layering?.TruthPages ?? [], result.Flow.Layering?.IgnoredPages ?? []);
        ApplySpoilerCheck();
    }

    /// <summary>逐条过剧透检查：命中的只标隐藏，不显示原文。</summary>
    private void ApplySpoilerCheck()
    {
        var flow = BuildFlow();
        var hits = FlowSpoilerCheck.Check(flow, _truthPageTexts);
        foreach (var row in FlowSteps)
        {
            row.IsHidden = false;
        }

        foreach (var hit in hits.Where(hit => hit.StepIndex < FlowSteps.Count))
        {
            var row = FlowSteps[hit.StepIndex];
            if (hit.Field == "提示")
            {
                row.IsHidden = true;
            }
            else if (hit.Field == "标题")
            {
                row.Title = string.Empty; // 标题是可编辑文本框，不能留原文；空标题本身就挡住建包
            }
        }

        // 条件同理：照抄真相的那几条直接去掉，不显示原文。
        foreach (var group in hits.Where(hit => hit.Field.StartsWith("条件", StringComparison.Ordinal) && hit.StepIndex < FlowSteps.Count)
                     .GroupBy(hit => hit.StepIndex))
        {
            var row = FlowSteps[group.Key];
            var copied = group.Select(hit => int.Parse(hit.Field["条件 ".Length..], System.Globalization.CultureInfo.InvariantCulture) - 1).ToHashSet();
            row.ConditionsText = string.Join('\n', row.Conditions.Where((_, i) => !copied.Contains(i)));
        }

        OnPropertyChanged(nameof(CanBuild));
        RaiseCommandStates();
    }

    /// <summary>④ 页的分层摘要：只有页数与页号范围。</summary>
    private void UpdateLayeringPages(IReadOnlyList<int> truthPages, IReadOnlyList<int> ignoredPages)
    {
        LayeringPages.Clear();
        var organizerPages = _extraction?.Pages
            .Where(page => string.Equals(page.Group, OrganizerGroupName, StringComparison.Ordinal))
            .Select(page => page.PageNumber)
            .OrderBy(page => page)
            .ToArray() ?? [];

        // 没识别到文字的页（封底、纯插图）也列出来，由人改成「不入包」确认放行；默认不放行。
        // shortcut: 只对 PDF 手册按文件内页号补页；手册是多个图片文件时没有这样的页号。
        var organizerFileIds = _extraction?.Pages
            .Where(page => string.Equals(page.Group, OrganizerGroupName, StringComparison.Ordinal))
            .Select(page => page.FileId)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var noTextPages = _extraction?.Sources
            .Where(file => organizerFileIds.Contains(file.FileId) && ScriptFileTypes.IsPdf(file.PrivatePath))
            .SelectMany(file => Enumerable.Range(1, file.PageCount))
            .Except(organizerPages)
            .Distinct()
            .OrderBy(page => page)
            .ToArray() ?? [];
        organizerPages = [.. organizerPages.Concat(noTextPages).OrderBy(page => page)];

        foreach (var page in organizerPages)
        {
            var layer = truthPages.Contains(page)
                ? "真相"
                : ignoredPages.Contains(page)
                    ? "不入包"
                    : "主持人专用";
            var row = new ImportLayeringPageRow(page, layer) { HasNoText = noTextPages.Contains(page) };
            row.Changed += (_, _) => OnPropertyChanged(nameof(LayeringNotice));
            LayeringPages.Add(row);
        }

        LayeringSummary = BuildLayeringSummary(organizerPages.Length, truthPages, ignoredPages);
        OnPropertyChanged(nameof(LayeringNotice));
    }

    private static string BuildLayeringSummary(
        int totalPages,
        IReadOnlyList<int> truthPages,
        IReadOnlyList<int> ignoredPages)
    {
        if (totalPages == 0)
        {
            return "组织者手册还没有识别结果。";
        }

        var hostOnly = totalPages - truthPages.Count - ignoredPages.Count;
        var parts = new List<string> { $"组织者手册共 {totalPages} 页" };
        if (truthPages.Count > 0)
        {
            parts.Add($"真相 {truthPages.Count} 页（{RangeText(truthPages)}）");
        }

        if (ignoredPages.Count > 0)
        {
            parts.Add($"不入包 {ignoredPages.Count} 页（{RangeText(ignoredPages)}）");
        }

        parts.Add($"其余 {Math.Max(0, hostOnly)} 页为主持人专用页");
        return string.Join("：", [parts[0], string.Join("，", parts.Skip(1))]);
    }

    /// <summary>「第 8–11 页」这种范围写法：连续页合并成区间。</summary>
    private static string RangeText(IReadOnlyList<int> pages)
    {
        var sorted = pages.OrderBy(page => page).ToArray();
        var ranges = new List<string>();
        var start = sorted[0];
        var previous = sorted[0];
        for (var i = 1; i < sorted.Length; i++)
        {
            if (sorted[i] == previous + 1)
            {
                previous = sorted[i];
                continue;
            }

            ranges.Add(start == previous ? $"第 {start} 页" : $"第 {start}–{previous} 页");
            start = previous = sorted[i];
        }

        ranges.Add(start == previous ? $"第 {start} 页" : $"第 {start}–{previous} 页");
        return string.Join("、", ranges);
    }

    private void AddGroup(ImportGroupRow row)
    {
        row.Changed += OnGroupRowChanged;
        Groups.Add(row);
    }

    private void ClearGroups()
    {
        foreach (var group in Groups)
        {
            group.Changed -= OnGroupRowChanged;
        }

        Groups.Clear();
    }

    private void OnGroupRowChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CanGoNext));
        NextCommand.RaiseCanExecuteChanged();
    }

    private void AddFlowStep(ImportFlowStepRow row)
    {
        row.Changed += OnFlowStepRowChanged;
        FlowSteps.Add(row);
    }

    private void ClearFlowSteps()
    {
        foreach (var step in FlowSteps)
        {
            step.Changed -= OnFlowStepRowChanged;
        }

        FlowSteps.Clear();
    }

    private void OnFlowStepRowChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(CanBuild));
        OnPropertyChanged(nameof(CanGoNext));
        BuildCommand.RaiseCanExecuteChanged();
        NextCommand.RaiseCanExecuteChanged();
    }

    /// <summary>起草失败或没有模型时给一张空白环节表：不套任何默认流程。</summary>
    private void AddBlankStep()
    {
        ClearFlowSteps();
        AddFlowStep(new ImportFlowStepRow { Title = string.Empty, Kind = ScriptStepKind.Read });
        AddFlowStep(new ImportFlowStepRow { Title = string.Empty, Kind = ScriptStepKind.Reveal });
        OnPropertyChanged(nameof(CanBuild));
        RaiseCommandStates();
    }

    private void AddStep()
    {
        AddFlowStep(new ImportFlowStepRow { Title = "新环节", Kind = ScriptStepKind.Talk });
        OnPropertyChanged(nameof(CanBuild));
        OnPropertyChanged(nameof(CanGoNext));
        NextCommand.RaiseCanExecuteChanged();
        BuildCommand.RaiseCanExecuteChanged();
        RaiseCommandStates();
    }

    /// <summary>删掉一行。删掉唯一的揭示环节要挡住。</summary>
    public bool CanDelete(ImportFlowStepRow row)
    {
        var reveals = FlowSteps.Count(step => step.Kind == ScriptStepKind.Reveal);
        return !(row.Kind == ScriptStepKind.Reveal && reveals <= 1);
    }

    public void DeleteStep(ImportFlowStepRow row)
    {
        if (!CanDelete(row))
        {
            Status = "不能删掉唯一的揭示环节。";
            return;
        }

        row.Changed -= OnFlowStepRowChanged;
        FlowSteps.Remove(row);
        OnPropertyChanged(nameof(CanBuild));
        OnPropertyChanged(nameof(CanGoNext));
        NextCommand.RaiseCanExecuteChanged();
        BuildCommand.RaiseCanExecuteChanged();
        RaiseCommandStates();
    }

    public void RemoveStep(ImportFlowStepRow row) => DeleteStep(row);

    /// <summary>上下移动。改完要重新过一遍剧透检查：位置变了，命中的环节也跟着变。</summary>
    public void MoveStep(ImportFlowStepRow row, int offset)
    {
        var index = FlowSteps.IndexOf(row);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= FlowSteps.Count)
        {
            return;
        }

        FlowSteps.Move(index, target);
        ApplySpoilerCheck();
        OnPropertyChanged(nameof(CanBuild));
        OnPropertyChanged(nameof(CanGoNext));
        NextCommand.RaiseCanExecuteChanged();
        BuildCommand.RaiseCanExecuteChanged();
    }

    /// <summary>按界面上的环节与分层组装一份流程声明。</summary>
    public ScriptFlow BuildFlow()
    {
        var steps = MakeStepIdsUnique(FlowSteps.Select((row, index) => row.ToStep(index)));
        var truthPages = LayeringPages.Where(page => page.Layer == "真相").Select(page => page.PageNumber).ToArray();
        var ignoredPages = LayeringPages.Where(page => page.Layer == "不入包").Select(page => page.PageNumber).ToArray();
        return new ScriptFlow
        {
            Steps = steps,
            Layering = new ScriptLayering
            {
                OrganizerGroup = OrganizerGroupName,
                PublicPages = [],
                TruthPages = truthPages,
                IgnoredPages = ignoredPages,
            },
        };
    }

    /// <summary>
    /// 环节 ID 必须唯一（引擎按 ID 找下一环）。已有环节沿用原 ID，
    /// 新加的按序号取名，撞上了就依次加 “-2”“-3”，直到唯一。
    /// 不用哈希：同一份流程重建两次要得到同一个包标识。
    /// </summary>
    private static IReadOnlyList<ScriptFlowStep> MakeStepIdsUnique(IEnumerable<ScriptFlowStep> steps)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ScriptFlowStep>();
        foreach (var step in steps)
        {
            var id = step.StepId;
            if (used.Add(id))
            {
                result.Add(step);
                continue;
            }

            var suffix = 2;
            string candidate;
            do
            {
                candidate = $"{id}-{suffix++}";
            }
            while (!used.Add(candidate));

            result.Add(step with { StepId = candidate });
        }

        return result;
    }

    /// <summary>建包：登记、写包文件与流程文件，然后到 ⑤。</summary>
    public async Task BuildAsync()
    {
        if (_extraction is null)
        {
            Status = "还没有识别结果，不能建包。";
            return;
        }

        var flow = BuildFlow();
        if (flow.ValidationIssues.Count > 0)
        {
            Status = "环节声明还有问题：" + string.Join("、", flow.ValidationIssues);
            return;
        }

        Busy = true;
        try
        {
            var result = await _serviceFactory().BuildAsync(_extraction, Title, PlayerCount, flow, CancellationToken.None);
            _built = result.Build.Package;
            _canStartAfterBuild = result.Build.Readiness.CanStart;
            ReadinessText = result.Build.Readiness.CanStart
                ? "这本可以开局。"
                : "这本还不能开局，需要复核：" + string.Join("、", result.Build.Readiness.IssueCategories);
            Step = ImportWizardStep.Done;
            Status = "导入完成。";
            OnPropertyChanged(nameof(CanBuild));
            RaiseCommandStates();
        }
        catch (Exception ex) when (ex is DomainRuleException or IOException)
        {
            Status = "建包失败：" + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    private ScriptPackage? _built;

    /// <summary>起草失败的原因（给界面与验收看）；成功时为 null。</summary>
    public string? DraftFailureReason { get; private set; }

    /// <summary>起草用了几次模型调用：1 表示第一次就成，2 表示重试过一次。没起草过为 0。</summary>
    public int DraftAttempts { get; private set; }

    private Task OpenBuiltAsync()
    {
        if (_built is not null)
        {
            PackageBuilt?.Invoke(this, _built);
        }

        return Task.CompletedTask;
    }

    /// <summary>关掉向导回剧本库。</summary>
    public void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void CancelExtraction() => _extractionCts?.Cancel();

    private string? OrganizerGroupName =>
        Groups.FirstOrDefault(row => row.Kind == ScriptGroupKind.Organizer)?.Name;

    /// <summary>
    /// 从剧本库「重新整理流程」：不重新识别，直接跳到 ④。
    /// 素材从冻结包的来源路径找回，识别走缓存，不会重跑 OCR。
    /// </summary>
    public async Task OpenExistingFlowAsync(ScriptPackage package, ScriptFlow? flow)
    {
        Start();
        Title = package.Title;
        PlayerCount = package.PlayerCount;
        var storyRoot = CommonSourceRoot(package);
        if (storyRoot is null || flow?.Layering?.OrganizerGroup is null)
        {
            Status = "找不到这本剧本的原始文件夹，请重新导入一次。";
            return;
        }

        FolderPath = storyRoot;
        var scan = _serviceFactory().Scan(storyRoot);
        _scan = scan;
        ClearGroups();
        foreach (var group in scan.Groups)
        {
            var row = new ImportGroupRow(group);
            if (string.Equals(group.Name, flow.Layering.OrganizerGroup, StringComparison.Ordinal))
            {
                row.Kind = ScriptGroupKind.Organizer;
            }

            AddGroup(row);
        }

        await ExtractAndDraftFromExistingAsync(flow);
    }

    private async Task ExtractAndDraftFromExistingAsync(ScriptFlow existing)
    {
        if (_scan is null)
        {
            return;
        }

        var filesByName = _scan.Groups.ToDictionary(group => group.Name, group => group.Files, StringComparer.Ordinal);
        var confirmed = Groups
            .Select(row => new ScriptGroup
            {
                Name = row.Name,
                RelativePath = row.RelativePath,
                Files = filesByName.TryGetValue(row.Name, out var files) ? files : [],
                Kind = row.Kind,
                GuessReason = row.GuessReason,
            })
            .ToArray();

        Busy = true;
        try
        {
            var progress = new Progress<ImportProgress>(report =>
                ImportingText = $"正在导入：第 {report.Completed}/{report.Total} 页");
            var extraction = await _serviceFactory()
                .ExtractAsync(FolderPath, confirmed, progress, CancellationToken.None);
            if (extraction.IsBlocked)
            {
                Status = "识别没有通过隐私预检或资料授权，已停下：" + extraction.BlockedReason;
                return;
            }

            SetExtraction(extraction);
            _truthPageTexts = extraction.Pages
                .Where(page => string.Equals(page.Group, OrganizerGroupName, StringComparison.Ordinal))
                .Select(page => page.Text)
                .ToArray();

            // 用已有的流程，不重新起草。
            ClearFlowSteps();
            foreach (var step in existing.Steps)
            {
                AddFlowStep(new ImportFlowStepRow
                {
                    Source = step,
                    Title = step.Title,
                    Kind = step.Kind,
                    Hint = step.Hint ?? string.Empty,
                    ConditionsText = string.Join('\n', step.Conditions),
                    SourceRef = step.SourceRef,
                });
            }

            UpdateLayeringPages(existing.Layering?.TruthPages ?? [], existing.Layering?.IgnoredPages ?? []);
            ApplySpoilerCheck();
            Step = ImportWizardStep.Flow;
            Status = "改完环节后确认，会生成新版本的包。";
        }
        finally
        {
            Busy = false;
            ImportingText = string.Empty;
        }
    }

    /// <summary>冻结包来源文件的共同父目录：就是当初导入的那个剧本文件夹。</summary>
    private static string? CommonSourceRoot(ScriptPackage package)
    {
        var directories = package.SourceFiles
            .Select(file => Path.GetDirectoryName(file.PrivatePath))
            .Where(directory => !string.IsNullOrEmpty(directory))
            .Select(directory => directory!)
            .ToArray();
        if (directories.Length == 0)
        {
            return null;
        }

        var common = directories[0];
        foreach (var directory in directories.Skip(1))
        {
            while (!directory.StartsWith(common, StringComparison.OrdinalIgnoreCase))
            {
                var parent = Path.GetDirectoryName(common);
                if (parent is null)
                {
                    return null;
                }

                common = parent;
            }
        }

        return common;
    }

    /// <summary>读剧本包旁边那份流程文件；没有就返回 null（界面会提示只能重新起草）。</summary>
    public static ScriptFlow? LoadSavedFlow(string packagePath)
    {
        var flowPath = Path.ChangeExtension(packagePath, ".flow.json");
        if (!File.Exists(flowPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ScriptFlow>(
                File.ReadAllText(flowPath),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    Converters = { new JsonStringEnumConverter() },
                });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void RaiseStepFlags()
    {
        OnPropertyChanged(nameof(IsFolderStep));
        OnPropertyChanged(nameof(IsGroupsStep));
        OnPropertyChanged(nameof(IsExtractingStep));
        OnPropertyChanged(nameof(IsFlowStep));
        OnPropertyChanged(nameof(IsDoneStep));
        OnPropertyChanged(nameof(StepLabel));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        NextCommand.RaiseCanExecuteChanged();
        BackCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        ScanCommand.RaiseCanExecuteChanged();
        BuildCommand.RaiseCanExecuteChanged();
        OpenBuiltCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanBuild));
        OnPropertyChanged(nameof(IsImporting));
    }
}
