using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MixCut.Models;
using MixCut.ViewModels.Cards;

namespace MixCut.ViewModels;

/// <summary>
/// SegmentLibraryViewModel V2 扩展 —— 给 MVVM 数据驱动视图（SegmentLibraryViewV2）用的字段和方法。
/// 通过 partial 隔离，不影响 V1 视图。
/// 实现 <see cref="ISegmentCardHost"/>，CardVM 反向调用入口。
/// </summary>
public partial class SegmentLibraryViewModel : ISegmentCardHost
{
    /// <summary>V2 视图绑定的分组列表（视频维度分组）。保留供老逻辑兼容，新视图用 Cards。</summary>
    public ObservableCollection<VideoGroupViewModel> Groups { get; } = new();

    /// <summary>V2 视图绑定的平铺卡片列表（用 VirtualizingWrapPanel 渲染）。</summary>
    public ObservableCollection<SegmentCardViewModel> Cards { get; } = new();

    /// <summary>所有卡片 VM 的快速索引（按 Segment.Id）。</summary>
    private readonly Dictionary<Guid, SegmentCardViewModel> _cardIndex = new();

    /// <summary>V1 时代用 SelectedSegment 表示单选；V2 同步推送 IsSelected 给 CardVM。</summary>
    private SegmentCardViewModel? _selectedCard;

    /// <summary>
    /// 分镜结构性变更（右键单删 / Ctrl+Z 撤销恢复）后触发，让 View 刷新统计 / 类型 chip / 空态。
    /// 对齐 CLAUDE.md §F「数据变更 → UI 刷新通道」：VM 改了 segments 必须广播给 View，否则统计数字停在旧值。
    /// （批量删除走 View 自己的 RefreshAfterSegmentChange；右键单删由 CardVM 命令触发，View 不在调用链上，故需此事件。）
    /// </summary>
    public event Action? SegmentsStructurallyChanged;

    // ============ V2 数据装载 ============

    /// <summary>切项目 / 筛选 / 排序变化时调用，重建 Groups。</summary>
    public void RebuildGroups()
    {
        // 计算筛选后的分镜（FilteredSegments 已经按现有 ApplyFilter 维护）
        var filtered = FilteredSegments.ToList();

        // 按 Video 分组（保留视频出现顺序）
        var groupsBuilt = new List<(Video Video, List<Segment> Segs)>();
        var videoIndex = new Dictionary<Guid, int>();
        foreach (var seg in filtered)
        {
            if (seg.Video is null) continue;
            if (!videoIndex.TryGetValue(seg.Video.Id, out var idx))
            {
                videoIndex[seg.Video.Id] = groupsBuilt.Count;
                groupsBuilt.Add((seg.Video, new List<Segment> { seg }));
            }
            else
            {
                groupsBuilt[idx].Segs.Add(seg);
            }
        }

        // 复用现有 CardVM；创建新分组容器
        var newGroups = new List<VideoGroupViewModel>(groupsBuilt.Count);
        var newCardIds = new HashSet<Guid>();
        foreach (var (video, segs) in groupsBuilt)
        {
            var cards = new List<SegmentCardViewModel>(segs.Count);
            foreach (var seg in segs)
            {
                newCardIds.Add(seg.Id);
                if (!_cardIndex.TryGetValue(seg.Id, out var card))
                {
                    card = new SegmentCardViewModel(seg, this);
                    _cardIndex[seg.Id] = card;
                }
                else
                {
                    card.RefreshFromSegment();
                }
                // 同步多选 + 序号
                card.IsSelectionMode = IsSelectionMode;
                card.IsChecked = SelectedSegmentIds.Contains(seg.Id);
                card.SequenceNumber = NumberFor(seg);
                cards.Add(card);
            }
            newGroups.Add(new VideoGroupViewModel(video, cards));
        }

        // 清理已不需要的 CardVM
        var toRemove = _cardIndex.Keys.Where(id => !newCardIds.Contains(id)).ToList();
        foreach (var id in toRemove)
        {
            if (_cardIndex.TryGetValue(id, out var c)) c.Dispose();
            _cardIndex.Remove(id);
        }

        // 用增量更新代替整体替换（避免 UI 全量重建）
        ReplaceGroupsInPlace(newGroups);

        var totalCards = newGroups.Sum(g => g.Segments.Count);
        Serilog.Log.Information(
            "[GroupDiag] groups={GroupCount} totalCards={Total} sortByQuality={SQ}",
            newGroups.Count, totalCards, SortByQuality);
    }

