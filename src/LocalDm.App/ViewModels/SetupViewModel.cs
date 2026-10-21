using System.Collections.ObjectModel;
using System.Windows;
using LocalDm.Core.Domain;
using LocalDm.Core.Hosting;
using LocalDm.Core.Ports;
using LocalDm.Infrastructure.Storage;

namespace LocalDm.App.ViewModels;

/// <summary>
/// 设置页：真实连接诊断、model ID 列表、音色试听、外部工具状态与错误。
/// 不显示真实剧本内容；caller key 明文只经 DPAPI 保存，输入框随即清空。
/// </summary>
public sealed class SetupViewModel : ObservableObject
{
    private readonly SettingsStore _settingsStore;
    private readonly IMagpieGateway _gateway;
    private readonly ITtsService _tts;
    private readonly HostDirector _director;
    private ToolSettings _settings = ToolSettings.Default;

    /// <summary>载入时设置页里的音色。用来判断「用户是否真的在设置页改过音色」。</summary>
    private string _loadedVoiceId = string.Empty;
    private string _status = string.Empty;
    private string _modelId = string.Empty;
    private string _magpieBaseUri = ToolSettings.DefaultMagpieUri.ToString();
    private string _voiceId = string.Empty;
    private string _callerKeyState = "未配置 caller key（本机 Magpie 通常不需要）。";
    private string _mineruExecutable = string.Empty;
    private string _ocrPythonPath = string.Empty;
    private bool _autoRead = true;
    private int _contextWindowTokens = ToolSettings.DefaultContextWindowTokens;
    private double _uiScale = ToolSettings.DefaultUiScale;
    private ChatWidthMode _chatWidthMode = ChatWidthMode.Standard;
    private bool _busy;

    public SetupViewModel(
        SettingsStore settingsStore,
        IMagpieGateway gateway,
        ITtsService tts,
        HostDirector director)
    {
        _settingsStore = settingsStore;
        _gateway = gateway;
        _tts = tts;
        _director = director;

        CheckConnectionCommand = new RelayCommand(CheckConnectionAsync, () => !Busy);
        CheckVoicesCommand = new RelayCommand(() => { RefreshVoices(); return Task.CompletedTask; }, () => !Busy);
        PreviewVoiceCommand = new RelayCommand(PreviewVoiceAsync, () => !Busy && AvailableVoices.Count > 0);
        SaveCommand = new RelayCommand(SaveAsync, () => !Busy);
    }

    public ObservableCollection<string> AvailableModels { get; } = [];

    public ObservableCollection<VoiceOption> AvailableVoices { get; } = [];

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string ModelId
    {
        get => _modelId;
        set => SetProperty(ref _modelId, value);
    }

    public string MagpieBaseUri
    {
        get => _magpieBaseUri;
        set => SetProperty(ref _magpieBaseUri, value);
    }

    public string VoiceId
    {
        get => _voiceId;
        set => SetProperty(ref _voiceId, value);
    }

    /// <summary>
    /// 回复生成后是否自动朗读。关闭后只显示文字，仍可在每条回复旁单独重播。
    /// </summary>
    public bool AutoRead
    {
        get => _autoRead;
        set => SetProperty(ref _autoRead, value);
    }

    /// <summary>
    /// 所用模型的上下文窗口长度（tokens）。主持台按它判断上下文占用，
    /// 快满时自动把较早的对话整理成备忘。
    /// </summary>
    public int ContextWindowTokens
    {
        get => _contextWindowTokens;
        set => SetProperty(ref _contextWindowTokens, value);
    }

    /// <summary>
    /// 界面缩放倍数。与窗口上的 Ctrl+加号 / Ctrl+滚轮改的是同一个值：
    /// 在这里改会立即作用到窗口，窗口上改也会立刻反映到这个滑块。
    /// </summary>
    public double UiScale
    {
        get => _uiScale;
        set
        {
            var clamped = Math.Clamp(Math.Round(value, 2), ToolSettings.MinUiScale, ToolSettings.MaxUiScale);
            if (SetProperty(ref _uiScale, clamped))
            {
                OnPropertyChanged(nameof(UiScaleHint));
            }
        }
    }

    /// <summary>滑块旁边那行说明：写出当前倍数与快捷键。</summary>
    public string UiScaleHint =>
        $"当前 {UiScale:P0}；Ctrl+加号、Ctrl+减号、Ctrl+0 恢复，Ctrl+滚轮同样生效。";

    /// <summary>对话宽度档位：标准 / 宽 / 满宽。</summary>
    public ChatWidthMode ChatWidthMode
    {
        get => _chatWidthMode;
        set
        {
            if (SetProperty(ref _chatWidthMode, value))
            {
                OnPropertyChanged(nameof(IsChatWidthStandard));
                OnPropertyChanged(nameof(IsChatWidthWide));
                OnPropertyChanged(nameof(IsChatWidthFull));
            }
        }
    }

