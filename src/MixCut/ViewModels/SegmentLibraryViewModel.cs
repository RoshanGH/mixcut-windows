using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MixCut.Data;
using MixCut.Models;
using MixCut.Utilities;

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

    /// <summary>当前项目的全部分镜（未经筛选）。空态要靠它区分「一个都没有」和「被筛选光了」。</summary>
    public IReadOnlyList<Segment> AllSegments => _segments;

    /// <summary>筛选后的分镜列表。</summary>
    public ObservableCollection<Segment> FilteredSegments { get; } = new();

    public SegmentFilter Filter { get; private set; } = new();

    [ObservableProperty]
    private Segment? _selectedSegment;

    /// <summary>配音变体检视器 VM（v0.5.0）；单击分镜后由 SelectCard 加载。DI 注入 DubbingViewModel 时非空。</summary>
    public DubVariantInspectorViewModel? DubInspector { get; private set; }

    /// <summary>配音编排（v0.5.0）；RebuildGroups 传给各视频组的「配音设置条」。</summary>
    private DubbingViewModel? _dubbing;

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
    /// 为「批量导出」构建变体任务列表：按当前选中分镜从库里<b>重新加载（含 Video + SegmentDubs）</b>，
    /// 再展开成「原版 + 各已生成变体」。必须重载 dubs —— <see cref="LoadSegments"/> 只 Include 了 Video，
    /// 直接读 <c>EffectiveDubVariants</c> 拿不到变体（会退化成只导原版）。对齐 mac VariantExportInput.from。
    /// </summary>
    public IReadOnlyList<Services.Export.VariantExportJob> BuildVariantExportJobs()
    {
        var orderedSelected = SelectedSegments;   // 已按视频 + StartTime 排序
        var ids = orderedSelected.Select(s => s.Id).ToList();
        if (ids.Count == 0) return Array.Empty<Services.Export.VariantExportJob>();

        using var db = _dbFactory.CreateDbContext();
        var fresh = db.Segments
            .Include(s => s.Video)
            .Include(s => s.SegmentDubs)
            .Where(s => ids.Contains(s.Id))
            .ToList()
            .ToDictionary(s => s.Id);

        // 保持选中排序；NumberFor 按 Video.Id + Segment.Id 取号，对新实例同样有效。
        var segsInOrder = orderedSelected
            .Where(s => fresh.ContainsKey(s.Id))
            .Select(s => fresh[s.Id])
            .ToList();
        return Services.Export.VariantExportInput.From(segsInOrder, NumberFor);
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
        DubbingViewModel dubbing,
        ILogger<SegmentLibraryViewModel> logger)
    {
        _dbFactory = dbFactory;
        _ffmpeg = ffmpeg;
        _dubbing = dubbing;
        _logger = logger;
        DubInspector = new DubVariantInspectorViewModel(dubbing);
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
            // v0.3.1 对齐：分镜缩略图改用「首帧」(startTime + 100ms)，避开转场/黑帧。
            .Select(s => (s.Id, VideoPath: s.Video!.LocalPath, FirstFrameTime: Math.Max(0, s.StartTime + 0.1)))
            .ToList();
        if (snapshot.Count == 0) return;

        var thumbDir = Utilities.AppPaths.Root + @"\Thumbnails";
        System.IO.Directory.CreateDirectory(thumbDir);
        _logger.LogInformation("[ThumbRepair] 需要补生成 {N} 个 segment 缩略图", snapshot.Count);

        // 串行执行，避免吃 CPU（每次 ffmpeg 几十毫秒，30 个加起来 ~3s）
        foreach (var (segId, videoPath, firstFrameTime) in snapshot)
        {
            try
            {
                var thumbPath = System.IO.Path.Combine(thumbDir, $"seg_{segId}.jpg");
                await _ffmpeg.GenerateThumbnailAsync(videoPath, thumbPath, firstFrameTime);
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
        // 切项目铁律：重置单选 + 配音检视器选中态，不让旧项目状态串过来。
        _selectedCard = null;
        DubInspector?.Clear();

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
        RecomputeVideoNumbers(projectId);
        ApplyFilter();
    }

    /// <summary>视频编号（issue #23）：视频 Id → 项目内按导入时间（ProjectVideo.AddedAt）升序的 1-based 号。
    /// 与素材导入页徽章、Agent 的 video_no 同一套规则；覆盖全部视频（含自建分镜载体）。</summary>
    private Dictionary<Guid, int> _videoNoById = new();

    private void RecomputeVideoNumbers(Guid projectId)
    {
        try
        {
            if (_context is null) { _videoNoById = new(); return; }
            var ids = _context.ProjectVideos
                .Where(pv => pv.ProjectId == projectId && pv.VideoId != null)
                .OrderBy(pv => pv.AddedAt)
                .Select(pv => pv.VideoId!.Value)
                .ToList();
            _videoNoById = ids.Select((id, idx) => (id, idx)).ToDictionary(x => x.id, x => x.idx + 1);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SegLib] 计算视频编号失败");
            _videoNoById = new();
        }
    }

    /// <summary>某视频在当前项目内的编号；未知返回 0（组头徽章隐藏）。</summary>
    public int VideoNoFor(Guid videoId) => _videoNoById.GetValueOrDefault(videoId);

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

    /// <summary>
    /// 批量删除选中分镜。返回被删分镜的快照（供 P0-10 撤销）；返回 null 表示保存失败。
    /// <para>P0-16：① 显式事务包裹（SQLite 下全删或全不删）；② 保存失败时<b>不</b>改内存集合，
    /// 避免「DB 没删成功但 UI 显示删了」的不一致，由调用方据返回值提示，杜绝静默假成功。</para>
    /// <para>P0-10：删除前 clone 一份脱离 EF 跟踪的快照返回，调用方压入 UndoManager 供 Ctrl+Z 恢复。</para>
    /// </summary>
    public IReadOnlyList<Segment>? DeleteSelectedSegments()
    {
        if (_context is null) return null;
        var idsToDelete = SelectedSegmentIds.ToHashSet();
        if (idsToDelete.Count == 0) return Array.Empty<Segment>();

        var segs = _segments.Where(s => idsToDelete.Contains(s.Id)).ToList();
        // P0-10：删除前先快照（脱离导航属性，仅标量 + VideoId），供撤销重建。
        var snapshots = segs.Select(Infrastructure.UndoStack.UndoClone.CloneSegment).ToList();
        try
        {
            using var tx = _context.Database.BeginTransaction();
            // 一次查全部待删行，而不是每个分镜发一次主键 SELECT ——「全选 → 删除」在上百分镜的
            // 项目里就是上百次串行往返，用户看到的是点完删除后界面顿住。
            var tracked = _context.Segments.Where(s => idsToDelete.Contains(s.Id)).ToList();
            _context.Segments.RemoveRange(tracked);
            _context.SaveChanges();
            tx.Commit();
        }
        catch (Exception ex)
        {
            // 保存失败：DB 已回滚，内存集合保持原样 → UI 仍显示这些分镜（与 DB 一致）。
            _logger.LogError("批量删除分镜保存失败，已回滚，内存不变: {Msg}", ex.Message);
            return null;
        }

        // 仅在 DB 删除确认成功后才同步内存与选中态。
        foreach (var seg in segs)
        {
            if (SelectedSegment?.Id == seg.Id) SelectedSegment = null;
        }
        _segments.RemoveAll(s => idsToDelete.Contains(s.Id));
        SelectedSegmentIds.Clear();
        _selectionOrder.Clear();
        SelectionChanged?.Invoke();
        RecomputeNumberByVideo();
        ApplyFilter();
        return snapshots;
    }

    /// <summary>
    /// P0-10：撤销分镜删除 —— 把快照重新插回 DB 并重查列表。返回实际恢复的数量。
    /// 仅恢复分镜实体本身（标量 + VideoId），不恢复级联删掉的 SchemeSegment 链接
    /// （用户通常先删分镜再生成方案；恢复珍贵的 AI 分析结果才是核心诉求）。
    /// </summary>
    public int RestoreSegments(IReadOnlyList<Segment> snapshots)
    {
        if (_context is null || CurrentProject is null || snapshots.Count == 0)
        {
            return 0;
        }
        var restored = 0;
        try
        {
            using var tx = _context.Database.BeginTransaction();
            foreach (var snap in snapshots)
            {
                // 防重：DB 已存在同 Id（极端情况）跳过，避免主键冲突。
                if (_context.Segments.Any(s => s.Id == snap.Id))
                {
                    continue;
                }
                _context.Segments.Add(Infrastructure.UndoStack.UndoClone.CloneSegment(snap));
                restored++;
            }
            _context.SaveChanges();
            tx.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogError("[Undo] 恢复分镜失败，已回滚: {Msg}", ex.Message);
            return 0;
        }
        // 只重查恢复的这几个（带 Video 导航），用当前 _context 把同源实例加回内存集合。
        // 关键：绝不调 LoadSegments —— 它会 dispose 旧 _context、换一个全新 context，
        // 导致页面上已存在的卡片仍抱着旧 context 的死实例。撤销后再删 / 再微调那些卡片时，
        // 会拿死实例去新 context 上 Remove/Save，撞「同主键已被另一实例跟踪」而失败
        // （用户报的「删一个→撤销→再删第二个提示删除失败」的根因）。
        var ids = snapshots.Select(s => s.Id).ToHashSet();
        var restoredEntities = _context.Segments
            .Include(s => s.Video)
            .Where(s => ids.Contains(s.Id))
            .ToList();
        foreach (var entity in restoredEntities)
        {
            if (_segments.All(s => s.Id != entity.Id))
            {
                _segments.Add(entity);
            }
        }
        RecomputeNumberByVideo();
        ApplyFilter();
        return restored;
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
            // 性能：用 OrdinalIgnoreCase 比较替代 ToLowerInvariant().Contains() —— 后者对每个
            // segment 的 Text + 每个 Keyword 都分配一个小写字符串副本，几百个分镜 × 每次按键
            // 全量重算会卡顿。OrdinalIgnoreCase 零分配，对中英文搜索结果等价。
            var query = Filter.SearchText;
            result = result.Where(s =>
                s.Text.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                s.Keywords.Any(k => k.Contains(query, StringComparison.OrdinalIgnoreCase)));
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
        // 排序也属于「当前视图的筛选状态」，切项目时一并归位，
        // 否则新项目会沿用上个项目的「按质量排序」而工具栏下拉显示的却是「按时间」。
        SortByQuality = false;
        ApplyFilter();
    }

    // ---- 边界微调 ----

    /// <summary>兼容旧调用：把秒步长换算为帧步长。</summary>
    public void AdjustStartTime(Segment segment, double step)
    {
        var fps = EffectiveEditFps(segment);
        var delta = fps > 0 ? Math.Sign(step) * Math.Max(1, Math.Abs(FrameTime.SecondsToFrame(step, fps))) : 0;
        if (delta != 0) AdjustStartFrame(segment, delta);
    }

    /// <summary>兼容旧调用：把秒步长换算为帧步长。</summary>
    public void AdjustEndTime(Segment segment, double step)
    {
        var fps = EffectiveEditFps(segment);
        var delta = fps > 0 ? Math.Sign(step) * Math.Max(1, Math.Abs(FrameTime.SecondsToFrame(step, fps))) : 0;
        if (delta != 0) AdjustEndFrame(segment, delta);
    }

    /// <summary>起点移动指定帧数；相邻上一分镜的终点同步移动，避免重叠或裂缝。</summary>
    public void AdjustStartFrame(Segment segment, int deltaFrames)
    {
        if (deltaFrames == 0) return;
        if (!ApplyFrameEdit(segment, segment.StartFrame + deltaFrames, segment.EndFrame))
        {
            return;
        }
        RequestPlay(segment, segment.StartTime, Math.Min(segment.StartTime + 2, segment.EndTime));
    }

    /// <summary>终点移动指定帧数；相邻下一分镜的起点同步移动，保持整条时间轴连续。</summary>
    public void AdjustEndFrame(Segment segment, int deltaFrames)
    {
        if (deltaFrames == 0) return;
        if (!ApplyFrameEdit(segment, segment.StartFrame, segment.EndFrame + deltaFrames))
        {
            return;
        }
        RequestPlay(segment, Math.Max(segment.StartTime, segment.EndTime - 1), segment.EndTime);
    }

    /// <summary>直接设置开始时间。</summary>
    public void SetStartTime(Segment segment, double newStart)
    {
        var fps = EffectiveEditFps(segment);
        if (fps <= 0) return;
        if (!ApplyFrameEdit(segment, FrameTime.SecondsToFrame(newStart, fps), segment.EndFrame))
        {
            return;
        }
        RequestPlay(segment, segment.StartTime, Math.Min(segment.StartTime + 2, segment.EndTime));
    }

    /// <summary>直接设置结束时间。</summary>
    public void SetEndTime(Segment segment, double newEnd)
    {
        var fps = EffectiveEditFps(segment);
        if (fps <= 0) return;
        if (!ApplyFrameEdit(segment, segment.StartFrame, FrameTime.SecondsToFrame(newEnd, fps)))
        {
            return;
        }
        RequestPlay(segment, Math.Max(segment.StartTime, segment.EndTime - 1), segment.EndTime);
    }

    /// <summary>
    /// 帧级边界编辑。分镜边界是共享边界：改当前起点会同步上一条终点，改当前终点会同步下一条起点。
    /// 保存失败时所有受影响分镜整体回滚，绝不留下内存与数据库不一致。
    /// </summary>
    private bool ApplyFrameEdit(Segment segment, int newStartFrame, int newEndFrame)
    {
        var fps = EffectiveEditFps(segment);
        if (fps <= 0) return false;

        var sameVideo = _segments
            .Where(s => s.VideoId == segment.VideoId)
            .OrderBy(s => s.StartFrame)
            .ToList();
        var index = sameVideo.FindIndex(s => s.Id == segment.Id);
        var previous = index > 0 ? sameVideo[index - 1] : null;
        var next = index >= 0 && index + 1 < sameVideo.Count ? sameVideo[index + 1] : null;
        var maxFrame = segment.Video?.Duration > 0
            ? FrameTime.SecondsToFrame(segment.Video.Duration, fps)
            : int.MaxValue;

        newStartFrame = Math.Max(0, newStartFrame);
        newEndFrame = Math.Min(maxFrame, newEndFrame);
        if (newStartFrame >= newEndFrame) return false;
        if (previous is not null && newStartFrame <= previous.StartFrame) return false;
        if (next is not null && newEndFrame >= next.EndFrame) return false;

        var affected = new List<Segment> { segment };
        if (previous is not null && newStartFrame != segment.StartFrame) affected.Add(previous);
        if (next is not null && newEndFrame != segment.EndFrame) affected.Add(next);
        var snapshots = affected.ToDictionary(
            s => s.Id,
            s => (s.StartFrame, s.EndFrame, s.StartTime, s.EndTime, s.Fps, s.Text));

        segment.SetBoundsFrames(newStartFrame, newEndFrame, fps);
        if (previous is not null && newStartFrame != snapshots[segment.Id].StartFrame)
        {
            previous.SetBoundsFrames(previous.StartFrame, newStartFrame, fps);
        }
        if (next is not null && newEndFrame != snapshots[segment.Id].EndFrame)
        {
            next.SetBoundsFrames(newEndFrame, next.EndFrame, fps);
        }
        foreach (var item in affected) ReExtractText(item);

        // R3（对齐 mac setFrameRange→invalidateReplacedPicture）：改了分镜边界 → 帧数已变，
        // 旧 AI 替换画面片与新边界不再匹配，作废之，防导出/预览用到过时画面。
        var pictureInvalidated = new List<Segment>();
        foreach (var item in affected)
        {
            if (!string.IsNullOrEmpty(item.ReplacedPictureVideoPath))
            {
                item.InvalidateReplacedPicture();
                pictureInvalidated.Add(item);
            }
        }

        if (Save())
        {
            _logger.LogInformation(
                "[FrameEditDiag] segment={Segment} start={StartFrame} end={EndFrame} fps={Fps:F3} affected={Affected} 作废替换画面={Invalidated}",
                segment.Id, segment.StartFrame, segment.EndFrame, fps, affected.Count, pictureInvalidated.Count);
            // 作废了替换画面的分镜：刷新其卡片（切换画面胶囊消失、缩略图回原画面）。
            foreach (var item in pictureInvalidated)
            {
                if (_cardIndex.Values.FirstOrDefault(c => c.Segment.Id == item.Id) is { } card)
                {
                    card.RefreshFromSegment();
                }
            }
            // issue #19：开始帧变化的分镜要按新首帧重抽缩略图（首帧 = StartFrame 处那一帧）。
            // 只有起点动了才影响首帧：segment 自己被调开始、或 next 被同步了起点都在此列；
            // previous 只动终点、首帧不变，天然不满足下面的条件。结束边界调整不重抽。
            foreach (var item in affected)
            {
                if (item.StartFrame != snapshots[item.Id].StartFrame)
                {
                    ScheduleFirstFrameRethumb(item);
                }
            }
            return true;
        }

        foreach (var item in affected)
        {
            var old = snapshots[item.Id];
            item.StartFrame = old.StartFrame;
            item.EndFrame = old.EndFrame;
            item.StartTime = old.StartTime;
            item.EndTime = old.EndTime;
            item.Fps = old.Fps;
            item.Text = old.Text;
        }
        return false;
    }

    private static double EffectiveEditFps(Segment segment) =>
        segment.EffectiveFps > 0 ? segment.EffectiveFps : 30.0;

    /// <summary>每张卡的首帧重抽 debounce：连点 ±帧时取消上一次未落地的重抽，只在停手后抽一次。</summary>
    private readonly Dictionary<Guid, CancellationTokenSource> _rethumbCts = new();

    /// <summary>
    /// issue #19：调「开始」边界后按新 StartFrame 重抽首帧缩略图、写回 ThumbnailPath，让卡片首帧预览跟着变。
    /// 之前调开始边界从不重抽首帧、且缩略图文件名固定（seg_{id}.jpg），ThumbnailPath 值不变 → 卡片 INPC
    /// 不触发 → 首帧「怎么调都不变」。这里文件名带 startFrame 保证路径唯一，值一变卡片就刷新。
    /// 带 ~400ms per-segment debounce，连点微调不会反复抽帧卡顿。
    /// </summary>
    private void ScheduleFirstFrameRethumb(Segment segment)
    {
        if (_ffmpeg is null) return;
        var videoPath = segment.Video?.LocalPath;
        if (string.IsNullOrEmpty(videoPath)) return;

        // debounce：取消该分镜上一次尚未落地的重抽。
        if (_rethumbCts.TryGetValue(segment.Id, out var prev))
        {
            prev.Cancel();
            prev.Dispose();
        }
        var cts = new CancellationTokenSource();
        _rethumbCts[segment.Id] = cts;
        _ = RethumbAfterDelayAsync(segment.Id, videoPath!, segment.StartFrame, EffectiveEditFps(segment), cts.Token);
    }

    private async Task RethumbAfterDelayAsync(Guid segId, string videoPath, int startFrame, double fps, CancellationToken token)
    {
        try
        {
            await Task.Delay(400, token);   // 停手 400ms 才真的抽帧
            if (token.IsCancellationRequested) return;

            var timeSec = Utilities.FrameTime.FrameToSeconds(startFrame, fps);
            var thumbDir = System.IO.Path.Combine(Utilities.AppPaths.Root, "Thumbnails");
            System.IO.Directory.CreateDirectory(thumbDir);
            // 文件名带 startFrame → 路径唯一 → ThumbnailPath 一变卡片就刷新（对齐 mac「缩略图路径带 startFrame」）。
            var newPath = System.IO.Path.Combine(thumbDir, $"seg_{segId}_f{startFrame}.jpg");
            if (!System.IO.File.Exists(newPath))
            {
                await _ffmpeg!.GenerateThumbnailAsync(videoPath, newPath, timeSec);
            }
            if (token.IsCancellationRequested || !System.IO.File.Exists(newPath)) return;

            // 写回 DB（短上下文）。
            await using (var db = await _dbFactory.CreateDbContextAsync())
            {
                var seg = await db.Segments.FirstOrDefaultAsync(x => x.Id == segId);
                if (seg is not null)
                {
                    seg.ThumbnailPath = newPath;
                    await db.SaveChangesAsync();
                }
            }
            // 同步内存 segment + 预热 cache + 刷新卡片（UI 线程）。
            var memSeg = _segments.FirstOrDefault(s => s.Id == segId);
            if (memSeg is not null) memSeg.ThumbnailPath = newPath;
            _ = Infrastructure.ThumbnailCache.Shared.LoadAsync(newPath);

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            void Refresh()
            {
                if (_cardIndex.Values.FirstOrDefault(c => c.Segment.Id == segId) is { } card)
                {
                    card.RefreshFromSegment();
                }
            }
            if (dispatcher is null || dispatcher.CheckAccess()) Refresh();
            else dispatcher.Invoke(Refresh);

            _logger.LogInformation("[ThumbRethumb] segment={Id} startFrame={Frame} → {Path}", segId, startFrame, newPath);
        }
        catch (OperationCanceledException) { /* 被后续微调取消，属正常 debounce */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ThumbRethumb] segment {Id} 首帧重抽失败", segId);
        }
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

    /// <summary>
    /// 删除单个分镜。返回 false 表示 DB 保存失败（调用方应提示重试、不入撤销栈）。
    /// 按 Id 取当前 _context 跟踪的实例再删（而非直接删传入实例）—— 对齐批量删除，
    /// 避免传入的是旧 context 死实例时撞「同主键已被另一实例跟踪」。
    /// </summary>
    public bool DeleteSegment(Segment segment)
    {
        if (_context is null) return false;
        var tracked = _context.Segments.FirstOrDefault(s => s.Id == segment.Id);
        if (tracked is not null)
        {
            try
            {
                _context.Segments.Remove(tracked);
                _context.SaveChanges();
            }
            catch (Exception ex)
            {
                // DB 保存失败：内存不动，UI 与 DB 仍一致；返回 false 让调用方人话提示 + 重试。
                _logger.LogError("删除分镜保存失败: {Msg}", ex.Message);
                return false;
            }
        }
        // DB 删除确认成功（或 DB 已无此分镜）后再同步内存与选中态。
        if (SelectedSegment?.Id == segment.Id)
        {
            SelectedSegment = null;
        }
        _segments.RemoveAll(s => s.Id == segment.Id);
        ApplyFilter();
        return true;
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

    /// <summary>持久化当前改动。返回 false 表示保存失败（调用方应回滚内存值，避免 UI 与 DB 不一致）。</summary>
    private bool Save()
    {
        try
        {
            _context?.SaveChanges();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError("分镜保存失败: {Message}", ex.Message);
            return false;
        }
    }

    public void Dispose()
    {
        _context?.Dispose();
        _context = null;
    }
}