    private void ReplaceGroupsInPlace(List<VideoGroupViewModel> newGroups)
    {
        Groups.Clear();
        foreach (var g in newGroups)
        {
            Groups.Add(g);
        }
    }

    /// <summary>
    /// 平铺重建 Cards（用于 VirtualizingWrapPanel 渲染）。
    /// 视口外的 container 不会被实例化，1000 张分镜也能瞬时加载。
    /// </summary>
    public void RebuildCards()
    {
        var filtered = FilteredSegments.ToList();

        // 清掉过时 CardVM
        var newCardIds = new HashSet<Guid>(filtered.Select(s => s.Id));
        var toRemove = _cardIndex.Keys.Where(id => !newCardIds.Contains(id)).ToList();
        foreach (var id in toRemove)
        {
            if (_cardIndex.TryGetValue(id, out var c)) c.Dispose();
            _cardIndex.Remove(id);
        }

        // 直接整体替换（VirtualizingWrapPanel 会只渲染视口内的）
        Cards.Clear();
        foreach (var seg in filtered)
        {
            if (!_cardIndex.TryGetValue(seg.Id, out var card))
            {
                card = new SegmentCardViewModel(seg, this);
                _cardIndex[seg.Id] = card;
            }
            else
            {
                card.RefreshFromSegment();
            }
            card.IsSelectionMode = IsSelectionMode;
            card.IsChecked = SelectedSegmentIds.Contains(seg.Id);
            card.SequenceNumber = NumberFor(seg);
            Cards.Add(card);
        }
    }

