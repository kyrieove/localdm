using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LocalDm.Core.Domain;

namespace LocalDm.App.ViewModels;

/// <summary>极简 MVVM 基类：本项目只用一个，不引入第三方框架。</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>同步/异步中继命令。</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly bool _allowReentry;
    private EventHandler? _canExecuteChanged;
    private bool _isExecuting;

    public RelayCommand(Func<Task> execute, Func<bool>? canExecute = null, bool allowReentry = false)
    {
        _execute = execute;
        _canExecute = canExecute;
        _allowReentry = allowReentry;
    }

    /// <summary>
    /// 事件同时挂接到 CommandManager.RequerySuggested 上，
    /// 这样用户点击、输入、切换焦点后，所有按钮都会自动重新判断。
    /// 注意：后台任务结束这类没有用户输入的变化，仍然要手动调用 RaiseCanExecuteChanged()。
    /// </summary>
    public event EventHandler? CanExecuteChanged
    {
        add
        {
            _canExecuteChanged += value;
            CommandManager.RequerySuggested += value;
        }
        remove
        {
            _canExecuteChanged -= value;
            CommandManager.RequerySuggested -= value;
        }
    }

    public bool CanExecute(object? parameter)
    {
        if (!_allowReentry && _isExecuting)
        {
            return false;
        }

        return _canExecute?.Invoke() ?? true;
    }

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute();
        }
        catch (Exception ex)
        {
            ExecutionFailed?.Invoke(this, ex);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public event EventHandler<Exception>? ExecutionFailed;

    public void RaiseCanExecuteChanged() => _canExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>宿主视图共用的页面切换。</summary>
public sealed class ShellViewModel : ObservableObject
{
    private readonly LibraryViewModel _library;
    private readonly SetupViewModel _setup;
    private readonly HostViewModel _host;
    private readonly ImportWizardViewModel? _wizard;
    private object? _current;

    public ShellViewModel(
        LibraryViewModel library,
        SetupViewModel setup,
        HostViewModel host,
        ImportWizardViewModel? wizard = null)
    {
        _library = library;
        _setup = setup;
        _host = host;
        _wizard = wizard;
        _current = library;
        ShowLibraryCommand = new RelayCommand(() => { Current = _library; return Task.CompletedTask; });
        ShowSetupCommand = new RelayCommand(() => { Current = _setup; return Task.CompletedTask; });
        ShowHostCommand = new RelayCommand(() => { Current = _host; return Task.CompletedTask; });
        ShowImportCommand = new RelayCommand(ShowImportAsync, () => _wizard is not null);

        // 首页选定剧本后自动切到主持台，用户不必自己再找入口
        _library.PackageOpened += (_, _) => Current = _host;

        // 剧本库的「导入新剧本」：向导是剧本库里的一个切换视图，不另开窗口。
        if (_wizard is not null)
        {
            _library.ImportRequested += (_, _) => _ = ShowImportAsync();
            _wizard.CloseRequested += (_, _) => Current = _library;
            _library.FlowRefineRequested += (_, row) => _ = RefineFlowAsync(row);
            _wizard.PackageBuilt += (_, package) => ImportCompleted?.Invoke(this, package);

            // 导入进度同步到剧本库列表：回到列表也能看见「正在导入：第 N/M 页」。
            _wizard.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ImportWizardViewModel.ImportingText))
                {
                    _library.SetImporting(_wizard.ImportingText);
                }
            };
        }

        // 会话状态变了：左侧轨道可能刚出现「回到本局」入口
        _host.Session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SessionViewModel.SessionStateText)
                or nameof(SessionViewModel.PhaseTitle)
                or nameof(SessionViewModel.Revision))
            {
                OnPropertyChanged(nameof(CanReturnToSession));
                OnPropertyChanged(nameof(HasSession));
            }
        };

        // 模型选择在设置页改完后，页脚那行连接状态要跟着变
        _setup.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SetupViewModel.ModelId)
                or nameof(SetupViewModel.AvailableModels))
            {
                OnPropertyChanged(nameof(ModelStatusLine));
                OnPropertyChanged(nameof(IsModelMissing));
            }
        };
    }

    /// <summary>页脚那行：模型到底选好了没有。只说结论，不报地址与 ID。</summary>
    public string ModelStatusLine =>
        IsModelMissing ? "模型未选择" : "模型已就绪";

    /// <summary>还没选模型：导航栏「设置」下面常亮一行提醒，开局前就能看见。</summary>
    public bool IsModelMissing => string.IsNullOrWhiteSpace(_setup.ModelId);

    /// <summary>当前是否停在主持台：左侧轨道据此决定显示本局面板还是剧本库入口。</summary>
    public bool IsHostActive => ReferenceEquals(Current, _host);

    /// <summary>当前是否停在设置页。</summary>
    public bool IsSetupActive => ReferenceEquals(Current, _setup);

    /// <summary>导航栏「剧本库」亮起：剧本库与导入向导都算这一栏。</summary>
    public bool IsLibraryActive => !IsHostActive && !IsSetupActive;

    /// <summary>已经装载了一局：导航栏显示「本局」入口。</summary>
    public bool HasSession => _host.Session.Projection is not null;

    /// <summary>
    /// 能不能「回到本局」：已经装载了一局、但当前不在主持台。
    /// 没有这个入口，从设置页回来只能重新走一遍剧本库。
    /// </summary>
    public bool CanReturnToSession => _host.Session.Projection is not null && !IsHostActive;

    public object? Current
    {
        get => _current;
        private set
        {
            if (SetProperty(ref _current, value))
            {
                OnPropertyChanged(nameof(IsHostActive));
                OnPropertyChanged(nameof(IsSetupActive));
                OnPropertyChanged(nameof(IsLibraryActive));
                OnPropertyChanged(nameof(CanReturnToSession));

                if (ReferenceEquals(value, _library))
                {
                    // 回到剧本库：刷新「上一局还没走完」的入口，
                    // 否则那张卡片会一直停在启动时的旧阶段上。
                    LibraryShown?.Invoke(this, EventArgs.Empty);
                }
            }
        }
    }

    /// <summary>切回首页时触发。</summary>
    public event EventHandler? LibraryShown;

    public RelayCommand ShowLibraryCommand { get; }

    public RelayCommand ShowSetupCommand { get; }

    public RelayCommand ShowHostCommand { get; }

    /// <summary>打开导入向导。</summary>
    public RelayCommand ShowImportCommand { get; }

    /// <summary>导入建好一本新剧本；宿主据此把它装进主持台并刷新剧本库。</summary>
    public event EventHandler<ScriptPackage>? ImportCompleted;

    /// <summary>进向导并回到第一步。</summary>
    private Task ShowImportAsync()
    {
        if (_wizard is null)
        {
            return Task.CompletedTask;
        }

        _wizard.Start();
        Current = _wizard;
        return Task.CompletedTask;
    }

    /// <summary>「重新整理流程」：带着这本已有的流程直接到 ④，不重新识别。</summary>
    private async Task RefineFlowAsync(LibraryPackageRow row)
    {
        if (_wizard is null)
        {
            return;
        }

        var flow = ImportWizardViewModel.LoadSavedFlow(row.FilePath);
        await _wizard.OpenExistingFlowAsync(row.Package, flow);
        Current = _wizard;
    }

    /// <summary>导入向导本身（测试与宿主用）。</summary>
    public ImportWizardViewModel? Import => _wizard;

    public SetupViewModel Setup => _setup;

    public HostViewModel Host => _host;

    public LibraryViewModel Library => _library;

    /// <summary>默认不启动任何模型请求；只有用户显式点击才发起。</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _setup.LoadAsync(cancellationToken);
        await _library.LoadAsync(cancellationToken);
        await _host.LoadAsync(cancellationToken);
    }
}
