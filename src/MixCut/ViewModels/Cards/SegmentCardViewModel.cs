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
    private readonly Segment _segment;
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
        _thumbnailPath = segment.ThumbnailPath;
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

    /// <summary>ThumbnailCache 通知某张图加载完成时调用，若路径匹配则刷新 binding。</summary>
    private void OnThumbnailLoaded(string loadedPath)
    {
        if (loadedPath == ThumbnailPath)
        {
            // 切到 UI 线程触发 PropertyChanged（event 可能从 ThreadPool 线程触发）
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                OnPropertyChanged(nameof(ThumbnailImage));
            }
            else
            {
                dispatcher.BeginInvoke(new Action(() => OnPropertyChanged(nameof(ThumbnailImage))));
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
    private double _startTime;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationDisplay))]
    [NotifyPropertyChangedFor(nameof(Duration))]
    private double _endTime;

    [ObservableProperty]
    private double _qualityScore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TranscriptCharCount))]
    [NotifyPropertyChangedFor(nameof(HasTranscript))]
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
    private bool _isSelected;

    /// <summary>鼠标是否悬停在卡片上。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBoundaryRowVisible))]
    private bool _isHovering;

    /// <summary>多选模式下是否被勾选。</summary>
    [ObservableProperty]
    private bool _isChecked;

    /// <summary>多选模式总开关（父 VM 推下来）。</summary>
    [ObservableProperty]
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

    public int TranscriptCharCount => TranscriptText?.Length ?? 0;

    public bool HasTranscript => !string.IsNullOrWhiteSpace(TranscriptText);

    // ============ 命令 ============

    [RelayCommand]
    private void AdjustStart(string? stepStr)
    {
        var step = ParseStep(stepStr, 0.1);
        // 通过 host 走 V1 的 AdjustStartTime 路径（含 ReExtractText 重新提取台词 + Save）
        _host.AdjustStartTime(this, step);
    }

    [RelayCommand]
    private void AdjustEnd(string? stepStr)
    {
        var step = ParseStep(stepStr, 0.1);
        _host.AdjustEndTime(this, step);
    }

    [RelayCommand]
    private void CommitStart(string? text)
    {
        if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            _host.SetStartTime(this, v);
        }
    }

    [RelayCommand]
    private void CommitEnd(string? text)
    {
        if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            _host.SetEndTime(this, v);
        }
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

    // ============ 同步刷新 ============

    /// <summary>
    /// 父 VM 改动 segment 字段（StartTime/EndTime/Text 等）后调用，把 segment 同步到 CardVM 的 ObservableProperty
    /// 触发 binding 刷新。host 在 AdjustStartTime/SetStartTime 末尾调用。
    /// </summary>
    public void RefreshFromSegment()
    {
        StartTime = _segment.StartTime;
        EndTime = _segment.EndTime;
        QualityScore = _segment.QualityScore;
        TranscriptText = _segment.Text ?? string.Empty;
        PositionType = _segment.PositionType;
        SemanticTypes = new ObservableCollection<SemanticType>(_segment.SemanticTypes);
        ThumbnailPath = _segment.ThumbnailPath;
        SegmentIndexLabel = _segment.SegmentIndex ?? string.Empty;
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
}
