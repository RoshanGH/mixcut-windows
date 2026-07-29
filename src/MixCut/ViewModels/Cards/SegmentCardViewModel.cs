using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MixCut.Models;

namespace MixCut.ViewModels.Cards;

/// <summary>
/// 单张分镜卡片的 ViewModel。
///
/// 设计原则（来自 spec §3.2 关键原则 6）：
/// - 所有视觉状态（IsSelected / IsHovering / IsBoundaryRowVisible）都存在这里，**不存在 ItemContainer 自身**
///   （虚拟化复用 container 会污染状态，所以状态必须存 VM）。
/// - 时间字段直接驱动 UI binding，setter 触发 PropertyChanged + 异步 debounce 持久化。
/// - 持久化串行（per-VM SemaphoreSlim）+ debounce 300ms，防止连点 ±0.1s 时持久化乱序（spec §6.2）。
///
/// 这个 VM 不引用任何 UI 类型，可单测。
/// </summary>
public sealed partial class SegmentCardViewModel : ObservableObject, IDisposable
{
    private Segment _segment;   // 非 readonly：RebuildGroups 复用卡片时会同步为最新查到的 Segment（见 RefreshFromSegment）
    private readonly ISegmentCardHost _host;

    public SegmentCardViewModel(Segment segment, ISegmentCardHost host)
    {
        _segment = segment;
        _host = host;

        // 初始值从 Segment 读出
        _startTime = segment.StartTime;
        _endTime = segment.EndTime;
        _qualityScore = segment.QualityScore;
        _transcriptText = segment.Text ?? string.Empty;
        _positionType = segment.PositionType;
        _semanticTypes = new ObservableCollection<SemanticType>(segment.SemanticTypes);
        _thumbnailPath = segment.EffectivePicture.ThumbnailPath ?? segment.ThumbnailPath;
        _segmentIndexLabel = segment.SegmentIndex ?? string.Empty;

        // 视频文件可用性 cache 一次（避免每次 UI 刷新都走 File.Exists）
        var localPath = segment.Video?.LocalPath;
        _isVideoFileAvailable = !string.IsNullOrEmpty(localPath) && File.Exists(localPath);
        _videoLocalPath = localPath;

        // 视频宽高比
        var video = segment.Video;
        _videoAspectRatio = video is { Width: > 0, Height: > 0 }
            ? Math.Max((double)video.Width / video.Height, 4.0 / 5.0)
            : 9.0 / 16.0;

        // 订阅缩略图加载完成事件（不同 CardVM 共享同一图时一次加载多卡片刷新）
        Infrastructure.ThumbnailCache.Shared.ImageLoaded += OnThumbnailLoaded;
    }

    /// <summary>底层 Segment（命令执行 / 持久化使用）。View 不应直接绑定到它的字段。</summary>
    public Segment Segment => _segment;

    public Guid Id => _segment.Id;

    public Guid? VideoId => _segment.VideoId;

    /// <summary>
    /// 缩略图。优先 segment.ThumbnailPath；缺失时 fallback 视频 thumbnail。
    /// cache 命中立即返回；未命中触发 LoadAsync 后台加载 → ImageLoaded 事件 → INPC 刷新。
    /// </summary>
    public System.Windows.Media.ImageSource? ThumbnailImage
    {
        get
        {
            var path = !string.IsNullOrEmpty(ThumbnailPath)
                ? ThumbnailPath
                : _segment.Video?.ThumbnailPath;
            if (string.IsNullOrEmpty(path)) return null;
            var cached = Infrastructure.ThumbnailCache.Shared.PeekImage(path);
            if (cached is not null) return cached;
            _ = Infrastructure.ThumbnailCache.Shared.LoadAsync(path);
            return null;
        }
    }

    /// <summary>
    /// 卡片实际显示的预览图：剪映式逐帧微调时优先显示 <see cref="ScrubImage"/>（当前调到的边界帧），
    /// 否则回退到分镜静态缩略图。
    /// </summary>
    public System.Windows.Media.ImageSource? PreviewImage => ScrubImage ?? ThumbnailImage;

