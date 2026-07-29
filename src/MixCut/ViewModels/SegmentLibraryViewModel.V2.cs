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

    /// <summary>
    /// 调 IN/OUT 帧边界后请求「境界预览播放」（对齐 Mac：调开头→从起点自动播几秒；调结尾→从末尾前几秒播到最后，
    /// 便于查看调整结果）。bool=isStart。VM 只发意图，实际懒创建/复用卡片内 InlineVideoPlayer 播放窗口由 View 处理
    /// （播放器活在可视树里、VM 无引用）。与 <see cref="ShowBoundaryFrame"/> 的静止帧互补：静止帧即时反馈、播放看动态。
    /// </summary>
    public event Action<SegmentCardViewModel, bool>? BoundaryPreviewRequested;

    // ============ V2 数据装载 ============

    /// <summary>#17：自建分镜（IsUserUploaded 载体视频）聚成一个置顶组；其余按视频分组。</summary>
    private sealed record GroupSpec(Video Video, List<Segment> Segs, bool IsUserGroup);

    /// <summary>#17/#18：处理中的分镜（segId → 阶段文案），驱动卡片 loading 占位遮罩。</summary>
    private readonly Dictionary<Guid, string> _processingSegments = new();

    /// <summary>标记/清除某分镜的处理中状态（stage=null 清除），并即时刷新对应卡片的 loading 遮罩。</summary>
    public void SetProcessing(Guid segmentId, string? stage)
    {
        if (string.IsNullOrEmpty(stage)) _processingSegments.Remove(segmentId);
        else _processingSegments[segmentId] = stage;
        if (_cardIndex.TryGetValue(segmentId, out var card))
        {
            card.IsProcessing = !string.IsNullOrEmpty(stage);
            card.ProcessingText = stage ?? string.Empty;
        }
    }

    /// <summary>把筛选后的分镜切成「自建分镜置顶组 + 各视频普通组」的规格列表。</summary>
    private List<GroupSpec> BuildGroupSpecs(List<Segment> filtered)
    {
        var specs = new List<GroupSpec>();

        // 自建分镜置顶（非空才产出；组内可含多个载体视频，合并成一组）。
        var userSegs = filtered.Where(s => s.Video?.IsUserUploaded == true).ToList();
        if (userSegs.Count > 0)
        {
            specs.Add(new GroupSpec(userSegs[0].Video!, userSegs, true));
        }

        // 其余普通分镜按视频分组（保留视频出现顺序）。
        var videoIndex = new Dictionary<Guid, int>();
        foreach (var seg in filtered)
        {
            if (seg.Video is null || seg.Video.IsUserUploaded) continue;
            if (!videoIndex.TryGetValue(seg.Video.Id, out var idx))
            {
                videoIndex[seg.Video.Id] = specs.Count;
                specs.Add(new GroupSpec(seg.Video, new List<Segment> { seg }, false));
            }
            else
            {
                specs[idx].Segs.Add(seg);
            }
        }
        return specs;
    }

    /// <summary>为一组分镜复用/创建 CardVM（同步多选 + 序号），用到的 id 记入 <paramref name="newCardIds"/>。</summary>
    private List<SegmentCardViewModel> BuildCardsFor(List<Segment> segs, HashSet<Guid> newCardIds)
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
                card.RefreshFromSegment(seg);   // 同步为最新查到的 Segment（台词/标签/边界随之刷新）
            }
            card.IsSelectionMode = IsSelectionMode;
            card.IsChecked = SelectedSegmentIds.Contains(seg.Id);
            card.SequenceNumber = NumberFor(seg);
            // #17/#18：重建卡片时恢复处理中状态（占位卡在刷新后仍显示 loading）。
            var isProc = _processingSegments.TryGetValue(seg.Id, out var stage);
            card.IsProcessing = isProc;
            card.ProcessingText = stage ?? string.Empty;
            cards.Add(card);
        }
        return cards;
    }

    private VideoGroupViewModel MakeGroup(GroupSpec spec, List<SegmentCardViewModel> cards)
    {
        if (spec.IsUserGroup)
        {
            // 「自建分镜」聚合组由多个载体视频合并而成，不显示编号（issue #23）。
            return VideoGroupViewModel.CreateUserSegmentGroup(spec.Video, cards);
        }
        var group = new VideoGroupViewModel(spec.Video, cards, _dubbing);
        group.VideoNo = VideoNoFor(spec.Video.Id);
        return group;
    }

    /// <summary>切项目 / 筛选 / 排序变化时调用，重建 Groups。</summary>
    public void RebuildGroups()
    {
        var rebuildSw = System.Diagnostics.Stopwatch.StartNew();
        var filtered = FilteredSegments.ToList();
        var specs = BuildGroupSpecs(filtered);

        // 复用现有 CardVM，按规格建组（自建分镜组置顶）。
        var newGroups = new List<VideoGroupViewModel>(specs.Count);
        var newCardIds = new HashSet<Guid>();
        foreach (var spec in specs)
        {
            newGroups.Add(MakeGroup(spec, BuildCardsFor(spec.Segs, newCardIds)));
        }

        // 清理已不需要的 CardVM
        var toRemove = _cardIndex.Keys.Where(id => !newCardIds.Contains(id)).ToList();
        foreach (var id in toRemove)
        {
            if (_cardIndex.TryGetValue(id, out var c)) c.Dispose();
            _cardIndex.Remove(id);
        }

        // 组 VM 每次都是新建的 → 旧的必须逐个 Dispose 退订 DubbingViewModel.VideoStateChanged，
        // 否则搜索/筛选每跑一次就多留一份僵尸组，配音进度事件会把它们全部唤醒（各自再查一轮库）。
        foreach (var old in Groups)
        {
            old.Dispose();
        }

        // 用增量更新代替整体替换（避免 UI 全量重建）
        ReplaceGroupsInPlace(newGroups);

        // 配音状态批量补齐：组构造时刻意不查库（否则 视频数 × 2 次 SQL），
        // 这里一次查完再灌回去。fire-and-forget —— 建组本身不必等数据库。
        _ = RefreshDubStatusForGroupsAsync(newGroups);

        var totalCards = newGroups.Sum(g => g.Segments.Count);
        Serilog.Log.Information(
            "[GroupDiag] groups={GroupCount} totalCards={Total} sortByQuality={SQ} VM构建={Ms}ms",
            newGroups.Count, totalCards, SortByQuality, rebuildSw.ElapsedMilliseconds);

        // 真正的开销在 WPF 把这些 VM 展开成控件树（单卡约 250~400 个可视元素），
        // 发生在本方法返回之后的布局阶段 —— 用 Loaded 优先级的回调量到「可交互」为止。
        // 这个数字是判断「该不该继续给卡片减重」的唯一依据。
        var uiSw = System.Diagnostics.Stopwatch.StartNew();
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            new Action(() => Serilog.Log.Information(
                "[GroupDiag] 渲染就绪 cards={Total} 界面构建={Ms}ms", totalCards, uiSw.ElapsedMilliseconds)),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>一次查出所有组的配音状态并灌进各组（替代「每组各查两次」）。</summary>
    private async Task RefreshDubStatusForGroupsAsync(List<VideoGroupViewModel> groups)
    {
        if (_dubbing is null) return;
        try
        {
            // 自建分镜组不显示视频级配音条（dubbing 传的就是 null），不用查。
            var ids = groups.Where(g => !g.IsUserSegmentGroup)
                .Select(g => g.VideoId).Distinct().ToList();
            if (ids.Count == 0) return;

            var snapshot = await _dubbing.LoadDubStatusAsync(ids);
            foreach (var g in groups)
            {
                if (snapshot.TryGetValue(g.VideoId, out var s))
                {
                    await g.RefreshDubStatusAsync(s);
                }
            }
            Serilog.Log.Information("[GroupDiag] 配音状态批量预取 videos={Count}（2 次查询）", ids.Count);
        }
        catch (Exception ex)
        {
            // 配音状态只是设置条上的一行文案，查失败不该影响分镜库可用。
            Serilog.Log.Warning(ex, "[GroupDiag] 配音状态批量预取失败，设置条文案可能未刷新");
        }
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

        var specs = BuildGroupSpecs(filtered);

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

        // 逐组异步 Add（自建分镜组置顶），每加完一组 yield 让 WPF 渲染该组
        var sink = new HashSet<Guid>();
        foreach (var spec in specs)
        {
            Groups.Add(MakeGroup(spec, BuildCardsFor(spec.Segs, sink)));

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
        AdjustStartFrame(card.Segment, Math.Sign(step));
        RefreshCardsForVideo(card.Segment.VideoId);
        ShowBoundaryFrame(card, isStart: true);
        BoundaryPreviewRequested?.Invoke(card, true);
    }

    void ISegmentCardHost.AdjustEndTime(SegmentCardViewModel card, double step)
    {
        AdjustEndFrame(card.Segment, Math.Sign(step));
        RefreshCardsForVideo(card.Segment.VideoId);
        ShowBoundaryFrame(card, isStart: false);
        BoundaryPreviewRequested?.Invoke(card, false);
    }

    void ISegmentCardHost.SetStartTime(SegmentCardViewModel card, double newStart)
    {
        SetStartTime(card.Segment, newStart);
        RefreshCardsForVideo(card.Segment.VideoId);
        ShowBoundaryFrame(card, isStart: true);
        BoundaryPreviewRequested?.Invoke(card, true);
    }

    void ISegmentCardHost.SetEndTime(SegmentCardViewModel card, double newEnd)
    {
        SetEndTime(card.Segment, newEnd);
        RefreshCardsForVideo(card.Segment.VideoId);
        ShowBoundaryFrame(card, isStart: false);
        BoundaryPreviewRequested?.Invoke(card, false);
    }

    private void RefreshCardsForVideo(Guid? videoId)
    {
        foreach (var card in _cardIndex.Values.Where(c => c.Segment.VideoId == videoId))
        {
            card.RefreshFromSegment();
        }
    }

    // ============ 剪映式逐帧边界预览 ============

    /// <summary>已解码边界帧缓存（key=视频路径|帧号），来回微调同一帧立即命中、不重复抽帧。</summary>
    private readonly Dictionary<string, System.Windows.Media.ImageSource> _scrubFrameCache = new();

    /// <summary>每张卡的最新抽帧请求序号：连点微调时只让最后一次结果落地，避免旧帧覆盖新帧。</summary>
    private readonly Dictionary<Guid, int> _scrubToken = new();

    private string? _scrubDir;

    /// <summary>
    /// 调 IN/OUT 后把卡片预览切到「当前调到的那一帧」：调 IN 显示新起始帧、调 OUT 显示新末帧
    /// （EndFrame 不含，末画面是 EndFrame-1）。对齐剪映：边界走到哪一帧，预览就显示哪一帧，逐帧跟随。
    /// 抽帧用自带 ffmpeg 输入级 seek（与缩略图同源），带缓存 + 最新优先，连点也不乱。
    /// </summary>
    private async void ShowBoundaryFrame(SegmentCardViewModel card, bool isStart)
    {
        try
        {
            if (_ffmpeg is null) return;
            var seg = card.Segment;
            var path = seg.Video?.LocalPath;
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;
            var fps = seg.EffectiveFps;
            if (fps <= 0) return;

            var frame = isStart
                ? Math.Max(0, seg.StartFrame)
                : Utilities.FrameTime.LastIncludedFrame(seg.StartFrame, seg.EndFrame);
            var timeSec = Utilities.FrameTime.FrameToSeconds(frame, fps);

            // 最新优先：本卡每次请求递增序号，await 回来后若已不是最新就丢弃（连点时只认最后一帧）。
            var token = (_scrubToken.TryGetValue(seg.Id, out var t) ? t : 0) + 1;
            _scrubToken[seg.Id] = token;

            var key = path + "|" + frame.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (_scrubFrameCache.TryGetValue(key, out var cached))
            {
                card.ScrubImage = cached;
                return;
            }

            var outPath = System.IO.Path.Combine(
                ScrubDir(),
                Math.Abs(path.GetHashCode()).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "_" + frame.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".jpg");
            if (!System.IO.File.Exists(outPath))
            {
                await _ffmpeg.GenerateThumbnailAsync(path, outPath, timeSec);
            }
            if (!System.IO.File.Exists(outPath)) return;

            var img = LoadFrameBitmap(outPath);
            if (img is null) return;

            // 软上限：避免长时间微调把内存缓存撑爆。
            if (_scrubFrameCache.Count > 240) _scrubFrameCache.Clear();
            _scrubFrameCache[key] = img;

            // 过期请求不覆盖（用户已经又点了好几下）。
            if (_scrubToken.TryGetValue(seg.Id, out var cur) && cur == token)
            {
                card.ScrubImage = img;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[ScrubDiag] 抽边界帧失败");
        }
    }

    private string ScrubDir()
    {
        if (_scrubDir is null)
        {
            _scrubDir = System.IO.Path.Combine(Utilities.AppPaths.Root, "ScrubCache");
            System.IO.Directory.CreateDirectory(_scrubDir);
        }
        return _scrubDir;
    }

    private static System.Windows.Media.ImageSource? LoadFrameBitmap(string path)
    {
        try
        {
            using var stream = System.IO.File.OpenRead(path);
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 540; // 按显示尺寸解码，省内存（对齐 ThumbnailCache）
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze(); // 冻结后可跨线程安全赋给 UI 绑定
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ============ #18 分镜拆分 ============

    /// <summary>#18：查该分镜被多少个方案引用（&gt;0 时不允许拆分，避免悬空引用）。</summary>
    public async Task<int> CountSchemeReferencesAsync(Guid segmentId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.SchemeSegments.CountAsync(ss => ss.SegmentId == segmentId);
    }

    /// <summary>
    /// #18 分镜拆分：把一个分镜按 <paramref name="cutFrame"/> 切成前后两段全新分镜。
    /// A 复用原记录=[start,cut)，B 新建=[cut,end) 继承标签；原分镜配音/逐句字幕/画面替换全部清空（不可恢复）；
    /// A、B 各自重抽首帧 + 各自阿里 paraformer 重识别台词。已被方案引用的分镜禁止拆分（双保险）。
    /// </summary>
    public async Task<(bool Ok, string? Error, Guid AId, Guid BId)> SplitSegmentAsync(Guid segmentId, int cutFrame, CancellationToken ct = default)
    {
        string? videoPath = null;
        var aId = segmentId;
        var bId = Guid.Empty;
        double fps = 30.0;

        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var a = await db.Segments.Include(s => s.SchemeSegments).Include(s => s.Video)
                .FirstOrDefaultAsync(s => s.Id == segmentId, ct);
            if (a is null) return (false, "分镜不存在", Guid.Empty, Guid.Empty);
            if (a.SchemeSegments.Count > 0)
                return (false, $"该分镜已被 {a.SchemeSegments.Count} 个方案使用，请先在方案里移除对它的使用再拆分", Guid.Empty, Guid.Empty);

            var video = await db.Videos.FirstOrDefaultAsync(v => v.Id == a.VideoId, ct);
            videoPath = video?.LocalPath;
            fps = a.EffectiveFps > 0 ? a.EffectiveFps : (video?.Fps > 0 ? video.Fps : 30.0);
            var startFrame = a.StartFrame;
            var endFrame = a.EndFrame;
            if (cutFrame <= startFrame || cutFrame >= endFrame)
                return (false, "拆分点无效（需落在分镜内部）", Guid.Empty, Guid.Empty);

            // B 新建，继承 A 的语义标签（B 天然无衍生物）。
            var b = new Segment
            {
                VideoId = a.VideoId,
                SemanticTypesJson = a.SemanticTypesJson,
                PositionType = a.PositionType,
                KeywordsJson = a.KeywordsJson,
                QualityScore = a.QualityScore,
                QualityReasoning = a.QualityReasoning,
            };
            b.SetBoundsFrames(cutFrame, endFrame, fps);
            bId = b.Id;

            // A=[start,cut)，清空全部衍生物（A 内容变了，原衍生物失效、不可恢复）。
            a.SetBoundsFrames(startFrame, cutFrame, fps);
            var dubs = await db.SegmentDubs.Where(d => d.SegmentId == a.Id).ToListAsync(ct);
            if (dubs.Count > 0) db.SegmentDubs.RemoveRange(dubs);                 // 含逐句字幕 CaptionLines
            var shots = await db.PhysicalShots.Where(p => p.SegmentId == a.Id).ToListAsync(ct);
            if (shots.Count > 0) db.PhysicalShots.RemoveRange(shots);            // 级联删 ShotVariants
            a.InvalidateReplacedPicture();                                       // 清替换画面
            a.ClonedVoiceId = null;                                             // 清该分镜单独克隆的音色
            a.IsVoiceLocked = false;
            a.HasHardSubtitle = false;
            a.MaskRectJson = null;

            // 从原视频 ASR 字级时间戳按新边界提取 A/B 台词（本地瞬时、无需网络/重识别，无 key 依赖）。
            // 比阿里 paraformer 重识别更优：原视频已有字级时间戳，直接按新窗口取，准确且不产生网络请求。
            if (a.Video is not null)
            {
                var aText = ExtractTextFromAsrWords(a.Video, a.StartTime, a.EndTime);
                if (aText.Length > 0) a.Text = aText;
                b.Text = ExtractTextFromAsrWords(a.Video, b.StartTime, b.EndTime);
            }

            db.Segments.Add(b);
            await db.SaveChangesAsync(ct);
        }

        // A、B 各自重抽首帧缩略图（按新边界）。
        if (!string.IsNullOrEmpty(videoPath))
        {
            await RethumbSplitAsync(aId, videoPath!, fps, ct);
            await RethumbSplitAsync(bId, videoPath!, fps, ct);
        }

        _logger.LogInformation("[SplitDiag] 结构拆分完成 A={A} B={B} cut={Cut}（台词重识别由调用方后台编排）", aId, bId, cutFrame);
        return (true, null, aId, bId);
    }

    /// <summary>#18：从原视频 ASR 字级时间戳按时间窗提取台词（中心点落在 [start,end) 内的词拼接）。本地、无网络。</summary>
    private static string ExtractTextFromAsrWords(Models.Video video, double startSec, double endSec)
    {
        var matched = video.AsrWords
            .Where(w => (w.Start + w.End) / 2 >= startSec && (w.Start + w.End) / 2 < endSec)
            .Select(w => w.Word);
        return string.Concat(matched).Trim();
    }

    /// <summary>拆分后按新 StartFrame 重抽首帧缩略图并写回 ThumbnailPath（文件名带帧号保证路径唯一→触发刷新）。</summary>
    private async Task RethumbSplitAsync(Guid segId, string videoPath, double fps, CancellationToken ct)
    {
        try
        {
            if (_ffmpeg is null) return;
            int startFrame;
            await using (var db = await _dbFactory.CreateDbContextAsync(ct))
            {
                var s = await db.Segments.FirstOrDefaultAsync(x => x.Id == segId, ct);
                if (s is null) return;
                startFrame = s.StartFrame;
            }
            var thumbDir = System.IO.Path.Combine(Utilities.AppPaths.Root, "Thumbnails");
            System.IO.Directory.CreateDirectory(thumbDir);
            var newPath = System.IO.Path.Combine(thumbDir, $"seg_{segId}_f{startFrame}.jpg");
            var timeSec = Utilities.FrameTime.FrameToSeconds(startFrame, fps) + 0.05;
            await _ffmpeg.GenerateThumbnailAsync(videoPath, newPath, timeSec, ct);
            if (!System.IO.File.Exists(newPath)) return;
            await using (var db = await _dbFactory.CreateDbContextAsync(ct))
            {
                var s = await db.Segments.FirstOrDefaultAsync(x => x.Id == segId, ct);
                if (s is not null) { s.ThumbnailPath = newPath; await db.SaveChangesAsync(ct); }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[SplitDiag] 缩略图重抽失败 seg={Id}", segId); }
    }

    void ISegmentCardHost.RequestDelete(SegmentCardViewModel card)
    {
        var confirmed = Views.Shared.MixCutDialog.Confirm(
            Application.Current?.MainWindow,
            $"删除分镜 {card.SegmentIndexLabel}？",
            "删除后可以按 Ctrl+Z 撤销。",
            confirmText: "删除", cancelText: "取消", destructive: true, icon: "🗑");
        if (!confirmed) return;

        // P0-10 修：右键单删此前直接删、不入撤销栈，导致删完按 Ctrl+Z 提示「没有可撤销的操作」。
        // 现与批量删除路径对齐：删前快照 → 删除成功才压栈 → Ctrl+Z 调 RestoreSegments 恢复。
        var snapshot = Infrastructure.UndoStack.UndoClone.CloneSegment(card.Segment);
        if (!DeleteSegment(card.Segment))
        {
            // 不谎报成功、不压栈，给人话提示 + 重试入口（对齐批量删除的失败处理）。
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
        Views.Components.ToastService.Show("已删除分镜", Views.Components.ToastStyle.Warning,
            "撤销", () => Infrastructure.UndoStack.UndoManager.Shared.Undo());
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
        // v0.5.0：单击分镜 → 右侧配音变体检视器加载该分镜。
        _ = DubInspector?.LoadAsync(card.Segment);
    }

    // ============ P3：字幕处理（保留原声 / 字幕处理 / 遮挡框） ============

    /// <summary>
    /// 配音服务的非空访问器。<c>_dubbing</c> 声明为可空，但**生产环境恒非空** ——
    /// DI 对多构造选「参数最多且可满足者」，`FFmpegRunner`+`DubbingViewModel` 均已注册，
    /// 故实际走 4 参构造（注入非空 dubbing）；2 参构造仅供单元测试，且测试不触达下面这些配音回调。
    /// 用此属性集中表达该不变量，既消除 6 处 CS8602 噪音、又保留「真为 null 就 NRE 快速失败」（是配置错误，应立即暴露而非静默）。
    /// </summary>
    private DubbingViewModel Dubbing => _dubbing!;

    async Task ISegmentCardHost.ToggleVoiceLockAsync(SegmentCardViewModel card, bool locked)
    {
        await Dubbing.SetVoiceLockedAsync(card.Segment.Id, locked);
        // 关键修复：_dubbing 用独立短上下文改的是 DB 里另一个 Segment 实例，card.Segment 是内存里
        // LoadSegments 时加载的对象，不会被那次写入更新。必须手动同步内存对象，否则 RefreshFromSegment
        // 读到旧值 → 复选框/字幕处理面板不刷新 → 用户感知「点了没反应」。
        card.Segment.IsVoiceLocked = locked;
        // 刷新该卡 + 检视器（锁定 → 检视器切到 Locked 态）
        // DubsChanged 已由 SetVoiceLockedAsync 内部触发（刷新设置条变体计数）
        card.RefreshFromSegment();
        _ = DubInspector?.LoadAsync(card.Segment);
    }

    async Task ISegmentCardHost.SetSubtitleTreatmentAsync(SegmentCardViewModel card, SubtitleTreatment treatment)
    {
        await Dubbing.SetSubtitleTreatmentAsync(card.Segment.Id, treatment);
        // 同上：同步内存 Segment，否则三胶囊高亮不切换（DataTrigger 绑 SubtitleTreatment 读到旧值）。
        card.Segment.SubtitleTreatment = treatment;
        // DubsChanged 已由 SetSubtitleTreatmentAsync 内部触发
        card.RefreshFromSegment();
    }

    void ISegmentCardHost.CommitMaskRect(SegmentCardViewModel card)
    {
        _ = Dubbing.SetMaskRectAsync(card.Segment.Id, card.MaskRect);
    }

    void ISegmentCardHost.CommitFontRatio(SegmentCardViewModel card)
    {
        // 卡片 VM 已把新值写进内存 Segment（拖动实时预览）；这里落库 + 记为新分镜默认值。
        _ = Dubbing.SetFontRatioAsync(card.Segment.Id, card.Segment.SubtitleFontRatio);
    }

    async Task ISegmentCardHost.ApplyMaskToAllAsync(SegmentCardViewModel card)
    {
        var n = await Dubbing.ApplyMaskToAllAsync(card.Segment.Id);

        // _dubbing 走的是另一个短上下文，只改了 DB —— 分镜库 VM 的 _segments（卡片正绑定的对象）还是旧值。
        // 必须把源分镜的「字幕处理 + 遮挡框」同步到同一视频其余分镜的内存对象，再刷新对应卡片，UI 才联动。
        // 不能走 LoadSegments()：它换成新实例后 RebuildGroups 复用的旧卡片仍指向旧 Segment 对象 → RefreshFromSegment 读到旧值（正是本 bug 根因）。
        var src = card.Segment;
        var videoId = src.VideoId;
        foreach (var seg in _segments.Where(s => s.VideoId == videoId && s.Id != src.Id))
        {
            seg.HasHardSubtitle = src.HasHardSubtitle;
            seg.MaskStyleRaw = src.MaskStyleRaw;
            seg.MaskRectJson = src.MaskRectJson;
        }
        // 刷新受影响卡片：字幕处理胶囊高亮 / 遮挡框显隐 / 所见即所得预览都要跟着变（含源卡自身）。
        foreach (var c in _cardIndex.Values.Where(c => c.Segment.VideoId == videoId))
        {
            c.RefreshFromSegment();
        }
        Serilog.Log.Information("[GroupDiag] 遮挡应用到所有 src={Seg} affected={N} 刷新卡片={Cards}",
            src.Id, n, _cardIndex.Values.Count(c => c.Segment.VideoId == videoId));

        Views.Components.ToastService.Show(
            n > 0 ? $"已将字幕处理应用到本视频其余 {n} 个分镜" : "本视频没有其它分镜",
            Views.Components.ToastStyle.Success);
    }

    void ISegmentCardHost.RefreshCardFromHost(SegmentCardViewModel card) => card.RefreshFromSegment();

    // ---- #12 分镜头 AI 画面替换 ----

    /// <summary>请求打开「分镜头替换」工作区（View 订阅后开窗，避免 VM 依赖 View）。</summary>
    public event Action<Segment>? ShotEditRequested;

    Task ISegmentCardHost.RequestReplaceShotAsync(SegmentCardViewModel card)
    {
        ShotEditRequested?.Invoke(card.Segment);
        return Task.CompletedTask;
    }

    /// <summary>#18：请求打开分镜拆分窗口（View 订阅后开窗，避免 VM 依赖 View）。</summary>
    public event Action<Segment>? SplitRequested;

    void ISegmentCardHost.RequestSplit(SegmentCardViewModel card) => SplitRequested?.Invoke(card.Segment);

    async Task ISegmentCardHost.ToggleReplacedPictureAsync(SegmentCardViewModel card)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var seg = await db.Segments.FirstOrDefaultAsync(s => s.Id == card.Segment.Id);
        if (seg is null || string.IsNullOrEmpty(seg.ReplacedPictureVideoPath)) return;
        seg.PictureShowsReplaced = !seg.PictureShowsReplaced;
        await db.SaveChangesAsync();
        card.Segment.PictureShowsReplaced = seg.PictureShowsReplaced;   // 同步内存对象
        card.RefreshFromSegment();
        Views.Components.ToastService.Show(
            seg.PictureShowsReplaced ? "已切到替换画面" : "已切回原画面", Views.Components.ToastStyle.Success);
    }

    async Task ISegmentCardHost.DeleteReplacedPictureAsync(SegmentCardViewModel card)
    {
        // 二次确认：删的是用户花了几分钟 + 真金白银用 AI 生成出来的画面，而且视频文件会被物理删除、
        // 撤销不回来 —— 只能重新计费生成。同一个 VM 里删普通分镜都有确认 + Ctrl+Z 快照，
        // 这条代价更大的路径原本却是一点即删。
        var confirmed = Views.Shared.MixCutDialog.Confirm(
            System.Windows.Application.Current?.MainWindow,
            "删除这个分镜的 AI 替换画面？",
            "生成好的替换视频会被永久删除，画面还原为原始素材。\n\n"
            + "这一步不能撤销，之后想再要就得重新用 AI 生成（重新计费）。",
            confirmText: "删除替换画面", cancelText: "保留",
            destructive: true, icon: "🗑");
        if (!confirmed) return;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var seg = await db.Segments.FirstOrDefaultAsync(s => s.Id == card.Segment.Id);
        if (seg is null) return;
        var vid = seg.ReplacedPictureVideoPath;
        var thumb = seg.ReplacedPictureThumbnailPath;
        seg.InvalidateReplacedPicture();
        await db.SaveChangesAsync();
        TryDeleteFile(vid);
        TryDeleteFile(thumb);
        card.Segment.InvalidateReplacedPicture();
        card.RefreshFromSegment();
        Views.Components.ToastService.Show("已删除替换画面，还原原画面", Views.Components.ToastStyle.Success);
    }

    /// <summary>工作区合成完毕关闭后：把该分镜「替换画面」四字段从 DB 同步回内存卡片并刷新。</summary>
    public async Task ReloadReplacedPictureAsync(Segment segment)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var seg = await db.Segments.AsNoTracking().FirstOrDefaultAsync(s => s.Id == segment.Id);
        if (seg is null) return;
        segment.ReplacedPictureVideoPath = seg.ReplacedPictureVideoPath;
        segment.ReplacedPictureThumbnailPath = seg.ReplacedPictureThumbnailPath;
        segment.ReplacedPictureFrameCount = seg.ReplacedPictureFrameCount;
        segment.PictureShowsReplaced = seg.PictureShowsReplaced;
        var card = _cardIndex.Values.FirstOrDefault(c => c.Segment.Id == segment.Id);
        card?.RefreshFromSegment();
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { /* 忽略 */ }
    }

    async Task<string> ISegmentCardHost.ReRecognizeSegmentAsync(SegmentCardViewModel card, CancellationToken ct)
    {
        try
        {
            var newText = await Dubbing.ReRecognizeSegmentAsync(card.Segment.Id, ct);
            // 同步内存 Segment.Text：ReRecognizeSegmentAsync 只改了另一个短上下文里的实体，
            // 不同步这里的话，卡片随后 RefreshFromSegment 会用旧 _segment.Text 把新识别结果覆盖回去，
            // 看着像「点了没反应」（对齐 [[dub-p3-inmemory-sync-bug]] 教训）。
            card.Segment.Text = newText;
            Views.Components.ToastService.Show(
                $"重识别完成（{newText.Length} 字）",
                Views.Components.ToastStyle.Success);
            return newText;
        }
        catch (Exception ex)
        {
            var msg = ex is MixCut.Services.Dubbing.DubException d ? d.Message : "重识别失败，请稍后重试";
            Views.Components.ToastService.Show(msg, Views.Components.ToastStyle.Error);
            return string.Empty;
        }
    }

    async Task<string> ISegmentCardHost.SaveSegmentTextAsync(SegmentCardViewModel card, string newText, CancellationToken ct)
    {
        var saved = await Dubbing.UpdateSegmentTextAsync(card.Segment.Id, newText, ct);
        // 同步内存 Segment.Text（同 ReRecognize：短上下文改的是另一个实例）。
        card.Segment.Text = saved;
        Views.Components.ToastService.Show("台词已保存", Views.Components.ToastStyle.Success);
        return saved;
    }

}
