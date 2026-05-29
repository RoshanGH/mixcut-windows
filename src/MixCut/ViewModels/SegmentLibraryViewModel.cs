using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MixCut.Data;
using MixCut.Models;

namespace MixCut.ViewModels;

/// <summary>分镜筛选条件。对应 macOS 版 SegmentFilter。</summary>
public sealed class SegmentFilter
{
    public HashSet<SemanticType> SemanticTypes { get; } = new();
    public HashSet<PositionType> PositionTypes { get; } = new();
    public Guid? SourceVideoId { get; set; }
    public double MinQualityScore { get; set; }
    public string SearchText { get; set; } = string.Empty;
}

/// <summary>按视频分组的分镜。对应 macOS 版 VideoSegmentGroup。</summary>
public sealed record VideoSegmentGroup(Video Video, IReadOnlyList<Segment> Segments);

/// <summary>预览播放请求（微调后触发播放器跳转）。对应 macOS 版 SegmentPreviewRequest。</summary>
public sealed record SegmentPreviewRequest(Guid SegmentId, double From, double To);

/// <summary>分镜素材库统计。</summary>
public sealed record SegmentStatistics(
    int Total, IReadOnlyDictionary<SemanticType, int> ByType, double AverageQuality);

/// <summary>分镜素材库 ViewModel。对应 macOS 版 SegmentLibraryViewModel。</summary>
public partial class SegmentLibraryViewModel : ObservableObject, IDisposable
{
    private readonly IDbContextFactory<MixCutDbContext> _dbFactory;
    private readonly ILogger<SegmentLibraryViewModel> _logger;

    // 持有一个跟踪型上下文，便于分镜微调即时保存（对齐 macOS 版长生命周期 ModelContext）。
    private MixCutDbContext? _context;
    private readonly List<Segment> _segments = new();

    /// <summary>筛选后的分镜列表。</summary>
    public ObservableCollection<Segment> FilteredSegments { get; } = new();

    public SegmentFilter Filter { get; private set; } = new();

    [ObservableProperty]
    private Segment? _selectedSegment;

    [ObservableProperty]
    private bool _sortByQuality;

    [ObservableProperty]
    private bool _isGridView = true;

    /// <summary>微调后触发预览播放的信号。</summary>
    [ObservableProperty]
    private SegmentPreviewRequest? _previewRequest;

    /// <summary>当前正在播放的分镜 ID（全局唯一）。</summary>
    [ObservableProperty]
    private Guid? _playingSegmentId;

    // ===== 批量导出多选状态（对齐 macOS v0.2.4 commit 704363c）=====

    /// <summary>多选模式开关。</summary>
    [ObservableProperty]
    private bool _isSelectionMode;

    /// <summary>已选分镜 ID 集合（HashSet 包在 ObservableObject 之外，UI 通过显式 OnPropertyChanged 通知）。</summary>
    public HashSet<Guid> SelectedSegmentIds { get; } = new();

    /// <summary>
    /// 勾选顺序记录（v0.3.2 对齐 Mac c9a1e4e）。
    /// SelectedSegmentIds 是查找用的 HashSet，这里是用户实际勾选的先后顺序。
    /// 自定义组合场景必须保持勾选顺序，而批量导出场景仍用按视频+StartTime 排序的 SelectedSegments。
    /// 不变量：_selectionOrder 与 SelectedSegmentIds 始终同步（任一改动都要带上对方的改动）。
    /// </summary>
    private readonly List<Guid> _selectionOrder = new();

    /// <summary>选中数量变化通知（视图层 binding 不到 HashSet，用事件兜底刷新统计）。</summary>
    public event Action? SelectionChanged;

    /// <summary>每视频内的分镜编号映射（按 startTime 升序，每视频独立 1-based）。缓存避免反复重算。</summary>
    private Dictionary<Guid, Dictionary<Guid, int>> _numberByVideo = new();

    /// <summary>取得分镜在所属视频内的编号；找不到返回 0。</summary>
    public int NumberFor(Segment segment)
    {
        if (segment.Video is null) return 0;
        return _numberByVideo.TryGetValue(segment.Video.Id, out var map)
            && map.TryGetValue(segment.Id, out var n) ? n : 0;
    }

    public void ToggleSelection(Segment segment)
    {
        if (!SelectedSegmentIds.Add(segment.Id))
        {
            // 已选 → 取消勾选
            SelectedSegmentIds.Remove(segment.Id);
            _selectionOrder.Remove(segment.Id);
        }
        else
        {
            // 新增勾选 → 追加到顺序末尾
            _selectionOrder.Add(segment.Id);
        }
        SelectionChanged?.Invoke();
    }