    /// <summary>
    /// 逐帧微调时由 host 抽帧设入的「当前边界帧」画面（调 IN 显示新起始帧、调 OUT 显示新末帧）。
    /// null 时卡片显示静态缩略图。对齐剪映：拖/点边界时预览实时跟到那一帧。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewImage))]
    private System.Windows.Media.ImageSource? _scrubImage;

    /// <summary>
    /// #17/#18：卡片是否处理中（自建分镜上传的 ASR/打标、拆分后的重识别），显示 loading 占位遮罩。
    /// 由 host（SegmentLibraryViewModel.SetProcessing）驱动，处理完清除。
    /// </summary>
    [ObservableProperty] private bool _isProcessing;

    /// <summary>处理中的阶段文案（如「识别中…」「打标中…」「重新识别…」）。</summary>
    [ObservableProperty] private string _processingText = string.Empty;

    /// <summary>ThumbnailCache 通知某张图加载完成时调用，若路径匹配则刷新 binding。</summary>
    private void OnThumbnailLoaded(string loadedPath)
    {
        if (loadedPath == ThumbnailPath)
        {
            // 切到 UI 线程触发 PropertyChanged（event 可能从 ThreadPool 线程触发）
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            void Raise()
            {
                OnPropertyChanged(nameof(ThumbnailImage));
                OnPropertyChanged(nameof(PreviewImage));
            }
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                Raise();
            }
            else
            {
                dispatcher.BeginInvoke(new Action(Raise));
            }
        }
    }

    /// <summary>所属视频名（去掉扩展名前 N 字符），用于卡片左下角小字显示。</summary>
    public string VideoNameLabel
    {
        get
        {
            var name = _segment.Video?.Name ?? "未知视频";
            // 去扩展名 + 截断长名
            var dot = name.LastIndexOf('.');
            if (dot > 0) name = name.Substring(0, dot);
            return name.Length > 18 ? name.Substring(0, 18) + "…" : name;
        }
    }

    // ============ ObservableProperty 字段 ============

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationDisplay))]
    [NotifyPropertyChangedFor(nameof(Duration))]
    [NotifyPropertyChangedFor(nameof(StartTimecode))]
    [NotifyPropertyChangedFor(nameof(StartFrameText))]
    [NotifyPropertyChangedFor(nameof(TimeRangeDisplay))]
    [NotifyPropertyChangedFor(nameof(LastFrameTimecode))]
    private double _startTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationDisplay))]
    [NotifyPropertyChangedFor(nameof(Duration))]
    [NotifyPropertyChangedFor(nameof(EndTimecode))]
    [NotifyPropertyChangedFor(nameof(EndFrameText))]
    [NotifyPropertyChangedFor(nameof(TimeRangeDisplay))]
    [NotifyPropertyChangedFor(nameof(LastFrameTimecode))]
    private double _endTime;

    [ObservableProperty]
    private double _qualityScore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TranscriptCharCount))]
    [NotifyPropertyChangedFor(nameof(HasTranscript))]
    [NotifyPropertyChangedFor(nameof(TranscriptReadonlyVisible))]
    [NotifyPropertyChangedFor(nameof(TranscriptEmptyVisible))]
    private string _transcriptText;

    [ObservableProperty]
    private PositionType _positionType;

    [ObservableProperty]
    private ObservableCollection<SemanticType> _semanticTypes;

    [ObservableProperty]
    private string? _thumbnailPath;

    [ObservableProperty]
    private string _segmentIndexLabel;

    private readonly bool _isVideoFileAvailable;
    private readonly string? _videoLocalPath;
    private readonly double _videoAspectRatio;

    public bool IsVideoFileAvailable => _isVideoFileAvailable;
    public string? VideoLocalPath => _videoLocalPath;
    public double VideoAspectRatio => _videoAspectRatio;

    // ============ 视觉状态（纯 UI 状态，不持久化） ============

    /// <summary>当前是否选中（高亮显示）。点击卡片切换。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBoundaryRowVisible))]
    [NotifyPropertyChangedFor(nameof(TranscriptActionsVisible))]
    private bool _isSelected;

    /// <summary>鼠标是否悬停在卡片上。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBoundaryRowVisible))]
    [NotifyPropertyChangedFor(nameof(TranscriptActionsVisible))]
    private bool _isHovering;

    /// <summary>
    /// 这张卡当前是否正在内联播放（播放器已挂上、画面在动）。
    ///
    /// 为什么必须存在 VM 而不是直接改控件的 Visibility：卡片容器一旦启用虚拟化复用，
    /// 「▶ 按钮和时长徽章被隐藏」这种写在可视元素上的局部值会跟着容器漂到另一张卡上，
    /// 那张卡就永久没有播放按钮和时长 —— 而且只在滚动过特定顺序后才复现，极难查。
    /// 状态存 VM、可见性走 binding，容器复用时随 DataContext 自动切换，天然免疫。
    /// （这也是本类顶部「所有视觉状态都存这里」原则的一部分。）
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayOverlayVisible))]
    [NotifyPropertyChangedFor(nameof(DurationBadgeVisible))]
    private bool _isPlayingInline;

    /// <summary>▶ 播放层是否可见：非多选态、且当前没在播。</summary>
    public bool PlayOverlayVisible => !IsSelectionMode && !IsPlayingInline;

    /// <summary>时长徽章是否可见：播放时隐藏，避免挡住播放控制栏右侧的总时长。</summary>
    public bool DurationBadgeVisible => !IsPlayingInline;

    /// <summary>是否处于台词内联编辑态（显示 TextBox + 保存/取消）。对齐 mac isEditingText。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TranscriptActionsVisible))]
    [NotifyPropertyChangedFor(nameof(TranscriptReadonlyVisible))]
    [NotifyPropertyChangedFor(nameof(TranscriptEmptyVisible))]
    private bool _isEditingText;

    /// <summary>编辑态下的草稿文本（TextBox 双向绑定）；取消则丢弃，保存才落库。</summary>
    [ObservableProperty]
    private string _draftText = string.Empty;

    /// <summary>多选模式下是否被勾选。</summary>
    [ObservableProperty]
    private bool _isChecked;

    /// <summary>多选模式总开关（父 VM 推下来）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayOverlayVisible))]
    private bool _isSelectionMode;

    /// <summary>序号徽章（视频内编号 #1/#2...）。父 VM 推下来。</summary>
    [ObservableProperty]
    private int _sequenceNumber;

    /// <summary>对齐 Mac BoundaryAdjustRow：仅 hover 或 selected 时显示，避免 50+ TextField 实例化。</summary>
    public bool IsBoundaryRowVisible => IsHovering || IsSelected;

    // ============ 计算属性 ============

    public double Duration => Math.Max(0, EndTime - StartTime);

    /// <summary>"10.8s" 格式。</summary>
    public string DurationDisplay =>
        Duration.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + "s";

    /// <summary>起点剪映式时间码（时:分:秒:帧，按 fps 进位）。issue #7。</summary>
    public string StartTimecode =>
        _segment.StartTimecode;

    /// <summary>终点剪映式时间码。</summary>
    public string EndTimecode =>
        _segment.EndTimecode;

    /// <summary>"起 - 止" 时间码区间显示。</summary>
    public string TimeRangeDisplay => $"{StartTimecode} - {EndTimecode}";

    public string LastFrameTimecode => _segment.LastFrameTimecode;

    public string FrameRateDisplay =>
        _segment.EffectiveFps > 0
            ? $"{_segment.EffectiveFps:0.###} fps"
            : "帧率未知";

    public int TranscriptCharCount => TranscriptText?.Length ?? 0;

    public bool HasTranscript => !string.IsNullOrWhiteSpace(TranscriptText);

    /// <summary>台词区 ✏/↻ 操作按钮是否可见：hover 或选中、且非编辑态（对齐 mac 卡片 header 交互）。
    /// 空台词也显示，用户可直接 ✏ 输入或 ↻ 重识别 —— 这是 Windows 之前缺失、导致「看不到/不能改」的根因。</summary>
    public bool TranscriptActionsVisible => (IsHovering || IsSelected) && !IsEditingText;

    /// <summary>只读台词文本是否可见：有台词且非编辑态。</summary>
    public bool TranscriptReadonlyVisible => HasTranscript && !IsEditingText;

    /// <summary>「暂无台词」占位是否可见：无台词且非编辑态。</summary>
    public bool TranscriptEmptyVisible => !HasTranscript && !IsEditingText;

    // ============ P3 字幕处理绑定（读写底层 Segment 字段，持久化经 host） ============

    public bool IsVoiceLocked => _segment.IsVoiceLocked;
    public SubtitleTreatment SubtitleTreatment => _segment.SubtitleTreatment;

    /// <summary>是否显示遮挡框（模糊/纯色需要定位区域，直接烧录不需要）。</summary>
    public bool NeedsMaskEditor => _segment.SubtitleTreatment != SubtitleTreatment.Direct;

    /// <summary>是否显示字幕预览层（含字号预览）：只要不保留原声就显示（三种字幕处理都要预览字号）。
    /// 对齐 mac：预览随 !isVoiceLocked 显示，遮挡框另由 <see cref="NeedsMaskEditor"/> 控制。</summary>
    public bool SubtitlePreviewVisible => !_segment.IsVoiceLocked;

    /// <summary>遮挡框归一化坐标（拖拽期间写内存，松手时经 host 落库）。</summary>
    public SubtitleMaskRect MaskRect
    {
        get => _segment.MaskRect;
        set { _segment.MaskRect = value; }
    }

    /// <summary>逐分镜字号比例（issue #23 Agent 设置）；≤0 = 跟随全局，字号预览层据此渲染。</summary>
    public double SegmentFontRatio => _segment.SubtitleFontRatio;

    /// <summary>
    /// 卡片字号滑块绑定值（逐分镜）：未单独调过时显示全局默认，一经拖动即写入本分镜自己的
    /// <see cref="Segment.SubtitleFontRatio"/>（拖动实时预览，松手 400ms 后落库一次，防拖动期间刷库）。
    /// </summary>
    public double FontRatio
    {
        get => _segment.SubtitleFontRatio > 0
            ? SubtitleFontSize.Clamp(_segment.SubtitleFontRatio)
            : SubtitleFontState.Shared.Ratio;
        set
        {
            var clamped = SubtitleFontSize.Clamp(value);
            if (Math.Abs(FontRatio - clamped) < 0.0001 && _segment.SubtitleFontRatio > 0) return;
            _segment.SubtitleFontRatio = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FontPercentText));
            OnPropertyChanged(nameof(SegmentFontRatio));   // 画面里的示例字幕实时跟随
            ScheduleFontRatioCommit();
        }
    }

    /// <summary>「6.9%」字号百分比文案。</summary>
    public string FontPercentText => $"{FontRatio * 100:0.#}%";

    /// <summary>字号落库 debounce：拖动期间只预览，停手 400ms 落一次库。</summary>
    private CancellationTokenSource? _fontCommitCts;

    private void ScheduleFontRatioCommit()
    {
        _fontCommitCts?.Cancel();
        _fontCommitCts?.Dispose();
        var cts = new CancellationTokenSource();
        _fontCommitCts = cts;
        _ = CommitFontRatioAfterDelayAsync(cts.Token);
    }

    private async Task CommitFontRatioAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(400, token);
            if (!token.IsCancellationRequested)
            {
                _host.CommitFontRatio(this);
            }
        }
        catch (OperationCanceledException)
        {
            // 拖动中被下一次调整取代，静默
        }
    }

    // ============ 命令 ============

    [RelayCommand]
    private void AdjustStart(string? stepStr)
    {
        // 通过 host 走 V1 的 AdjustStartTime 路径（含 ReExtractText 重新提取台词 + Save）
        _host.AdjustStartTime(this, FrameStep(stepStr));
    }

    [RelayCommand]
    private void AdjustEnd(string? stepStr)
    {
        _host.AdjustEndTime(this, FrameStep(stepStr));
    }

    /// <summary>
    /// issue #7 逐帧微调：取 stepStr 的<b>方向</b>，步长固定为 1 帧（1/fps）。
    /// fps 未知（旧数据未回填）时退化为原步长（默认 ±0.1s）。
    /// </summary>
    private double FrameStep(string? stepStr)
    {
        var raw = ParseStep(stepStr, 0.1);
        var fps = _segment.EffectiveFps;
        if (fps <= 0)
        {
            return raw;
        }
        return (raw < 0 ? -1.0 : 1.0) / fps;
    }

    [RelayCommand]
    private void CommitStart(string? text) => CommitBoundary(text, isStart: true);

    [RelayCommand]
    private void CommitEnd(string? text) => CommitBoundary(text, isStart: false);

    /// <summary>
    /// 提交手输的边界值。**输入的是帧号**（与 ± 按钮的步长单位一致）。
    ///
    /// 为什么按帧而不是秒：这两个 ± 按钮走的是 <see cref="FrameStep"/>，一次正好 1 帧；
    /// 而输入框以前显示/接受的是秒（0.0、13.1），点一下 ± 数字只动 0.04，用户根本对不上
    /// 「我调的是帧」这件事。issue #7 已经把整个分镜模型重构成帧精确，UI 这里也应当以帧为准。
    /// 兼容：fps 未知的旧数据无法做帧↔秒换算，此时退回按秒解释输入值。
    /// </summary>
    private void CommitBoundary(string? text, bool isStart)
    {
        var fps = _segment.EffectiveFps;
        double seconds;
        if (fps > 0)
        {
            if (!int.TryParse(text?.Trim(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var frame))
            {
                return;
            }
            seconds = Math.Max(0, frame) / fps;
        }
        else if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                     System.Globalization.CultureInfo.InvariantCulture, out seconds))
        {
            return;
        }

        if (isStart) _host.SetStartTime(this, seconds);
        else _host.SetEndTime(this, seconds);
    }

    /// <summary>起点帧号（输入框显示值）。fps 未知的旧数据回退成秒，见 <see cref="CommitBoundary"/>。</summary>
    public string StartFrameText => FrameTextFor(StartTime);

    /// <summary>终点帧号（输入框显示值）。</summary>
    public string EndFrameText => FrameTextFor(EndTime);

    private string FrameTextFor(double seconds)
    {
        var fps = _segment.EffectiveFps;
        return fps > 0
            ? ((int)Math.Round(seconds * fps)).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : seconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static double ParseStep(string? s, double fallback)
    {
        return double.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    [RelayCommand]
    private void CopyTranscript()
    {
        try
        {
            Clipboard.SetText(TranscriptText ?? string.Empty);
            Views.Components.ToastService.Show("台词已复制", Views.Components.ToastStyle.Success);
        }
        catch { /* clipboard busy, ignore */ }
    }

    [RelayCommand]
    private void ShowInExplorer()
    {
        if (string.IsNullOrEmpty(VideoLocalPath) || !IsVideoFileAvailable) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{VideoLocalPath}\"",
                UseShellExecute = true,
            });
        }
        catch { /* path with unusual chars */ }
    }

    [RelayCommand]
    private void Delete()
    {
        _host.RequestDelete(this);
    }

    // ---- #12 分镜头 AI 画面替换 ----

    /// <summary>本分镜是否已有 AI 替换画面（无论当前显示原/替换）。</summary>
    public bool HasReplacedPicture => !string.IsNullOrEmpty(_segment.ReplacedPictureVideoPath);

    /// <summary>当前是否显示替换画面。</summary>
    public bool PictureShowsReplaced => _segment.PictureShowsReplaced;

    /// <summary>切换画面胶囊的文案：当前显示替换画面 → "替换画面"，否则 "原画面"。对齐 mac。</summary>
    public string PictureToggleLabel => _segment.PictureShowsReplaced ? "替换画面" : "原画面";

    [RelayCommand]
    private async Task ReplaceShot() => await _host.RequestReplaceShotAsync(this);

    /// <summary>#18：右键「拆分」→ 请求 host 打开拆分窗口。</summary>
    [RelayCommand]
    private void SplitSegment() => _host.RequestSplit(this);

    [RelayCommand]
    private async Task ToggleReplacedPicture()
    {
        await _host.ToggleReplacedPictureAsync(this);
        OnPropertyChanged(nameof(PictureShowsReplaced));
        OnPropertyChanged(nameof(PictureToggleLabel));
    }

    [RelayCommand]
    private async Task DeleteReplacedPicture()
    {
        await _host.DeleteReplacedPictureAsync(this);
        OnPropertyChanged(nameof(HasReplacedPicture));
        OnPropertyChanged(nameof(PictureShowsReplaced));
    }

    [RelayCommand]
    private void ToggleSemantic(SemanticType type)
    {
        _host.ToggleSemanticType(this, type);
        // _host 改完会通知 RefreshSemanticTypes
    }

    [RelayCommand]
    private void UpdatePosition(PositionType type)
    {
        _host.UpdatePositionType(this, type);
    }

    [RelayCommand]
    private void ToggleChecked()
    {
        _host.ToggleSelection(this);
    }

    /// <summary>卡片整体点击：选中或多选 toggle。由 View 路由调用。</summary>
    public void HandleCardClick()
    {
        if (IsSelectionMode)
        {
            _host.ToggleSelection(this);
        }
        else
        {
            _host.SelectCard(this);
        }
    }

    [RelayCommand]
    private async Task ToggleVoiceLockAsync()
    {
        await _host.ToggleVoiceLockAsync(this, !IsVoiceLocked);
        _host.RefreshCardFromHost(this);
    }

    [RelayCommand]
    private async Task SetTreatmentAsync(string? raw)
    {
        if (!Enum.TryParse<SubtitleTreatment>(raw, ignoreCase: true, out var t)) return;
        await _host.SetSubtitleTreatmentAsync(this, t);
        _host.RefreshCardFromHost(this);
    }

    [RelayCommand]
    private void CommitMask()
    {
        _host.CommitMaskRect(this);
    }

    [RelayCommand]
    private async Task ApplyMaskToAllAsync()
    {
        await _host.ApplyMaskToAllAsync(this);
        _host.RefreshCardFromHost(this);
    }

    [RelayCommand]
    private async Task ReRecognizeAsync()
    {
        var newText = await _host.ReRecognizeSegmentAsync(this);
        if (!string.IsNullOrEmpty(newText))
        {
            TranscriptText = newText;
        }
        _host.RefreshCardFromHost(this);
    }

    // ============ 台词内联编辑（对齐 mac 卡片 ✏ → TextEditor → saveText） ============

    /// <summary>进入编辑态：草稿填当前台词，聚焦 TextBox。</summary>
    [RelayCommand]
    private void BeginEditText()
    {
        DraftText = TranscriptText ?? string.Empty;
        IsEditingText = true;
    }

    /// <summary>取消编辑：丢弃草稿。</summary>
    [RelayCommand]
    private void CancelEditText()
    {
        IsEditingText = false;
    }

    /// <summary>保存编辑：落库 + 同步内存 + 刷新 UI。</summary>
    [RelayCommand]
    private async Task SaveTextAsync()
    {
        var saved = await _host.SaveSegmentTextAsync(this, DraftText);
        TranscriptText = saved;
        IsEditingText = false;
    }

    // ============ 同步刷新 ============

    /// <summary>
    /// 父 VM 改动 segment 字段（StartTime/EndTime/Text 等）后调用，把 segment 同步到 CardVM 的 ObservableProperty
    /// 触发 binding 刷新。host 在 AdjustStartTime/SetStartTime 末尾调用。
    /// </summary>
    public void RefreshFromSegment(Segment? updated = null)
    {
        // RebuildGroups 复用本卡时传入库里最新查到的 Segment（EF 短上下文每次建新对象）：
        // 换掉持有的旧引用，否则「上传/拆分后 text/标签写库了、但卡片读的还是旧空对象」→ 台词/标签不刷新。
        if (updated is not null) _segment = updated;
        StartTime = _segment.StartTime;
        EndTime = _segment.EndTime;
        QualityScore = _segment.QualityScore;
        TranscriptText = _segment.Text ?? string.Empty;
        PositionType = _segment.PositionType;
        SemanticTypes = new ObservableCollection<SemanticType>(_segment.SemanticTypes);
        // #12：有替换画面时取替换缩略图（EffectivePicture 单一真源），否则原缩略图。
        ThumbnailPath = _segment.EffectivePicture.ThumbnailPath ?? _segment.ThumbnailPath;
        SegmentIndexLabel = _segment.SegmentIndex ?? string.Empty;
        // P3 字幕处理属性刷新
        OnPropertyChanged(nameof(IsVoiceLocked));
        OnPropertyChanged(nameof(SubtitleTreatment));
        OnPropertyChanged(nameof(NeedsMaskEditor));
        OnPropertyChanged(nameof(SubtitlePreviewVisible));
        OnPropertyChanged(nameof(MaskRect));
        // 逐分镜字号：Agent / 重载后刷新滑块与字号预览
        OnPropertyChanged(nameof(FontRatio));
        OnPropertyChanged(nameof(FontPercentText));
        OnPropertyChanged(nameof(SegmentFontRatio));
        // #12：合成/切换/删除替换画面后刷新「切换画面」胶囊 + 预览图。
        OnPropertyChanged(nameof(HasReplacedPicture));
        OnPropertyChanged(nameof(PictureShowsReplaced));
        OnPropertyChanged(nameof(PictureToggleLabel));
        OnPropertyChanged(nameof(PreviewImage));
    }

    public void Dispose()
    {
        // 解绑事件（避免泄漏：CardVM 被复用 / Project 切换时旧 VM 不再持有引用）
        Infrastructure.ThumbnailCache.Shared.ImageLoaded -= OnThumbnailLoaded;
    }
}

