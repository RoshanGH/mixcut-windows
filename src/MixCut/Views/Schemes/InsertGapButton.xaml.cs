using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MixCut.Views.Schemes;

/// <summary>
/// 方案 storyboard 分镜卡片之间的 ⊕ 插入按钮（场景 B）。
/// 默认宽 12px、Opacity 0；hover 时变 28px、Opacity 1（CSS-like 切换）。
/// </summary>
/// <remarks>
/// 暴露 <see cref="InsertPosition"/>（要插入的方案位置，1-based；count+1 = 尾部追加）
/// + <see cref="Clicked"/> 事件，宿主用回调里的位置参数调 SchemeViewModel.InsertSegment。
/// </remarks>
public partial class InsertGapButton : UserControl
{
    public static readonly DependencyProperty InsertPositionProperty =
        DependencyProperty.Register(
            nameof(InsertPosition),
            typeof(int),
            typeof(InsertGapButton),
            new PropertyMetadata(0));

    /// <summary>该按钮代表的插入位置（1-based）。count+1 = 尾部追加。</summary>
    public int InsertPosition
    {
        get => (int)GetValue(InsertPositionProperty);
        set => SetValue(InsertPositionProperty, value);
    }

    /// <summary>用户点击事件，参数为 <see cref="InsertPosition"/>。</summary>
    public event EventHandler<int>? Clicked;

    public InsertGapButton()
    {
        InitializeComponent();
    }

    private void OnClick(object sender, MouseButtonEventArgs e) =>
        Clicked?.Invoke(this, InsertPosition);
}
