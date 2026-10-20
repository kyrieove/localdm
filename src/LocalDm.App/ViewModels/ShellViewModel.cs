using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

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

    public RelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public async void Execute(object? parameter)
    {
        try
        {
            await _execute();
        }
        catch (Exception ex)
        {
            ExecutionFailed?.Invoke(this, ex);
        }
    }

    public event EventHandler<Exception>? ExecutionFailed;

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>宿主视图共用的页面切换。</summary>
public sealed class ShellViewModel : ObservableObject
{
    private readonly LibraryViewModel _library;
    private readonly SetupViewModel _setup;
    private readonly HostViewModel _host;
    private object? _current;

    public ShellViewModel(LibraryViewModel library, SetupViewModel setup, HostViewModel host)
    {
        _library = library;
        _setup = setup;
        _host = host;
        _current = library;
        ShowLibraryCommand = new RelayCommand(() => { Current = _library; return Task.CompletedTask; });
        ShowSetupCommand = new RelayCommand(() => { Current = _setup; return Task.CompletedTask; });
        ShowHostCommand = new RelayCommand(() => { Current = _host; return Task.CompletedTask; });

        // 首页选定剧本后自动切到主持台，用户不必自己再找入口
        _library.PackageOpened += (_, _) => Current = _host;

        // 会话状态变了：左侧轨道可能刚出现「回到本局」入口
        _host.Session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SessionViewModel.SessionStateText)
                or nameof(SessionViewModel.PhaseTitle)
                or nameof(SessionViewModel.Revision))
            {
                OnPropertyChanged(nameof(CanReturnToSession));
            }
        };

        // 模型选择在设置页改完后，页脚那行连接状态要跟着变
        _setup.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SetupViewModel.ModelId)
                or nameof(SetupViewModel.AvailableModels))
            {
                OnPropertyChanged(nameof(ModelStatusLine));
            }
        };
    }

    /// <summary>页脚那行：模型到底选好了没有。只说结论，不报地址与 ID。</summary>
    public string ModelStatusLine =>
        string.IsNullOrWhiteSpace(_setup.ModelId) ? "模型未选择" : "模型已就绪";

    /// <summary>当前是否停在主持台：左侧轨道据此决定显示本局面板还是剧本库入口。</summary>
    public bool IsHostActive => ReferenceEquals(Current, _host);

    /// <summary>当前是否停在设置页。</summary>
    public bool IsSetupActive => ReferenceEquals(Current, _setup);

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
