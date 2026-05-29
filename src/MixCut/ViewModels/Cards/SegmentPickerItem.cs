using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using MixCut.Models;

namespace MixCut.ViewModels.Cards;

/// <summary>
/// SegmentPickerDrawer / 未来 ArrangeOrderSheet 的卡片项。
/// 把 <see cref="MixCut.Models.Segment"/> 包装成可观察的 IsAlreadyInScheme + 派生显示字段，
/// 供 <see cref="MixCut.Views.Shared.SegmentCardCompact"/> 绑定。
/// </summary>
/// <remarks>
/// ThumbnailImage 不在构造时加载（懒加载）—— 由宿主 (Phase 4b) 在 ItemsSource 设置后
/// 通过 ThumbnailCache.Shared 异步加载并 set。Phase 4a 只交付 VM 字段。
/// </remarks>
public partial class SegmentPickerItem : ObservableObject
{
    /// <summary>底层分镜实体，宿主用它做 Insert/Replace 调用。</summary>
    public Segment Segment { get; }

    /// <summary>该分镜是否已在当前方案中（true 则卡片置灰 + 不可点）。</summary>
    [ObservableProperty]
    private bool _isAlreadyInScheme;

    /// <summary>缩略图，懒加载；初始为 null，宿主异步填入。</summary>
    [ObservableProperty]
    private BitmapImage? _thumbnailImage;

    /// <summary>时长 chip 文本，如 "10.8s"。</summary>
    public string DurationLabel => $"{Segment.Duration:F1}s";

    /// <summary>语义类型中文标签列表（供 ItemsControl 显示）。</summary>
    public IReadOnlyList<string> SemanticTypeLabels =>
        Segment.SemanticTypes.Select(t => t.ToLabel()).ToList();

    /// <summary>台词文本。</summary>
    public string Text => Segment.Text;

    public SegmentPickerItem(Segment segment, bool isAlreadyInScheme)
    {
        Segment = segment;
        _isAlreadyInScheme = isAlreadyInScheme;
    }
}