    public bool IsChatWidthStandard
    {
        get => ChatWidthMode == ChatWidthMode.Standard;
        set
        {
            if (value)
            {
                ChatWidthMode = ChatWidthMode.Standard;
            }
        }
    }

    public bool IsChatWidthWide
    {
        get => ChatWidthMode == ChatWidthMode.Wide;
        set
        {
            if (value)
            {
                ChatWidthMode = ChatWidthMode.Wide;
            }
        }
    }

    public bool IsChatWidthFull
    {
        get => ChatWidthMode == ChatWidthMode.Full;
        set
        {
            if (value)
            {
                ChatWidthMode = ChatWidthMode.Full;
            }
        }
    }

    public string CallerKeyState
    {
        get => _callerKeyState;
        private set => SetProperty(ref _callerKeyState, value);
    }

    public string MineruExecutable
    {
        get => _mineruExecutable;
        set => SetProperty(ref _mineruExecutable, value);
    }

    /// <summary>备用 OCR 的 Python 解释器；留空则依次退回环境变量与 python。</summary>
    public string OcrPythonPath
    {
        get => _ocrPythonPath;
        set => SetProperty(ref _ocrPythonPath, value);
    }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
            {
                CheckConnectionCommand.RaiseCanExecuteChanged();
                PreviewVoiceCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public RelayCommand CheckConnectionCommand { get; }

    public RelayCommand CheckVoicesCommand { get; }

    public RelayCommand PreviewVoiceCommand { get; }

    public RelayCommand SaveCommand { get; }

    public ToolSettings CurrentSettings => _settings;

    /// <summary>设置保存成功后触发；宿主据此让主持台立刻用上新配置，不必重启。</summary>
    public event EventHandler<ToolSettings>? SettingsSaved;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        _settings = await _settingsStore.LoadAsync(cancellationToken);
        MagpieBaseUri = _settings.MagpieBaseUri.ToString();
        ModelId = _settings.ModelId ?? string.Empty;
        VoiceId = _settings.VoiceId ?? string.Empty;
        AutoRead = _settings.AutoRead;
        ContextWindowTokens = _settings.ContextWindowTokens;
        UiScale = _settings.UiScale;
        ChatWidthMode = _settings.ChatWidthMode;
        _loadedVoiceId = VoiceId;
        MineruExecutable = _settings.MineruExecutable ?? string.Empty;
        OcrPythonPath = _settings.OcrPythonPath ?? string.Empty;
        CallerKeyState = string.IsNullOrWhiteSpace(_settings.CallerKeyRef)
            ? "未配置 caller key（本机 Magpie 通常不需要）。"
            : "已配置 caller key（DPAPI 加密，不显示明文）。";
        RefreshVoices();
    }

    private async Task CheckConnectionAsync()
    {
        if (!TryParseBaseUri(out var uri, out var error))
        {
            Status = error;
            return;
        }

        Busy = true;
        Status = "正在读取模型列表…";
        try
        {
            if (_gateway is Infrastructure.Magpie.MagpieGateway magpie)
            {
                magpie.BaseUri = uri;
                magpie.CallerKey = _settingsStore.LoadCallerKey(_settings.CallerKeyRef);
            }
            var models = await _gateway.ListModelsAsync(CancellationToken.None);
            AvailableModels.Clear();
            foreach (var model in models)
            {
                AvailableModels.Add(model);
            }

            ModelId = AvailableModels.Contains(_settings.ModelId ?? string.Empty)
                ? _settings.ModelId!
                : model_id_preferred(AvailableModels);

            // 读到列表并选定 model ID 就是明确选择：立刻落盘，避免用户忘了点保存。
            // 音色不能在这里用启动时的旧快照：主持台可能已经改过音色了。
            _settings = new ToolSettings
            {
                MagpieBaseUri = uri,
                ModelId = ModelId,
                CallerKeyRef = _settings.CallerKeyRef,
                MineruExecutable = _settings.MineruExecutable,
                OcrPythonPath = OcrPythonPath,
                VoiceId = ResolveVoiceId(),
                AutoRead = AutoRead,
                InputHint = _settings.InputHint,
                ContextWindowTokens = ContextWindowTokens,
                UiScale = UiScale,
                ChatWidthMode = ChatWidthMode,
            };
            await _settingsStore.SaveAsync(_settings, CancellationToken.None);
            SettingsSaved?.Invoke(this, _settings);

            // 列表成功不等于生成可用：这里只报告列表结果
            Status = $"已连接本机模型服务，读取到 {models.Count} 个 model ID，已自动保存 model ID。";
        }
        catch (DomainRuleException ex)
        {
            AvailableModels.Clear();
            Status = ex.PublicReason;
        }
        finally
        {
            Busy = false;
        }
    }

