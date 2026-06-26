using System.Windows.Controls;

namespace MixCut.Views;

/// <summary>
/// 配音变体检视器（v0.5.0）。独立 UserControl，由宿主（MainWindow 右侧覆盖层）设置 DataContext =
/// DubVariantInspectorViewModel，并按 IsVisible 控制可视性。抽出是为了绕开 SegmentLibraryViewV2
/// content 区的水平测量怪象（右对齐内容会被推出屏幕外），改由 MainWindow 有界内容列右锚定渲染。
/// </summary>
public partial class DubVariantInspectorView : UserControl
{
    public DubVariantInspectorView()
    {
        InitializeComponent();
    }
}