/// <summary>
/// SegmentCardViewModel 反向调用 host（SegmentLibraryViewModel）的接口。
/// 走 V1 已有的 AdjustStartTime/AdjustEndTime/SetStartTime/SetEndTime 路径，
/// 这些方法内部会 ReExtractText（从 ASR 重新提取台词）+ Save。
/// </summary>
public interface ISegmentCardHost
{
    void AdjustStartTime(SegmentCardViewModel card, double step);
    void AdjustEndTime(SegmentCardViewModel card, double step);
    void SetStartTime(SegmentCardViewModel card, double newStart);
    void SetEndTime(SegmentCardViewModel card, double newEnd);
    void RequestDelete(SegmentCardViewModel card);
    void ToggleSemanticType(SegmentCardViewModel card, SemanticType type);
    void UpdatePositionType(SegmentCardViewModel card, PositionType type);
    void ToggleSelection(SegmentCardViewModel card);
    void SelectCard(SegmentCardViewModel card);
    /// <summary>P3：切换「保留原声」并持久化。</summary>
    Task ToggleVoiceLockAsync(SegmentCardViewModel card, bool locked);
    /// <summary>P3：设置字幕处理方式并持久化。</summary>
    Task SetSubtitleTreatmentAsync(SegmentCardViewModel card, SubtitleTreatment treatment);
    /// <summary>P3：遮挡框拖拽松手 → 落库一次。</summary>
    void CommitMaskRect(SegmentCardViewModel card);
    /// <summary>逐分镜字号滑块停手 → 落库一次（并记为新分镜默认值）。</summary>
    void CommitFontRatio(SegmentCardViewModel card);
    /// <summary>P3：把当前分镜的字幕处理+遮挡框应用到同视频其余分镜。</summary>
    Task ApplyMaskToAllAsync(SegmentCardViewModel card);
    /// <summary>P3：刷新卡片字幕处理绑定（host 持久化后调用）。</summary>
    void RefreshCardFromHost(SegmentCardViewModel card);
    /// <summary>P6：单分镜 paraformer ASR 重识别，只替换台词文本。</summary>
    Task<string> ReRecognizeSegmentAsync(SegmentCardViewModel card, CancellationToken ct = default);
    /// <summary>手动编辑台词落库（对齐 mac saveText）。返回清洗后的文本。</summary>
    Task<string> SaveSegmentTextAsync(SegmentCardViewModel card, string newText, CancellationToken ct = default);

    /// <summary>#12：打开「分镜头替换」工作区。</summary>
    Task RequestReplaceShotAsync(SegmentCardViewModel card);

    /// <summary>#18：请求打开分镜拆分窗口（View 订阅后开窗）。</summary>
    void RequestSplit(SegmentCardViewModel card);
    /// <summary>#12：在原画面 ↔ 替换画面间切换（已有替换画面时）。</summary>
    Task ToggleReplacedPictureAsync(SegmentCardViewModel card);
    /// <summary>#12：删除替换画面、还原为原画面。</summary>
    Task DeleteReplacedPictureAsync(SegmentCardViewModel card);
}
