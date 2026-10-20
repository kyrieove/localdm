using System.Windows.Controls;

namespace LocalDm.App.Views;

/// <summary>
/// 设置页。游玩界面不再出现 caller key 输入与文档工具路径：
/// 语音、模型与可用性检查收在这里，日常只需要选一次。
/// </summary>
public partial class SetupView : UserControl
{
    public SetupView()
    {
        InitializeComponent();
    }
}
