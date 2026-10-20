using System.Windows;
using System.Windows.Controls;
using LocalDm.App.ViewModels;

namespace LocalDm.App.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
    }

    /// <summary>每本剧本后面那个「重新整理流程」：带着这一行回到向导第 ④ 步。</summary>
    private void OnRefineFlow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LibraryPackageRow row }
            && DataContext is LibraryViewModel library)
        {
            library.RequestFlowRefine(row);
        }
    }
}