    /// <summary>全选当前筛选后可见的所有分镜（按 FilteredSegments 当前显示顺序）。</summary>
    public void SelectAllVisible()
    {
        SelectedSegmentIds.Clear();
        _selectionOrder.Clear();
        foreach (var s in FilteredSegments)
        {
            if (SelectedSegmentIds.Add(s.Id))
            {
                _selectionOrder.Add(s.Id);
            }
        }
        SelectionChanged?.Invoke();
    }

    /// <summary>反选（针对当前筛选后可见的所有分镜）。新增的按 FilteredSegments 当前顺序追加。</summary>
    public void InvertSelectionVisible()
    {
        var visibleOrdered = FilteredSegments.Select(s => s.Id).ToList();
        var newSelectionSet = visibleOrdered.Where(id => !SelectedSegmentIds.Contains(id)).ToHashSet();
        SelectedSegmentIds.Clear();
        _selectionOrder.Clear();
        foreach (var id in visibleOrdered)
        {
            if (newSelectionSet.Contains(id))
            {
                SelectedSegmentIds.Add(id);
                _selectionOrder.Add(id);
            }
        }
        SelectionChanged?.Invoke();
    }

    public void ClearSelection()
    {
        SelectedSegmentIds.Clear();
        _selectionOrder.Clear();
        SelectionChanged?.Invoke();
    }

    /// <summary>进入/退出多选模式（退出时自动清空已选）。</summary>
    public void SetSelectionMode(bool enabled)
    {
        IsSelectionMode = enabled;
        if (!enabled) ClearSelection();
    }

