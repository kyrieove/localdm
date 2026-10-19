using System.Windows;
using System.Windows.Controls;
using LocalDm.App.ViewModels;

namespace LocalDm.App.Views;

public partial class SetupView : UserControl
{
    public SetupView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// PasswordBox.Password 不参与数据绑定（避免明文进绑定系统），只在此处回填到 ViewModel 内存。
    /// </summary>
    private void OnCallerKeyChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SetupViewModel vm && sender is PasswordBox box)
        {
            vm.CallerKeyInput = box.Password;
        }
    }
}
