using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MixCut.Models;

namespace MixCut.ViewModels.Cards;

/// <summary>
/// 按视频分组的容器 VM（SegmentLibrary V2 用）。
/// 一个 VideoGroupViewModel = 一个视频的标题栏 + 配音设置条 + 它的全部分镜卡片。
/// </summary>
public sealed partial class VideoGroupViewModel : ObservableObject, IDisposable
{
    private readonly DubbingViewModel? _dubbing;
    private bool _disposed;

    public Video Video { get; }

    public Guid VideoId => Video.Id;

    public string VideoName => Video.Name ?? "未命名视频";

    /// <summary>"3 个分镜 · 12s" 这种统计文本。</summary>
    public string MetaText { get; private set; }

    /// <summary>#17：是否为「自建分镜」聚合组（多个载体视频合并、置顶、隐藏视频级配音条）。</summary>
    public bool IsUserSegmentGroup { get; private set; }

    /// <summary>组头显示名：自建分镜组固定「自建分镜」，普通组用视频名。</summary>
    public string DisplayName => IsUserSegmentGroup ? "自建分镜" : VideoName;

    /// <summary>
    /// 视频编号（issue #23）：与素材导入页徽章、Agent 的 video_no 同一套规则——项目内按导入时间升序 1 起。
    /// 0 = 不显示（「自建分镜」聚合组由多个载体视频合并而成，不编号）。
    /// </summary>
    public int VideoNo { get; set; }

    /// <summary>编号徽章文本。</summary>
    public string VideoNoText => $"{VideoNo} 号";

    /// <summary>编号徽章可见性（自建分镜组隐藏）。</summary>
    public bool ShowVideoNo => !IsUserSegmentGroup && VideoNo > 0;

    /// <summary>是否显示视频级配音设置条（自建分镜组隐藏 —— 每个自建分镜是独立载体、逐卡各自操作）。</summary>
    public bool ShowDubBar => !IsUserSegmentGroup;

    /// <summary>
    /// #17：构造「自建分镜」聚合组（carrier 仅用于缩略图占位；dubbing 传 null，不显示视频级配音条）。
    /// </summary>
    public static VideoGroupViewModel CreateUserSegmentGroup(Video carrier, IEnumerable<SegmentCardViewModel> cards)
    {
        var g = new VideoGroupViewModel(carrier, cards, dubbing: null);
        g.IsUserSegmentGroup = true;
        g.MetaText = $"{g.Segments.Count} 个自建分镜";
        return g;
    }

    public ImageSource? ThumbnailImage { get; }

    /// <summary>本组的分镜卡片（视图绑定到这里）。</summary>
    public ObservableCollection<SegmentCardViewModel> Segments { get; }

    public VideoGroupViewModel(Video video, IEnumerable<SegmentCardViewModel> cards,
        DubbingViewModel? dubbing = null, DubStatusSnapshot? dubStatus = null)
    {
        Video = video;
        _dubbing = dubbing;
        Segments = new ObservableCollection<SegmentCardViewModel>(cards);
        var dur = video.Duration > 0 ? $" · {video.Duration:F0}s" : string.Empty;
        MetaText = $"{Segments.Count} 个分镜{dur}";
        ThumbnailImage = LoadThumbnail(video.ThumbnailPath);

        if (_dubbing is not null)
        {
            _variantCount = _dubbing.VariantCount;
            _dubbing.VideoStateChanged += OnDubVideoStateChanged;
            // 注意：这里**不主动查库**。配音状态由 RebuildGroups 批量预取后统一灌进来
            // （见 RefreshDubStatusAsync 的 prefetched 参数）。曾经在这里 fire-and-forget 查两次，
            // 结果每次重建分组 = 视频数 × 2 次 SQL × 2 个 DbContext。
            _ = RefreshDubStatusAsync(dubStatus);
        }
    }

    // ---- 配音设置条（v0.5.0）----

    /// <summary>变体数（1~5，全局设置）。设置条 stepper 绑定。</summary>
    [ObservableProperty] private int _variantCount = 2;

    partial void OnVariantCountChanged(int value)
    {
        if (_dubbing is not null) _dubbing.VariantCount = value;
        OnPropertyChanged(nameof(CanIncVariant));
        OnPropertyChanged(nameof(CanDecVariant));
    }

    /// <summary>变体数未达上限（5）——「＋」按钮 IsEnabled 绑定，到限即禁用而非静默无响应。</summary>
    public bool CanIncVariant => VariantCount < 5;
    /// <summary>变体数未达下限（1）——「−」按钮 IsEnabled 绑定。</summary>
    public bool CanDecVariant => VariantCount > 1;

    /// <summary>本视频是否正在配音处理中（克隆/改写/合成）。</summary>
    [ObservableProperty] private bool _isDubBusy;

    /// <summary>非忙碌（按钮/stepper 的 IsEnabled 绑定）。</summary>
    public bool IsNotDubBusy => !IsDubBusy;

    partial void OnIsDubBusyChanged(bool value) => OnPropertyChanged(nameof(IsNotDubBusy));

    /// <summary>忙碌时的进度文案（带阶段序号，如「③ 改写台词 第 1/2 套…」）。</summary>
    [ObservableProperty] private string _dubProgress = string.Empty;

    /// <summary>忙碌进度比例（0~1）。仅 DubProgressIndeterminate=false 时有意义。</summary>
    [ObservableProperty] private double _dubProgressValue;

    /// <summary>该阶段无法估算百分比（分离人声 / 克隆音色）→ UI 走无限滚动进度条。</summary>
    [ObservableProperty] private bool _dubProgressIndeterminate = true;