    private static string model_id_preferred(IEnumerable<string> models)
    {
        var list = models.ToArray();
        return list.FirstOrDefault(m => m == "codex/gpt-6.1-sol")
            ?? list.FirstOrDefault(m => m == "group/auto-deepseek-v4-1-flash")
            ?? list.FirstOrDefault()
            ?? string.Empty;
    }

    private void RefreshVoices()
    {
        AvailableVoices.Clear();
        foreach (var voice in _tts.GetChineseVoices())
        {
            AvailableVoices.Add(new VoiceOption(voice));
        }

        if (string.IsNullOrWhiteSpace(VoiceId) && AvailableVoices.Count > 0)
        {
            VoiceId = AvailableVoices[0].Name;
        }

        Status = AvailableVoices.Count == 0
            ? "本机没有可用的中文声音：文字仍会显示，只有播音不可用。"
            : $"检测到 {AvailableVoices.Count} 个中文声音。";
    }

    private async Task PreviewVoiceAsync()
    {
        Busy = true;
        try
        {
            var item = new SpeechItem
            {
                SpeechId = Guid.NewGuid().ToString("N"),
                RequestId = "setup-preview",
                GenerationId = "setup-preview",
                SafeText = "这是音色试听，只读出这一句。",
            };
            await _tts.SpeakAsync(item, string.IsNullOrWhiteSpace(VoiceId) ? null : VoiceId, CancellationToken.None);
            Status = "试听完成。";
        }
        catch (DomainRuleException ex)
        {
            Status = ex.PublicReason;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// 这次要写盘的音色。
    /// 用户确实在设置页改过 → 用这里的；否则沿用磁盘上的当前值
    /// （主持台也可能改过音色，设置页拿启动时的旧快照覆盖回去就把它弄丢了）。
    /// </summary>
    private string? ResolveVoiceId()
    {
        if (VoiceId != _loadedVoiceId)
        {
            return string.IsNullOrWhiteSpace(VoiceId) ? null : VoiceId.Trim();
        }

        return _settingsStore.Load().VoiceId;
    }

    private async Task SaveAsync()
    {
        if (!TryParseBaseUri(out var uri, out var error))
        {
            Status = error;
            return;
        }

        try
        {
            // 主持台也能改音色。只有「用户确实在设置页动过音色」时才用这里的值；
            // 否则沿用磁盘上的当前值 —— 否则设置页会拿启动时的旧值把主持台的选择悄悄退回去。
            var voiceId = ResolveVoiceId();

            _settings = new ToolSettings
            {
                MagpieBaseUri = uri,
                ModelId = string.IsNullOrWhiteSpace(ModelId) ? null : ModelId.Trim(),
                CallerKeyRef = _settings.CallerKeyRef,
                MineruExecutable = string.IsNullOrWhiteSpace(MineruExecutable) ? null : MineruExecutable.Trim(),
                OcrPythonPath = string.IsNullOrWhiteSpace(OcrPythonPath) ? null : OcrPythonPath.Trim(),
                VoiceId = voiceId,
                AutoRead = AutoRead,
                InputHint = _settings.InputHint,
                ContextWindowTokens = ContextWindowTokens,
                UiScale = UiScale,
                ChatWidthMode = ChatWidthMode,
            };
            _settings.Validate();
            await _settingsStore.SaveAsync(_settings, CancellationToken.None);
            // 只在确实写盘成功后才推进「用户已改过音色」的标记：
            // 否则校验或写盘失败时，下一次保存会走「没改过」分支把本次选择丢掉。
            _loadedVoiceId = VoiceId;
            Status = "设置已保存。";
            OnPropertyChanged(nameof(CurrentSettings));
            SettingsSaved?.Invoke(this, _settings);
        }
        catch (DomainRuleException ex)
        {
            Status = ex.PublicReason;
        }
    }

    private bool TryParseBaseUri(out Uri uri, out string error)
    {
        error = string.Empty;
        if (!Uri.TryCreate(MagpieBaseUri, UriKind.Absolute, out var parsed))
        {
            uri = ToolSettings.DefaultMagpieUri;
            error = "模型地址格式不正确。";
            return false;
        }

        var settings = new ToolSettings { MagpieBaseUri = parsed };
        try
        {
            settings.Validate();
        }
        catch (DomainRuleException ex)
        {
            uri = parsed;
            error = ex.PublicReason;
            return false;
        }

        if (!parsed.AbsolutePath.EndsWith('/'))
        {
            parsed = new Uri(parsed + "/");
        }

        uri = parsed;
        return true;
    }
}