    /// <summary>当前已选分镜列表（按视频 + StartTime 排序，导出用）。</summary>
    public IReadOnlyList<Segment> SelectedSegments
    {
        get
        {
            var selected = _segments.Where(s => SelectedSegmentIds.Contains(s.Id));
            return selected
                .OrderBy(s => s.Video?.Id.ToString() ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(s => s.StartTime)
                .ToList();
        }
    }

    /// <summary>
    /// 已选分镜，按用户勾选先后顺序（v0.3.2 对齐 Mac c9a1e4e）。
    /// 供「✨ 组合为方案」场景使用 —— SelectedSegments 是按视频+StartTime 排序的版本，给批量导出用。
    /// </summary>
    public IReadOnlyList<Segment> SelectedSegmentsInOrder
    {
        get
        {
            var segById = _segments.ToDictionary(s => s.Id);
            return _selectionOrder
                .Where(id => segById.ContainsKey(id))
                .Select(id => segById[id])
                .ToList();
        }
    }

    /// <summary>当前显示的项目（LoadSegments 时设置；供 View 取项目句柄用）。</summary>
    public Project? CurrentProject { get; private set; }

    private readonly Services.VideoProcessing.FFmpegRunner? _ffmpeg;

    public SegmentLibraryViewModel(
        IDbContextFactory<MixCutDbContext> dbFactory, ILogger<SegmentLibraryViewModel> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>DI 构造（带 ffmpeg），让分镜库视图能补生成缺失的 thumbnail。</summary>
    public SegmentLibraryViewModel(
        IDbContextFactory<MixCutDbContext> dbFactory,
        Services.VideoProcessing.FFmpegRunner ffmpeg,
        ILogger<SegmentLibraryViewModel> logger)
    {
        _dbFactory = dbFactory;
        _ffmpeg = ffmpeg;
        _logger = logger;
    }

    /// <summary>
    /// 扫所有 segment，对缺失 ThumbnailPath 的（或文件不存在的）后台串行调 ffmpeg 现场生成。
    /// 每生成一张通过 ThumbnailCache.LoadAsync 主动入 cache，事件触发 CardVM 刷新。
    /// 修复历史数据：早期版本 segment 创建时没生成 thumbnail。
    /// </summary>
    public async Task RepairMissingThumbnailsAsync()
    {
        if (_ffmpeg is null) return;
        // 快照需要修的 segments（避免长持有 _context）
        var snapshot = _segments
            .Where(s => s.Video is not null
                        && !string.IsNullOrEmpty(s.Video.LocalPath)
                        && (string.IsNullOrEmpty(s.ThumbnailPath)
                            || !System.IO.File.Exists(s.ThumbnailPath)))
            .Select(s => (s.Id, VideoPath: s.Video!.LocalPath, MidTime: (s.StartTime + s.EndTime) / 2.0))
            .ToList();
        if (snapshot.Count == 0) return;

        var thumbDir = Utilities.AppPaths.Root + @"\Thumbnails";
        System.IO.Directory.CreateDirectory(thumbDir);
        _logger.LogInformation("[ThumbRepair] 需要补生成 {N} 个 segment 缩略图", snapshot.Count);

        // 串行执行，避免吃 CPU（每次 ffmpeg 几十毫秒，30 个加起来 ~3s）
        foreach (var (segId, videoPath, midTime) in snapshot)
        {
            try
            {
                var thumbPath = System.IO.Path.Combine(thumbDir, $"seg_{segId}.jpg");
                await _ffmpeg.GenerateThumbnailAsync(videoPath, thumbPath, midTime);
                // 写回 DB
                await using (var db = await _dbFactory.CreateDbContextAsync())
                {
                    var seg = await db.Segments.FirstOrDefaultAsync(x => x.Id == segId);
                    if (seg is not null)
                    {
                        seg.ThumbnailPath = thumbPath;
                        await db.SaveChangesAsync();
                    }
                }
                // 更新内存 segments + 通知 cache（CardVM 监听 ImageLoaded 会自动刷新 binding）
                var memSeg = _segments.FirstOrDefault(s => s.Id == segId);
                if (memSeg is not null) memSeg.ThumbnailPath = thumbPath;
                _ = Infrastructure.ThumbnailCache.Shared.LoadAsync(thumbPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[ThumbRepair] segment {Id} 缩略图生成失败", segId);
            }
        }
        _logger.LogInformation("[ThumbRepair] 完成");
    }

    /// <summary>加载项目的所有分镜。</summary>
    public void LoadSegments(Project project)
    {
        _context?.Dispose();
        _context = _dbFactory.CreateDbContext();
        CurrentProject = project;

        var projectId = project.Id;
        var segs = _context.Segments
            .Include(s => s.Video)
            .Where(s => s.Video != null
                        && s.Video.ProjectVideos.Any(pv => pv.ProjectId == projectId))
            .ToList();

        _segments.Clear();
        _segments.AddRange(segs);
        RecomputeNumberByVideo();
        ApplyFilter();
    }

    /// <summary>
    /// 计算每个视频内分镜的 1-based 编号（按 StartTime 升序）。
    /// 用于批量导出时的文件命名「{N}_{video}.mp4」 + 卡片左上角 #N 徽章。
    /// 对齐 macOS recomputeNumberByVideo。
    /// </summary>
    private void RecomputeNumberByVideo()
    {
        var result = new Dictionary<Guid, Dictionary<Guid, int>>();
        var byVideo = new Dictionary<Guid, List<Segment>>();
        foreach (var seg in _segments)
        {
            if (seg.Video is null) continue;
            if (!byVideo.TryGetValue(seg.Video.Id, out var list))
            {
                list = new List<Segment>();
                byVideo[seg.Video.Id] = list;
            }
            list.Add(seg);
        }
        foreach (var (videoId, segs) in byVideo)
        {
            var sorted = segs.OrderBy(s => s.StartTime).ToList();
            var map = new Dictionary<Guid, int>();
            for (var i = 0; i < sorted.Count; i++)
            {
                map[sorted[i].Id] = i + 1;
            }
            result[videoId] = map;
        }
        _numberByVideo = result;
    }

    /// <summary>批量删除当前选中的分镜。对齐 macOS deleteSelectedSegments。</summary>
    public void DeleteSelectedSegments()
    {
        if (_context is null) return;
        var idsToDelete = SelectedSegmentIds.ToHashSet();
        if (idsToDelete.Count == 0) return;

        var segs = _segments.Where(s => idsToDelete.Contains(s.Id)).ToList();
        foreach (var seg in segs)
        {
            if (SelectedSegment?.Id == seg.Id) SelectedSegment = null;
            var tracked = _context.Segments.FirstOrDefault(s => s.Id == seg.Id);
            if (tracked is not null)
            {
                _context.Segments.Remove(tracked);
            }
        }
        try
        {
            _context.SaveChanges();
        }
        catch (Exception ex)
        {
            _logger.LogError("批量删除分镜保存失败: {Msg}", ex.Message);
        }
        _segments.RemoveAll(s => idsToDelete.Contains(s.Id));
        SelectedSegmentIds.Clear();
        _selectionOrder.Clear();
        SelectionChanged?.Invoke();
        RecomputeNumberByVideo();
        ApplyFilter();
    }

    /// <summary>
    /// 按语义类型统计分镜数量（用于类型筛选 chip 显示徽章 + 0 数量降饱和）。
    /// 对齐 macOS 版 countByType。
    /// </summary>
    public IReadOnlyDictionary<SemanticType, int> CountByType()
    {
        var dict = new Dictionary<SemanticType, int>();
        foreach (var type in SemanticTypeExtensions.All)
        {
            dict[type] = 0;
        }
        foreach (var seg in _segments)
        {
            foreach (var t in seg.SemanticTypes)
            {
                dict[t] = dict.GetValueOrDefault(t) + 1;
            }
        }
        return dict;
    }

    /// <summary>请求播放某个分镜（自动停止其他播放）。</summary>
    public void RequestPlay(Segment segment, double? from = null, double? to = null)
    {
        PlayingSegmentId = segment.Id;
        PreviewRequest = new SegmentPreviewRequest(
            segment.Id, from ?? segment.StartTime, to ?? segment.EndTime);
    }

    /// <summary>停止当前播放。</summary>
    public void StopCurrentPlayback() => PlayingSegmentId = null;

    /// <summary>应用筛选条件。</summary>
    public void ApplyFilter()
    {
        IEnumerable<Segment> result = _segments;

        if (Filter.SemanticTypes.Count > 0)
        {
            result = result.Where(s => s.SemanticTypes.Any(t => Filter.SemanticTypes.Contains(t)));
        }
        if (Filter.PositionTypes.Count > 0)
        {
            result = result.Where(s => Filter.PositionTypes.Contains(s.PositionType));
        }
        if (Filter.SourceVideoId is { } videoId)
        {
            result = result.Where(s => s.Video?.Id == videoId);
        }
        if (Filter.MinQualityScore > 0)
        {
            result = result.Where(s => s.QualityScore >= Filter.MinQualityScore);
        }
        if (!string.IsNullOrEmpty(Filter.SearchText))
        {
            var query = Filter.SearchText.ToLowerInvariant();
            result = result.Where(s =>
                s.Text.ToLowerInvariant().Contains(query) ||
                s.Keywords.Any(k => k.ToLowerInvariant().Contains(query)));
        }

        // 默认排序：先按视频原始导入顺序，组内按 StartTime。
        // 这样 RebuildGroups 按出现顺序分组时能直接拿到正确的视频排列 + 组内时序。
        // SortByQuality 模式下仍按全局质量降序排（用户主动选了"按质量"，不再分组）。
        if (SortByQuality)
        {
            result = result.OrderByDescending(s => s.QualityScore);
        }
        else
        {
            // 按视频首次出现顺序构建索引（保留 _segments 的导入顺序）
            var videoOrder = new Dictionary<Guid, int>();
            foreach (var s in _segments)
            {
                if (s.Video is null) continue;
                if (!videoOrder.ContainsKey(s.Video.Id))
                {
                    videoOrder[s.Video.Id] = videoOrder.Count;
                }
            }
            result = result
                .OrderBy(s => s.Video is null ? int.MaxValue
                            : videoOrder.TryGetValue(s.Video.Id, out var idx) ? idx : int.MaxValue)
                .ThenBy(s => s.StartTime);
        }

        FilteredSegments.Clear();
        foreach (var seg in result)
        {
            FilteredSegments.Add(seg);
        }
    }

    /// <summary>按视频分组的筛选结果。</summary>
    public IReadOnlyList<VideoSegmentGroup> GroupedSegments()
    {
        var map = new Dictionary<Guid, (Video Video, List<Segment> Segments)>();
        var order = new List<Guid>();

        foreach (var seg in FilteredSegments)
        {
            if (seg.Video is null)
            {
                continue;
            }
            if (!map.ContainsKey(seg.Video.Id))
            {
                map[seg.Video.Id] = (seg.Video, new List<Segment>());
                order.Add(seg.Video.Id);
            }
            map[seg.Video.Id].Segments.Add(seg);
        }

        return order.Select(id => new VideoSegmentGroup(map[id].Video, map[id].Segments)).ToList();
    }

    /// <summary>切换分镜的语义类型（多选：添加或移除，至少保留一个）。</summary>
    public void ToggleSemanticType(Segment segment, SemanticType type)
    {
        var types = segment.SemanticTypes.ToList();
        var idx = types.IndexOf(type);
        if (idx >= 0)
        {
            if (types.Count > 1)
            {
                types.RemoveAt(idx);
            }
        }
        else
        {
            types.Add(type);
        }
        segment.SemanticTypes = types;
        Save();
        ApplyFilter();
    }

    /// <summary>更新分镜的位置类型。</summary>
    public void UpdatePositionType(Segment segment, PositionType newType)
    {
        segment.PositionType = newType;
        Save();
        ApplyFilter();
    }

    /// <summary>重置筛选。</summary>
    public void ResetFilter()
    {
        Filter = new SegmentFilter();
        ApplyFilter();
    }

    // ---- 边界微调 ----

    /// <summary>调整开始时间（步进），调整后播放新开始处 2 秒。</summary>
    public void AdjustStartTime(Segment segment, double step)
    {
        var newStart = Math.Max(0, segment.StartTime + step);
        if (newStart >= segment.EndTime - 0.2)
        {
            return;
        }
        segment.StartTime = newStart;
        ReExtractText(segment);
        Save();
        RequestPlay(segment, newStart, Math.Min(newStart + 2, segment.EndTime));
    }

    /// <summary>调整结束时间（步进），调整后播放结束前 1 秒。</summary>
    public void AdjustEndTime(Segment segment, double step)
    {
        var videoDuration = segment.Video?.Duration ?? double.MaxValue;
        var newEnd = Math.Min(videoDuration, segment.EndTime + step);
        if (newEnd <= segment.StartTime + 0.2)
        {
            return;
        }
        segment.EndTime = newEnd;
        ReExtractText(segment);
        Save();
        RequestPlay(segment, Math.Max(segment.StartTime, newEnd - 1), newEnd);
    }

    /// <summary>直接设置开始时间。</summary>
    public void SetStartTime(Segment segment, double newStart)
    {
        var clamped = Math.Max(0, newStart);
        if (clamped >= segment.EndTime - 0.2)
        {
            return;
        }
        segment.StartTime = clamped;
        ReExtractText(segment);
        Save();
        RequestPlay(segment, clamped, Math.Min(clamped + 2, segment.EndTime));
    }

    /// <summary>直接设置结束时间。</summary>
    public void SetEndTime(Segment segment, double newEnd)
    {
        var videoDuration = segment.Video?.Duration ?? double.MaxValue;
        var clamped = Math.Min(videoDuration, newEnd);
        if (clamped <= segment.StartTime + 0.2)
        {
            return;
        }
        segment.EndTime = clamped;
        ReExtractText(segment);
        Save();
        RequestPlay(segment, Math.Max(segment.StartTime, clamped - 1), clamped);
    }

    /// <summary>根据当前时间范围重新从 ASR 提取台词（中心点匹配，避免跨段重复）。</summary>
    private static void ReExtractText(Segment segment)
    {
        var video = segment.Video;
        if (video is null)
        {
            return;
        }
        var matched = video.AsrWords
            .Where(w => (w.Start + w.End) / 2 >= segment.StartTime
                        && (w.Start + w.End) / 2 < segment.EndTime)
            .Select(w => w.Word);
        var text = string.Concat(matched).Trim();
        if (text.Length > 0)
        {
            segment.Text = text;
        }
    }

    /// <summary>删除分镜。</summary>
    public void DeleteSegment(Segment segment)
    {
        if (SelectedSegment?.Id == segment.Id)
        {
            SelectedSegment = null;
        }
        if (_context is not null)
        {
            _context.Segments.Remove(segment);
            _context.SaveChanges();
        }
        _segments.RemoveAll(s => s.Id == segment.Id);
        ApplyFilter();
    }

    /// <summary>统计信息。</summary>
    public SegmentStatistics Statistics()
    {
        var byType = new Dictionary<SemanticType, int>();
        foreach (var seg in _segments)
        {
            foreach (var t in seg.SemanticTypes)
            {
                byType[t] = byType.GetValueOrDefault(t) + 1;
            }
        }
        var avg = _segments.Count == 0 ? 0 : _segments.Sum(s => s.QualityScore) / _segments.Count;
        return new SegmentStatistics(_segments.Count, byType, avg);
    }

    private void Save()
    {
        try
        {
            _context?.SaveChanges();
        }
        catch (Exception ex)
        {
            _logger.LogError("分镜保存失败: {Message}", ex.Message);
        }
    }

    public void Dispose()
    {
        _context?.Dispose();
        _context = null;
    }
}
