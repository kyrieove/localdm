using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LocalDm.App.Input;
using LocalDm.App.ViewModels;

namespace LocalDm.App.Views;

public partial class HostView : UserControl
{
    public HostView()
    {
        InitializeComponent();
    }

    private void OnInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not HostViewModel vm)
        {
            return;
        }

        var composing = InputMethod.Current?.ImeState == InputMethodState.On
                        && !string.IsNullOrEmpty(vm.InputText);
        if (HostInputController.ShouldSendOnEnter(e, composing))
        {
            e.Handled = true;
            vm.SendCommand.Execute(null);
        }
    }

    private async void OnResumeClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is HostViewModel vm)
        {
            await vm.ResumeAsync();
        }
    }
}
