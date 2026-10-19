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
    private string _status = string.Empty;
    private string _modelId = string.Empty;
    private string _magpieBaseUri = ToolSettings.DefaultMagpieUri.ToString();
    private string _voiceId = string.Empty;
    private string _callerKeyInput = string.Empty;
    private string _callerKeyState = "未配置 caller key（本机 Magpie 通常不需要）。";
    private string _mineruExecutable = string.Empty;
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
        SaveCallerKeyCommand = new RelayCommand(SaveCallerKeyAsync, () => !Busy);
    }

    public ObservableCollection<string> AvailableModels { get; } = [];

    public ObservableCollection<TtsVoice> AvailableVoices { get; } = [];

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

    public string CallerKeyInput
    {
        get => _callerKeyInput;
        set => SetProperty(ref _callerKeyInput, value);
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

    public RelayCommand SaveCallerKeyCommand { get; }

    public ToolSettings CurrentSettings => _settings;

    /// <summary>设置保存成功后触发；宿主据此让主持台立刻用上新配置，不必重启。</summary>
    public event EventHandler<ToolSettings>? SettingsSaved;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        _settings = await _settingsStore.LoadAsync(cancellationToken);
        MagpieBaseUri = _settings.MagpieBaseUri.ToString();
        ModelId = _settings.ModelId ?? string.Empty;
        VoiceId = _settings.VoiceId ?? string.Empty;
        MineruExecutable = _settings.MineruExecutable ?? string.Empty;
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

            // 读到列表并选定 model ID 就是明确选择：立刻落盘，避免用户忘了点保存
            _settings = new ToolSettings
            {
                MagpieBaseUri = uri,
                ModelId = ModelId,
                CallerKeyRef = _settings.CallerKeyRef,
                MineruExecutable = _settings.MineruExecutable,
                VoiceId = _settings.VoiceId,
                InputHint = _settings.InputHint,
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
        return list.FirstOrDefault(m => m == "group/auto-deepseek-v4-1-flash") ?? list.FirstOrDefault() ?? string.Empty;
    }

    private void RefreshVoices()
    {
        AvailableVoices.Clear();
        foreach (var voice in _tts.GetChineseVoices())
        {
            AvailableVoices.Add(voice);
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

    private async Task SaveAsync()
    {
        if (!TryParseBaseUri(out var uri, out var error))
        {
            Status = error;
            return;
        }

        try
        {
            _settings = new ToolSettings
            {
                MagpieBaseUri = uri,
                ModelId = string.IsNullOrWhiteSpace(ModelId) ? null : ModelId.Trim(),
                CallerKeyRef = _settings.CallerKeyRef,
                MineruExecutable = string.IsNullOrWhiteSpace(MineruExecutable) ? null : MineruExecutable.Trim(),
                VoiceId = string.IsNullOrWhiteSpace(VoiceId) ? null : VoiceId.Trim(),
                InputHint = _settings.InputHint,
            };
            _settings.Validate();
            await _settingsStore.SaveAsync(_settings, CancellationToken.None);
            Status = "设置已保存（caller key 不在普通设置文件中）。";
            OnPropertyChanged(nameof(CurrentSettings));
            SettingsSaved?.Invoke(this, _settings);
        }
        catch (DomainRuleException ex)
        {
            Status = ex.PublicReason;
        }
    }

    private Task SaveCallerKeyAsync()
    {
        try
        {
            var keyRef = _settingsStore.SaveCallerKey(CallerKeyInput);
            _settings = _settings with { CallerKeyRef = keyRef };
            CallerKeyInput = string.Empty;
            CallerKeyState = "已配置 caller key（DPAPI 加密，不显示明文）。";
            Status = "caller key 已加密保存；请再点一次保存设置。";
        }
        catch (DomainRuleException ex)
        {
            Status = ex.PublicReason;
        }

        return Task.CompletedTask;
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