    /// <summary>「60%」百分比文案；无法估算时为空（UI 隐藏）。</summary>
    [ObservableProperty] private string _dubPercentText = string.Empty;

    /// <summary>空闲时的状态文案，如「✓ 6 个变体」。</summary>
    [ObservableProperty] private string _dubStatusText = string.Empty;

    /// <summary>主按钮文案：未克隆「克隆并改写配音」/ 已克隆「改写配音」。</summary>
    [ObservableProperty] private string _dubMainButtonText = "克隆并改写配音";

    private void OnDubVideoStateChanged(Guid videoId)
    {
        if (videoId != VideoId || _dubbing is null) return;
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            IsDubBusy = _dubbing.IsBusy(VideoId);
            var info = _dubbing.ProgressInfo(VideoId);
            DubProgress = info.Text;
            if (info.Fraction >= 0)
            {
                // 可估算阶段：百分比进度条 + 「N%」
                DubProgressIndeterminate = false;
                DubProgressValue = info.Fraction;
                DubPercentText = $"{(int)System.Math.Round(info.Fraction * 100)}%";
            }
            else
            {
                // 不可估算阶段（分离人声 / 克隆）：无限滚动条，不显示百分比
                DubProgressIndeterminate = true;
                DubProgressValue = 0;
                DubPercentText = string.Empty;
            }
            if (!IsDubBusy) _ = RefreshDubStatusAsync();
        });
    }

    /// <summary>刷新「✓ N 个变体」+ 主按钮文案（克隆与否）。</summary>
    /// <param name="prefetched">
    /// 批量预取的配音状态。传入时直接用，<b>一次数据库都不查</b>。
    /// RebuildGroups 会为所有视频一次性查好再逐组传进来 —— 否则每组构造各开 2 个 DbContext 查 2 次，
    /// 一个 10 视频的项目每次搜索/筛选/排序就是 20 次 SQL（见下方构造函数注释）。
    /// 传 null 表示「单组自行刷新」（配音进度推送时只有那一个视频要更新，走原路径即可）。
    /// </param>
    public async Task RefreshDubStatusAsync(DubStatusSnapshot? prefetched = null)
    {
        if (_dubbing is null) return;
        bool cloned;
        int n;
        if (prefetched is not null)
        {
            cloned = prefetched.IsCloned;
            n = prefetched.VariantCount;
        }
        else
        {
            cloned = await _dubbing.IsClonedAsync(VideoId);
            n = await _dubbing.EffectiveVariantCountAsync(VideoId);
        }
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            DubMainButtonText = cloned ? "改写配音" : "克隆并改写配音";
            DubStatusText = n > 0 ? $"✓ {n} 个变体" : string.Empty;
        });
    }

    /// <summary>一个视频的配音状态快照（批量预取用）。</summary>
    public sealed record DubStatusSnapshot(bool IsCloned, int VariantCount);

    /// <summary>
    /// 退订配音事件。<b>必须调用</b> —— 组 VM 在每次 RebuildGroups 都会整批重建，
    /// 若不退订，被丢弃的旧组仍挂在 DubbingViewModel.VideoStateChanged 上：搜索/筛选 10 次
    /// 就有 10 份同视频的僵尸组 VM，配音每推一次进度它们全部被唤醒、各自再查一轮数据库。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_dubbing is not null)
        {
            _dubbing.VideoStateChanged -= OnDubVideoStateChanged;
        }
    }

    [RelayCommand]
    private async Task RewriteAllAsync()
    {
        if (_dubbing is null) return;

        // 计费确认：这一下点下去 = 分镜数 × 变体数 次 AI 改写 + 同样次数的 TTS 克隆合成，
        // 40 个分镜拨到 ×5 就是 200 次付费调用，且发出去的请求取消也退不回来。
        // 同一个应用里 AI 画面重生成是有「生成并计费」确认的，配音这条路却一路绿灯 —— 对齐它。
        var count = Segments.Count;
        var total = count * Math.Max(1, VariantCount);
        var ok = Views.Shared.MixCutDialog.Confirm(
            System.Windows.Application.Current?.MainWindow,
            $"为「{VideoName}」的 {count} 个分镜各生成 {VariantCount} 版配音？",
            $"共约 {total} 次 AI 调用（改写台词 + 克隆音色合成），按用量计费。\n\n"
            + "生成期间可以随时取消，但已经发出的请求不会退费。",
            confirmText: "生成并计费", cancelText: "再想想", icon: "💳");
        if (!ok) return;

        await _dubbing.RewriteAllAsync(VideoId);
    }

    /// <summary>P1-5：取消本视频正在进行的配音流水线。</summary>
    [RelayCommand]
    private void CancelDub() => _dubbing?.CancelDub(VideoId);

    [RelayCommand]
    private void IncVariant() { if (VariantCount < 5) VariantCount++; }

    [RelayCommand]
    private void DecVariant() { if (VariantCount > 1) VariantCount--; }

    /// <summary>用新的 cards 集合替换并重新计算 meta（保持 ObservableCollection 实例不变，触发 diff 而非整体替换）。</summary>
    public void ReplaceSegments(IEnumerable<SegmentCardViewModel> cards)
    {
        Segments.Clear();
        foreach (var c in cards)
        {
            Segments.Add(c);
        }
        OnPropertyChanged(nameof(MetaText));
    }

    private static ImageSource? LoadThumbnail(string? path) =>
        Infrastructure.ThumbnailCache.Shared.GetImage(path);
}
