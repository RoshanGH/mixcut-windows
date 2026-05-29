using System.Windows.Controls;

namespace MixCut.Views.Shared;

/// <summary>
/// 紧凑分镜卡片，120×240。供 SegmentPickerDrawer（场景 B）和未来 ArrangeOrderSheet（场景 A）复用。
/// 9:16 缩略图（120×213）+ 标签条 + 台词；已在方案中时显示半透明遮罩 + 🚫 角标。
/// 数据契约：DataContext 必须实现 ThumbnailImage / IsAlreadyInScheme / DurationLabel /
/// SemanticTypeLabels / Text（见 <see cref="MixCut.ViewModels.Cards.SegmentPickerItem"/>）。
/// </summary>
public partial class SegmentCardCompact : UserControl
{
    public SegmentCardCompact()
    {
        InitializeComponent();
    }
}
