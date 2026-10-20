using System.Windows.Threading;

namespace LocalDm.App.ViewModels;

/// <summary>当前正在进行的那件事的类别。决定状态条的强调色与语气。</summary>
public enum ActivityKind
{
    /// <summary>空闲：状态条不显示。</summary>
    None = 0,

    /// <summary>在等本机模型返回。这一步最慢，必须让主持人一眼看出来。</summary>
    Thinking = 1,

    /// <summary>正在朗读。</summary>
    Speaking = 2,

    /// <summary>其它处理中（提交、落盘、恢复）。</summary>
    Working = 3,

    /// <summary>失败。需要主持人看见并知道发生了什么。</summary>
    Error = 4,
}

/// <summary>流水线里的一步：已走过、正在走、还没走。</summary>
public sealed class ActivityStep
{
    /// <summary>步骤之间的分隔符（第一步没有）。</summary>
    public string Prefix { get; init; } = string.Empty;

    public required string Text { get; init; }

    /// <summary>是不是当前这一步。</summary>
    public bool IsCurrent { get; init; }

    /// <summary>是不是已经走过的步骤。</summary>
    public bool IsPast { get; init; }
}

/// <summary>
/// 主持台的「进行中」状态。
///
/// 主持人点完按钮后可能要等好几秒才听到声音（等本机模型返回）。这段时间里
/// 屏幕上如果什么都不变，人就会以为程序卡死了。所以这里把「正在做什么、
/// 走到第几步、已经等了多久」显式说出来，并让状态条动起来。
/// </summary>
public sealed class HostActivity : ObservableObject, IDisposable
{
    /// <summary>完成后状态条的停留时间：让「已完成」被看见，但不长期占屏。</summary>
    private static readonly TimeSpan CompleteLinger = TimeSpan.FromSeconds(2.5);

    /// <summary>失败后状态条的停留时间：错误必须被看见，但不能一直挂着。</summary>
    private static readonly TimeSpan ErrorLinger = TimeSpan.FromSeconds(8);

    private readonly DispatcherTimer _ticker;
    private readonly DispatcherTimer _linger;
    private DateTime _startedAt;
    private IReadOnlyList<string> _stepLabels = [];
    private int _currentStep;

    private ActivityKind _kind = ActivityKind.None;
    private string _title = string.Empty;
    private string _detail = string.Empty;
    private string _elapsed = string.Empty;
    private bool _isActive;
    public HostActivity()
    {
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _ticker.Tick += (_, _) => RefreshElapsed();

        _linger = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _linger.Tick += (_, _) =>
        {
            if (DateTime.UtcNow - _lingerStartedAt < _lingerDuration)
            {
                return;
            }

            _linger.Stop();
            Reset(_idleTitle, _idleDetail);
        };
    }

    private string _idleTitle = "就绪";
    private string _idleDetail = string.Empty;

    /// <summary>回到空闲态时显示的内容。宿主在阶段变化时更新它。</summary>
    public void SetIdleState(string title, string detail)
    {
        _idleTitle = title;
        _idleDetail = detail;
        if (Kind == ActivityKind.None)
        {
            Reset(title, detail);
        }
    }

    private DateTime _lingerStartedAt;
    private TimeSpan _lingerDuration;

    /// <summary>正在进行中的那件事属于哪一类。</summary>
    public ActivityKind Kind
    {
        get => _kind;
        private set
        {
            if (SetProperty(ref _kind, value))
            {
                OnPropertyChanged(nameof(IsError));
                OnPropertyChanged(nameof(IsThinking));
                OnPropertyChanged(nameof(IsSpeaking));
                OnPropertyChanged(nameof(IsVisible));
                OnPropertyChanged(nameof(IsDisplayed));
            }
        }
    }

    /// <summary>大标题：一句话说清现在在干什么。</summary>
    public string Title
    {
        get => _title;
        private set
        {
            if (SetProperty(ref _title, value))
            {
                OnPropertyChanged(nameof(IsVisible));
                OnPropertyChanged(nameof(IsDisplayed));
            }
        }
    }

    /// <summary>副标题：补充信息（当前环节、失败原因）。</summary>
    public string Detail
    {
        get => _detail;
        private set => SetProperty(ref _detail, value);
    }

    /// <summary>已等待时长，例如「已等待 3.2 秒」。</summary>
    public string Elapsed
    {
        get => _elapsed;
        private set => SetProperty(ref _elapsed, value);
    }

    /// <summary>是否处于「进行中」：决定状态条是否可见、动效是否播放。</summary>
    public bool IsActive
    {
        get => _isActive;
        private set
        {
            if (SetProperty(ref _isActive, value))
            {
                OnPropertyChanged(nameof(IsDisplayed));
            }
        }
    }

