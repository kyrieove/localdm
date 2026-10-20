using System.Windows;
using System.Windows.Controls;
using LocalDm.App.ViewModels;

namespace LocalDm.App.Views;

public partial class ImportWizardView : UserControl
{
    public ImportWizardView() => InitializeComponent();

    private ImportWizardViewModel? Wizard => DataContext as ImportWizardViewModel;

    /// <summary>选剧本文件夹。用系统的文件夹选择框，不自己造一个。</summary>
    private void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择剧本文件夹" };
        if (dialog.ShowDialog() == true)
        {
            Wizard?.SetFolder(dialog.FolderName);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Wizard?.Close();

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(sender, -1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(sender, 1);

    private void Move(object sender, int offset)
    {
        if (sender is FrameworkElement { DataContext: ImportFlowStepRow row })
        {
            Wizard?.MoveStep(row, offset);
        }
    }

    private void OnDeleteStep(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ImportFlowStepRow row })
        {
            Wizard?.DeleteStep(row);
        }
    }
}
