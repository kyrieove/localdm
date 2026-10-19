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

        // 书脊摘要跟随会话状态，主持人抬头就能确认当前环节
        _host.Session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SessionViewModel.SessionStateText)
                or nameof(SessionViewModel.PhaseTitle)
                or nameof(SessionViewModel.Revision))
            {
                RefreshSpine();
            }
        };
    }

    public ObservableCollection<string> NavigationItems { get; } = ["本局", "设置", "主持"];

    /// <summary>书脊底部：当前装载的剧本名与人数，没有装载时为提示语。</summary>
    public string CurrentPackageLine =>
        _host.Session.Projection is { } p ? p.PublicTitle : "尚未装载剧本";

    /// <summary>书脊底部：当前环节与状态，供主持人抬头一眼确认。</summary>
    public string CurrentPhaseLine =>
        _host.Session.Projection is { } p
            ? $"{p.CurrentPhase.Title} · {_host.Session.SessionStateLabel} · 第 {p.Revision} 次提交"
            : "　";

    private void RefreshSpine()
    {
        OnPropertyChanged(nameof(CurrentPackageLine));
        OnPropertyChanged(nameof(CurrentPhaseLine));
    }

    public object? Current
    {
        get => _current;
        private set => SetProperty(ref _current, value);
    }

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
