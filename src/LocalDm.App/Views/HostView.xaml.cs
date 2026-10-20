using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LocalDm.App.Input;
using LocalDm.App.ViewModels;

namespace LocalDm.App.Views;

public partial class HostView : UserControl
{
    private HostViewModel? _host;

    public HostView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();

        if (e.NewValue is HostViewModel host)
        {
            _host = host;
            host.MessageAppended += OnMessageAppended;
            host.PropertyChanged += OnHostPropertyChanged;

            // 切回对话时可能已经积了几条消息：直接停在最新一条。
            ScrollToEnd();
        }
    }

    private void Detach()
    {
        if (_host is null)
        {
            return;
        }

        _host.MessageAppended -= OnMessageAppended;
        _host.PropertyChanged -= OnHostPropertyChanged;
        _host = null;
    }

    private void OnMessageAppended(object? sender, ChatMessage message) => ScrollToEnd();

    /// <summary>
    /// 进行中那一行出现时也要滚到底：它排在对话末尾，
    /// 不滚下去的话主持人看不到「正在回应」，只会以为点了没反应。
    /// </summary>
    private void OnHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HostViewModel.IsBusy) or nameof(HostViewModel.ShowMaterials))
        {
            ScrollToEnd();
        }
    }

    private void ScrollToEnd()
    {
        Dispatcher.BeginInvoke(
            new Action(() => ChatScroll?.ScrollToEnd()),
            DispatcherPriority.ContextIdle);
    }

    private void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not HostViewModel vm)
        {
            return;
        }

        // RelayCommand.Execute 不检查 CanExecute：忙的时候按回车会并发进入 SendAsync，
        // 于是同时跑两路模型请求与两段播音。
        if (HostInputController.ShouldSendOnEnter(e) && vm.SendCommand.CanExecute(null))
        {
            e.Handled = true;
            vm.SendCommand.Execute(null);
        }
    }
}