    /// <summary>流水线步骤：已走过 / 正在走 / 还没走。</summary>
    public IReadOnlyList<ActivityStep> Steps
    {
        get => _steps;
        private set => SetProperty(ref _steps, value);
    }

    private IReadOnlyList<ActivityStep> _steps = [];

    public bool IsError => Kind == ActivityKind.Error;

    public bool IsThinking => Kind == ActivityKind.Thinking;

    public bool IsSpeaking => Kind == ActivityKind.Speaking;

    /// <summary>
    /// 状态条是否应该有内容。
    /// 空闲时显示「就绪」，所以只要有标题就算有内容 —— 状态条是常驻的，
    /// 忽隐忽现会让整页布局跳动。
    /// </summary>
    public bool IsVisible => !string.IsNullOrEmpty(Title);

    /// <summary>
    /// 界面上要不要真的显示这一行。
    /// 只有「正在做」和「失败了」需要占屏：空闲的「就绪」以及完成后的停留
    /// 在对话式布局里是多余的常驻占位，逐条消息自己会写清朗读状态。
    /// </summary>
    public bool IsDisplayed => IsActive || IsError;

    /// <summary>
    /// 回到空闲态：显示「就绪」与当前环节。
    /// 空闲态也占位，是为了让状态条高度恒定 —— 状态条忽隐忽现会让整页布局跳动，
    /// 反而更难看清。
    /// </summary>
    public void Reset(string title, string detail = "")
    {
        _ticker.Stop();
        _linger.Stop();
        Kind = ActivityKind.None;
        Title = title;
        Detail = detail;
        Elapsed = string.Empty;
        IsActive = false;
        _stepLabels = [];
        _currentStep = 0;
        RebuildSteps();
    }

    /// <summary>
    /// 开始一件事。
    /// </summary>
    /// <param name="kind">类别，决定强调色。</param>
    /// <param name="title">大标题。</param>
    /// <param name="stepLabels">流水线步骤，按顺序。</param>
    /// <param name="detail">副标题。</param>
    public void Begin(ActivityKind kind, string title, IReadOnlyList<string> stepLabels, string detail = "")
    {
        _linger.Stop();
        _stepLabels = stepLabels;
        _currentStep = 0;
        _startedAt = DateTime.UtcNow;

        Kind = kind;
        Title = title;
        Detail = detail;
        IsActive = true;
        Elapsed = "刚开始";
        RebuildSteps();

        _ticker.Start();
    }

    /// <summary>推进到下一步（0 起）。</summary>
    public void SetStep(int index, string? title = null)
    {
        _currentStep = index;
        if (title is not null)
        {
            Title = title;
        }

        RebuildSteps();
    }

    /// <summary>更新副标题，不打断计时。</summary>
    public void SetDetail(string detail) => Detail = detail;

    /// <summary>这件事做完了。状态条短暂显示结果后回到「就绪」。</summary>
    public void Complete(string title)
    {
        _ticker.Stop();
        Kind = ActivityKind.Working;
        Title = title;
        Elapsed = string.Empty;
        IsActive = false;
        RebuildSteps();

        StartLinger(CompleteLinger);
    }

    /// <summary>这件事失败了。状态条以错误样式停留一段时间。</summary>
    public void Fail(string title, string detail = "")
    {
        _ticker.Stop();
        Kind = ActivityKind.Error;
        Title = title;
        Detail = detail;
        Elapsed = string.Empty;
        IsActive = false;
        RebuildSteps();

        StartLinger(ErrorLinger);
    }

    /// <summary>立即收起状态条，回到空闲态。</summary>
    public void Clear() => Reset(_idleTitle, _idleDetail);

    private void StartLinger(TimeSpan duration)
    {
        _lingerDuration = duration;
        _lingerStartedAt = DateTime.UtcNow;
        _linger.Start();
    }

    private void RefreshElapsed()
    {
        var seconds = (DateTime.UtcNow - _startedAt).TotalSeconds;
        Elapsed = seconds < 1
            ? "不到 1 秒"
            : $"已等待 {seconds:0.0} 秒";
    }

    private void RebuildSteps()
    {
        Steps = _stepLabels
            .Select((label, i) => new ActivityStep
            {
                Prefix = i == 0 ? string.Empty : "· ",
                Text = label,
                IsCurrent = i == _currentStep,
                IsPast = i < _currentStep,
            })
            .ToArray();
    }

    public void Dispose()
    {
        _ticker.Stop();
        _linger.Stop();
    }
}