    /// <summary>
    /// 异步分批装载 Groups：每加一组 yield 一帧，让 WPF 在每组之间完成 measure/arrange/render。
    /// 用户感知为"渐进出现"而不是"卡顿 1 秒"。
    /// </summary>
    public async Task RebuildGroupsAsync()
    {
        var filtered = FilteredSegments.ToList();

        var groupsBuilt = new List<(Video Video, List<Segment> Segs)>();
        var videoIndex = new Dictionary<Guid, int>();
        foreach (var seg in filtered)
        {
            if (seg.Video is null) continue;
            if (!videoIndex.TryGetValue(seg.Video.Id, out var idx))
            {
                videoIndex[seg.Video.Id] = groupsBuilt.Count;
                groupsBuilt.Add((seg.Video, new List<Segment> { seg }));
            }
            else
            {
                groupsBuilt[idx].Segs.Add(seg);
            }
        }

        // 清掉过时 CardVM
        var newCardIds = new HashSet<Guid>(filtered.Select(s => s.Id));
        var toRemove = _cardIndex.Keys.Where(id => !newCardIds.Contains(id)).ToList();
        foreach (var id in toRemove)
        {
            if (_cardIndex.TryGetValue(id, out var c)) c.Dispose();
            _cardIndex.Remove(id);
        }

        // 立即清空 Groups，UI 看到"已切到分镜库 + 加载中"
        Groups.Clear();
        await System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(
            () => { }, System.Windows.Threading.DispatcherPriority.Background);

        // 逐组异步 Add，每加完一组 yield 让 WPF 渲染该组
        foreach (var (video, segs) in groupsBuilt)
        {
            var cards = new List<SegmentCardViewModel>(segs.Count);
            foreach (var seg in segs)
            {
                if (!_cardIndex.TryGetValue(seg.Id, out var card))
                {
                    card = new SegmentCardViewModel(seg, this);
                    _cardIndex[seg.Id] = card;
                }
                else
                {
                    card.RefreshFromSegment();
                }
                card.IsSelectionMode = IsSelectionMode;
                card.IsChecked = SelectedSegmentIds.Contains(seg.Id);
                card.SequenceNumber = NumberFor(seg);
                cards.Add(card);
            }
            var group = new VideoGroupViewModel(video, cards);
            Groups.Add(group);

            // 让 WPF 渲染该组卡片（Background 优先级 = 等渲染周期完）
            await System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(
                () => { }, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>同步多选模式状态到所有 CardVM。</summary>
    public void SyncSelectionModeToCards()
    {
        foreach (var card in _cardIndex.Values)
        {
            card.IsSelectionMode = IsSelectionMode;
            if (!IsSelectionMode)
            {
                card.IsChecked = false;
            }
        }
    }

    /// <summary>同步 SelectedSegmentIds 到 CardVM.IsChecked。</summary>
    public void SyncCheckedToCards()
    {
        foreach (var card in _cardIndex.Values)
        {
            card.IsChecked = SelectedSegmentIds.Contains(card.Id);
        }
    }

    // ============ ISegmentCardHost 实现（走 V1 已有路径，含 ReExtractText 台词联动） ============

    void ISegmentCardHost.AdjustStartTime(SegmentCardViewModel card, double step)
    {
        AdjustStartTime(card.Segment, step);
        card.RefreshFromSegment();
    }

    void ISegmentCardHost.AdjustEndTime(SegmentCardViewModel card, double step)
    {
        AdjustEndTime(card.Segment, step);
        card.RefreshFromSegment();
    }

    void ISegmentCardHost.SetStartTime(SegmentCardViewModel card, double newStart)
    {
        SetStartTime(card.Segment, newStart);
        card.RefreshFromSegment();
    }

    void ISegmentCardHost.SetEndTime(SegmentCardViewModel card, double newEnd)
    {
        SetEndTime(card.Segment, newEnd);
        card.RefreshFromSegment();
    }

    void ISegmentCardHost.RequestDelete(SegmentCardViewModel card)
    {
        var result = MessageBox.Show(
            $"删除分镜 {card.SegmentIndexLabel}？删除后可按 Ctrl+Z 撤销。",
            "确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;

        // P0-10 修：右键单删此前直接删、不入撤销栈，导致删完按 Ctrl+Z 提示「没有可撤销的操作」。
        // 现与批量删除路径对齐：删前快照 → 删除成功才压栈 → Ctrl+Z 调 RestoreSegments 整图恢复。
        var snapshot = Infrastructure.UndoStack.UndoClone.CloneSegment(card.Segment);
        try
        {
            DeleteSegment(card.Segment);
        }
        catch (Exception ex)
        {
            // 删除失败（DB 保存异常）：DeleteSegment 在 SaveChanges 抛出前不动内存，UI 与 DB 仍一致。
            // 不谎报成功、不压栈，给人话提示 + 重试入口（对齐批量删除的失败处理）。
            _logger.LogError("删除分镜失败: {Msg}", ex.Message);
            Views.Components.ToastService.Show("删除失败，请重试", Views.Components.ToastStyle.Error);
            return;
        }
        RebuildGroups();
        SegmentsStructurallyChanged?.Invoke();

        Infrastructure.UndoStack.UndoManager.Shared.Push(
            new Infrastructure.UndoStack.DelegateUndoAction(
                "删除 1 个分镜",
                () =>
                {
                    var n = RestoreSegments(new[] { snapshot });
                    RebuildGroups();
                    SegmentsStructurallyChanged?.Invoke();
                    Views.Components.ToastService.Show(
                        n > 0 ? "已恢复 1 个分镜" : "恢复失败，请重试",
                        n > 0 ? Views.Components.ToastStyle.Success : Views.Components.ToastStyle.Error);
                }));
        Views.Components.ToastService.Show("已删除分镜 · Ctrl+Z 撤销", Views.Components.ToastStyle.Warning);
    }

    void ISegmentCardHost.ToggleSemanticType(SegmentCardViewModel card, SemanticType type)
    {
        ToggleSemanticType(card.Segment, type);
        card.RefreshFromSegment();
    }

    void ISegmentCardHost.UpdatePositionType(SegmentCardViewModel card, PositionType type)
    {
        UpdatePositionType(card.Segment, type);
        card.RefreshFromSegment();
    }

    void ISegmentCardHost.ToggleSelection(SegmentCardViewModel card)
    {
        ToggleSelection(card.Segment);
        card.IsChecked = SelectedSegmentIds.Contains(card.Id);
    }

    void ISegmentCardHost.SelectCard(SegmentCardViewModel card)
    {
        if (_selectedCard is { } prev && prev != card)
        {
            prev.IsSelected = false;
        }
        card.IsSelected = true;
        _selectedCard = card;
        SelectedSegment = card.Segment;
    }

}
