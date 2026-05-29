using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using MixCut.Models;
using MixCut.ViewModels.Cards;

namespace MixCut.Views.Schemes;

/// <summary>
/// 右侧分镜选择抽屉（场景 B）。320px 宽，显示当前项目所有分镜的 9:16 网格视图。
/// </summary>
/// <remarks>
/// 数据契约：Items 是 ObservableCollection&lt;SegmentPickerItem&gt;，已在方案中的项 IsAlreadyInScheme=true。
/// 事件：SegmentPicked（用户点了可用分镜）、CloseRequested（点 ✕）。
/// 宿主负责：
/// 1) 准备 Items 集合（含「已在方案」标记 + 懒加载 ThumbnailImage）；
/// 2) 监听 SegmentPicked 调用 SchemeViewModel.InsertSegment / ReplaceSegment；
/// 3) 监听 CloseRequested 收起抽屉。
/// </remarks>
public partial class SegmentPickerDrawer : UserControl
{
    public static readonly DependencyProperty ItemsProperty =
        DependencyProperty.Register(
            nameof(Items),
            typeof(ObservableCollection<SegmentPickerItem>),
            typeof(SegmentPickerDrawer));

    public static readonly DependencyProperty HeaderTextProperty =
        DependencyProperty.Register(
            nameof(HeaderText),
            typeof(string),
            typeof(SegmentPickerDrawer),
            new PropertyMetadata("选择分镜"));

    public static readonly DependencyProperty StatsTextProperty =
        DependencyProperty.Register(
            nameof(StatsText),
            typeof(string),
            typeof(SegmentPickerDrawer),
            new PropertyMetadata(string.Empty));

    /// <summary>抽屉里展示的分镜卡片列表。</summary>
    public ObservableCollection<SegmentPickerItem>? Items
    {
        get => (ObservableCollection<SegmentPickerItem>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    /// <summary>头部标题。默认「选择分镜」。</summary>
    public string HeaderText
    {
        get => (string)GetValue(HeaderTextProperty);
        set => SetValue(HeaderTextProperty, value);
    }

    /// <summary>统计行文本，如「共 12 个 · 已在方案 3 个」。</summary>
    public string StatsText
    {
        get => (string)GetValue(StatsTextProperty);
        set => SetValue(StatsTextProperty, value);
    }

    /// <summary>用户点了一个可用分镜（已置灰的不会触发此事件）。</summary>
    public event EventHandler<Segment>? SegmentPicked;

    /// <summary>用户点了 ✕ 关闭。</summary>
    public event EventHandler? CloseRequested;

    public SegmentPickerDrawer()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnSegmentClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SegmentPickerItem item && !item.IsAlreadyInScheme)
        {
            SegmentPicked?.Invoke(this, item.Segment);
        }
    }
}
