using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MixCut.Models;
using MixCut.ViewModels;

namespace MixCut.Views;

/// <summary>素材导入视图。对应 macOS 版 ImportView。</summary>
public partial class ImportView : UserControl, IProjectView
{
    private readonly ImportViewModel _importVM;
    private readonly Action _onChanged;
    private Project? _project;
    private List<VideoRow> _rows = new();

    public ImportView(ImportViewModel importVM, Action onChanged)
    {
        _importVM = importVM;
        _onChanged = onChanged;
        InitializeComponent();
        _importVM.PropertyChanged += OnImportVMChanged;
        _importVM.VideoProgressChanged += OnVideoProgressChanged;
        // 导入流程中每完成一个视频 commit DB 后 fire 此事件，立刻刷视频列表
        // （之前依赖 _project.Videos navigation，stale entity 拿不到新插入的视频）。
        _importVM.VideoListChanged += OnVideoListChanged;
    }

    public void LoadProject(Project project)
    {
        _project = project;
        // 切到 import 视图时清掉旧的错误 banner，避免历史错误（如上次某个视频被并发删除）误导用户
        // 当前正在处理中除外（保留 IsProcessing 期间的实时报错）
        if (!_importVM.IsProcessing)
        {
            var prop = typeof(ImportViewModel).GetProperty(nameof(ImportViewModel.ErrorMessage));
            prop?.SetValue(_importVM, null);
        }
        RefreshVideoList();
        RefreshProgress();
    }

    private void OnVideoListChanged()
    {
        Dispatcher.BeginInvoke(RefreshVideoList);
    }

    private void RefreshVideoList()
    {
        if (_project is null)
        {
            return;
        }
        // 必须从 DB 重新拉，因为 _project 是 stale 实体；阶段 1 内每完成一个视频立即出现卡片，
        // ASR / AI 任务作为「处理中」覆盖层显示在卡片内（对齐 Mac 体验）。
        var videos = _importVM.GetProjectVideos(_project.Id);
        _rows = videos.Select(v => new VideoRow(v)).ToList();
        VideoList.ItemsSource = _rows;
        VideoListHeader.Visibility = _rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        VideoCountText.Text = $"共 {_rows.Count} 个视频";

        foreach (var row in _rows)
        {
            var p = _importVM.GetVideoProgress(row.Video.Id);
            row.ProgressOverall = p.OverallPercent;
            row.StageLabel = p.StageLabel;
        }
    }

    /// <summary>单视频进度变化 —— 不重建卡片，只更新对应 VideoRow 的字段（INPC 触发 UI 增量刷新）。</summary>
    private void OnVideoProgressChanged(Guid videoId, VideoProgressState state)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var row = _rows.FirstOrDefault(r => r.Video.Id == videoId);
            if (row is null) return;
            row.ProgressOverall = state.OverallPercent;
            row.StageLabel = state.StageLabel;
        });
    }

    private void OnImportVMChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            RefreshProgress();
            // 仅在阶段切换或处理状态变化时重建视频卡片，避免每次小进度变化都重新创建所有卡片
            // ——后者会把正在播放的 InlineVideoPlayer 强制重置。
            if (e.PropertyName == nameof(ImportViewModel.Phase) ||
                e.PropertyName == nameof(ImportViewModel.IsProcessing))
            {
                RefreshVideoList();
            }
        });
    }

    private void RefreshProgress()
    {
        ProgressPanel.Visibility = _importVM.IsProcessing ? Visibility.Visible : Visibility.Collapsed;
        PhaseText.Text = ImportPhaseLabel(_importVM.Phase);
        ProgressText.Text = _importVM.ProgressDescription;
        ProgressBar.Value = _importVM.Progress;

        var error = _importVM.ErrorMessage;
        ErrorBanner.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = error ?? string.Empty;
    }

    private static string ImportPhaseLabel(ImportPhase phase) => phase switch
    {
        ImportPhase.Idle => "等待导入",
        ImportPhase.Copying => "复制文件中",
        ImportPhase.ExtractingMetadata => "提取元数据",
        ImportPhase.GeneratingThumbnail => "生成缩略图",
        ImportPhase.DetectingScenes => "镜头检测",
        ImportPhase.Transcribing => "语音识别",
        ImportPhase.Analyzing => "AI 语义分析",
        ImportPhase.Optimizing => "优化边界",
        ImportPhase.Completed => "完成",
        ImportPhase.Failed => "失败",
        _ => phase.ToString(),
    };

    private void OnDismissError(object sender, RoutedEventArgs e)
    {
        var prop = typeof(ImportViewModel).GetProperty(nameof(ImportViewModel.ErrorMessage));
        prop?.SetValue(_importVM, null);
        RefreshProgress();
    }

    // ---- 文件选择与拖拽 ----

    private void OnImportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = ImportViewModel.FileDialogFilter,
        };
        if (dialog.ShowDialog() == true)
        {
            _ = ImportAsync(dialog.FileNames);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var ok = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (ok)
        {
            DropBorderBrush.Color = Color.FromRgb(0x1D, 0x6B, 0xE5);
            DropBackgroundBrush.Color = Color.FromRgb(0xE7, 0xF0, 0xFF);
            DropIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x6B, 0xE5));
            // 对齐 Mac drop zone：拖入瞬间换文字
            DropMainText.Text = "松开即可导入";
            DropMainText.Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x6B, 0xE5));
        }
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        DropBorderBrush.Color = Color.FromRgb(0xCC, 0xCC, 0xCC);
        DropBackgroundBrush.Color = Color.FromRgb(0xFA, 0xFA, 0xFA);
        DropIcon.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
        DropMainText.Text = "拖拽视频文件到此处";
        DropMainText.Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        OnDragLeave(sender, e);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var videos = files.Where(IsVideoFile).ToArray();
            if (videos.Length > 0)
            {
                _ = ImportAsync(videos);
            }
        }
    }

    private static bool IsVideoFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".mp4" or ".mov" or ".avi" or ".m4v" or ".mkv";
    }

    private async Task ImportAsync(IReadOnlyList<string> paths)
    {
        if (_project is null)
        {
            return;
        }
        await _importVM.ImportVideosAsync(paths, _project.Id);
        _onChanged();
    }

    // ---- 卡片操作 ----

    private async void OnRetryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not VideoRow row)
        {
            return;
        }

        // 立即给出视觉反馈，避免「点了没反应」错觉。
        var originalContent = button.Content;
        button.IsEnabled = false;
        button.Content = "重试中…";
        try
        {
            await _importVM.RetryAnalysisAsync(row.Video.Id);
        }
        finally
        {
            button.Content = originalContent;
            button.IsEnabled = true;
            _onChanged();
        }
    }

    private void OnDeleteVideoClick(object sender, RoutedEventArgs e) => DeleteVideo(sender as FrameworkElement);

    private void OnDeleteVideoMenu(object sender, RoutedEventArgs e) => DeleteVideo(sender as FrameworkElement);

    private void DeleteVideo(FrameworkElement? source)
    {
        if (source?.Tag is not VideoRow row || _project is null)
        {
            return;
        }
        var confirm = MessageBox.Show(
            $"确定要删除视频「{row.Video.Name}」吗？\n视频文件和相关分镜数据都将被删除，此操作不可恢复。",
            "确认删除", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm == MessageBoxResult.OK)
        {
            var name = row.Video.Name;
            _importVM.DeleteVideo(row.Video.Id, _project.Id);
            _onChanged();
            Components.ToastService.Show($"已删除「{name}」", Components.ToastStyle.Warning);
        }
    }

    private void OnCopyName(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: VideoRow row })
        {
            try
            {
                Clipboard.SetText(row.Video.Name);
                Components.ToastService.Show("文件名已复制", Components.ToastStyle.Success);
            }
            catch (Exception)
            {
                // 复制失败不阻塞
            }
        }
    }

    /// <summary>
    /// DataTemplate 里 InlineVideoPlayer 加载时绑定视频路径。
    /// （UserControl 无法直接 XAML 绑定方法调用，统一在 Loaded 里 SetVideo。）
    /// </summary>
    private void OnInlinePlayerLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Components.InlineVideoPlayer player)
        {
            return;
        }
        if (player.Tag is not VideoRow row)
        {
            return;
        }
        if (string.IsNullOrEmpty(row.VideoLocalPath))
        {
            return;
        }
        player.SetVideo(row.VideoLocalPath, row.VideoThumbnailPath);
    }

    /// <summary>「重做」按钮：仅重新跑 ASR（对齐 Mac retryASR 入口）。</summary>
    private async void OnRedoAsrClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not VideoRow row) return;

        var confirm = MessageBox.Show(
            $"重新识别「{row.Video.Name}」的台词？\n旧台词、分镜会被清除并重新生成。",
            "重新识别 ASR", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        Components.ToastService.Show("重新识别语音…", Components.ToastStyle.Info);
        button.IsEnabled = false;
        var originalContent = button.Content;
        button.Content = "重做中…";
        try
        {
            await _importVM.RetryASRAsync(row.Video.Id);
            Components.ToastService.Show("重新识别完成", Components.ToastStyle.Success);
        }
        finally
        {
            button.IsEnabled = true;
            button.Content = originalContent;
            _onChanged();
        }
    }

    private void OnShowInExplorer(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: VideoRow row } && File.Exists(row.Video.LocalPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{row.Video.LocalPath}\"",
                    UseShellExecute = true,
                });
            }
            catch (Exception)
            {
                // 忽略
            }
        }
    }

    /// <summary>卡片右侧台词面板的句子行（带时间戳）。</summary>
    public sealed record SentenceItem(string Time, string Text, int Index);

    /// <summary>
    /// 视频列表行显示模型。继承 ObservableObject 以支持实时进度更新——
    /// 处理中阶段切换、whisper 内部百分比变化都通过 INPC 单字段刷新，
    /// 不重建整张卡片（避免重置内联播放器状态）。
    /// </summary>
    public sealed partial class VideoRow : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        public Video Video { get; }
        public string Name => Video.Name;
        public string MetaText { get; }
        public string StatusLabel => Video.Status.ToLabel();
        public Brush BadgeBackground { get; }
        public Brush BadgeForeground { get; }

        public string ErrorText => Video.ErrorMessage?.Trim() ?? string.Empty;
        public Visibility ErrorVisibility =>
            string.IsNullOrEmpty(ErrorText) ? Visibility.Collapsed : Visibility.Visible;

        public Visibility RetryVisibility =>
            Video.Status == VideoStatus.Failed
            || (Video.Status == VideoStatus.Imported && Video.Segments.Count == 0)
                ? Visibility.Visible : Visibility.Collapsed;

        public ImageSource? ThumbnailImage { get; }
        public Brush OverlayBrush { get; }
        public string OverlayText { get; }
        public Visibility OverlayVisibility { get; }

        /// <summary>true 时左侧用 <see cref="Components.InlineVideoPlayer"/>，否则用带状态遮罩的静态缩略图。</summary>
        public bool UseInlinePlayer => Video.Status != VideoStatus.DetectingScenes
            && Video.Status != VideoStatus.Transcribing
            && Video.Status != VideoStatus.Analyzing
            && Video.Status != VideoStatus.Failed;
        public Visibility InlinePlayerVisibility =>
            UseInlinePlayer ? Visibility.Visible : Visibility.Collapsed;
        public Visibility StaticThumbnailVisibility =>
            UseInlinePlayer ? Visibility.Collapsed : Visibility.Visible;
        public string VideoLocalPath => Video.LocalPath;
        public string? VideoThumbnailPath => Video.ThumbnailPath;

        /// <summary>右侧台词面板的句子列表（带时间戳，源自 ASR sentences 或按句号兜底切分）。</summary>
        public IReadOnlyList<SentenceItem> Sentences { get; }
        public Visibility SentencesVisibility =>
            Sentences.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility EmptyTranscriptVisibility =>
            Sentences.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        public string TranscriptCharText { get; }

        /// <summary>
        /// ASR 分句粒度异常：句数=1 且时长>8s（明显该有多句被合一句）
        /// 或任一句字数 > 50（明显单句过长）。仅在视频已完成处理时检测。
        /// 对齐 Mac cachedIsASRAbnormal。
        /// </summary>
        public bool IsAsrAbnormal { get; }
        public Visibility AsrRedoVisibility =>
            IsAsrAbnormal ? Visibility.Visible : Visibility.Collapsed;
        public Visibility AsrAbnormalHintVisibility =>
            IsAsrAbnormal ? Visibility.Visible : Visibility.Collapsed;

        // ===== 实时进度字段（处理中由 ImportViewModel.VideoProgressChanged 更新） =====
        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
        private double _progressOverall;             // 0.0 - 1.0 整体进度

        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
        private string _stageLabel = string.Empty;   // "2/4 语音识别中 42%"

        /// <summary>当前是否在处理中（视频状态属于 DetectingScenes/Transcribing/Analyzing）。</summary>
        public bool IsBusy => Video.Status == VideoStatus.DetectingScenes
                              || Video.Status == VideoStatus.Transcribing
                              || Video.Status == VideoStatus.Analyzing;

        public Visibility BusyOverlayVisibility =>
            IsBusy ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>失败状态的左侧 4px 红色边带可见性。</summary>
        public Visibility FailedStripVisibility =>
            Video.Status == VideoStatus.Failed ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>完成状态的左侧绿色边带可见性。</summary>
        public Visibility CompletedStripVisibility =>
            Video.Status == VideoStatus.Completed ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>处理流水线 3 阶段状态（视频/ASR/AI）。</summary>
        public string PipelineText { get; }
        public Brush PipelineBackground { get; }
        public Visibility PipelineVisibility =>
            string.IsNullOrEmpty(PipelineText) ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>分镜类型分布汇总（如 噱头 2, 痛点 3）。</summary>
        public string SegmentTagsText { get; }
        public Visibility SegmentTagsVisibility =>
            string.IsNullOrEmpty(SegmentTagsText) ? Visibility.Collapsed : Visibility.Visible;

        public VideoRow(Video video)
        {
            Video = video;
            var duration = video.Duration > 0 ? FormatDuration(video.Duration) : "—";
            var resolution = video.Width > 0 ? video.Resolution : "未知";
            MetaText = $"{duration} · {resolution} · {video.Segments.Count} 分镜";
            ThumbnailImage = LoadThumbnail(video.ThumbnailPath);

            (BadgeBackground, BadgeForeground) = ResolveBadgeColors(video.Status);
            (OverlayBrush, OverlayText, OverlayVisibility) = ResolveOverlay(video.Status);

            Sentences = BuildSentences(video);
            var charCount = video.Transcript?.Length ?? 0;
            TranscriptCharText = charCount > 0 ? $"{charCount}字" : string.Empty;

            IsAsrAbnormal = DetectAsrAbnormal(video, Sentences);

            (PipelineText, PipelineBackground) = BuildPipeline(video);
            SegmentTagsText = BuildSegmentTags(video);
        }

        /// <summary>
        /// 检测 ASR 分句粒度异常。规则对齐 Mac computeIsASRAbnormal：
        /// - 仅在视频已完成（Completed）时检测；
        /// - 句数=1 且时长 > 8s：明显有多句被合成一句；
        /// - 任一句字数 > 50：单句过长。
        /// </summary>
        private static bool DetectAsrAbnormal(Video video, IReadOnlyList<SentenceItem> sentences)
        {
            if (video.Status != VideoStatus.Completed) return false;
            if (sentences.Count == 0) return false;
            if (sentences.Count == 1 && video.Duration > 8.0) return true;
            return sentences.Any(s => s.Text.Length > 50);
        }

        /// <summary>
        /// 从 ASR 数据构建带时间戳的句子列表。
        /// 优先用 Whisper 原生 sentences；否则按句末标点（。！？.!?）兜底切分 transcript。
        /// 对齐 macOS 版 ImportedVideoCard.formattedSentences。
        /// </summary>
        private static IReadOnlyList<SentenceItem> BuildSentences(Video video)
        {
            var asr = video.AsrSentences;
            if (asr is { Count: > 0 })
            {
                return asr.Select((s, i) => new SentenceItem(
                    Time: FormatTimestamp(s.Start),
                    Text: s.Text ?? string.Empty,
                    Index: i + 1)).ToList();
            }
            var transcript = video.Transcript?.Trim() ?? string.Empty;
            if (transcript.Length == 0)
            {
                return Array.Empty<SentenceItem>();
            }
            // 兜底按句末标点切分（无时间戳）
            var sentences = new List<SentenceItem>();
            var current = new System.Text.StringBuilder();
            foreach (var ch in transcript)
            {
                current.Append(ch);
                if ("。！？.!?".IndexOf(ch) >= 0)
                {
                    var t = current.ToString().Trim();
                    if (t.Length > 0)
                    {
                        sentences.Add(new SentenceItem(Time: string.Empty, Text: t, Index: sentences.Count + 1));
                    }
                    current.Clear();
                }
            }
            var tail = current.ToString().Trim();
            if (tail.Length > 0)
            {
                sentences.Add(new SentenceItem(Time: string.Empty, Text: tail, Index: sentences.Count + 1));
            }
            return sentences;
        }

        private static string FormatTimestamp(double seconds)
        {
            var mins = (int)seconds / 60;
            var secs = (int)seconds % 60;
            return $"{mins}:{secs:D2}";
        }

        /// <summary>构建流水线 3 阶段视觉指示。</summary>
        private static (string Text, Brush Bg) BuildPipeline(Video video)
        {
            if (video.Status == VideoStatus.Imported && video.Segments.Count > 0)
            {
                return (string.Empty, Brushes.Transparent);
            }
            // 各阶段是否完成
            var videoDone = video.Status != VideoStatus.Imported && video.Status != VideoStatus.DetectingScenes;
            var asrDone = !string.IsNullOrEmpty(video.Transcript);
            var aiDone = video.Status == VideoStatus.Completed;

            var videoActive = video.Status == VideoStatus.DetectingScenes;
            var asrActive = video.Status == VideoStatus.Transcribing;
            var aiActive = video.Status == VideoStatus.Analyzing;

            string Chip(string label, bool done, bool active)
            {
                if (active) return $"⏳ {label}";
                if (done) return $"✓ {label}";
                return $"○ {label}";
            }

            var text = $"{Chip("视频", videoDone, videoActive)}  →  " +
                       $"{Chip("ASR", asrDone, asrActive)}  →  " +
                       $"{Chip("AI", aiDone, aiActive)}";
            var bg = video.Status == VideoStatus.Completed
                ? (Brush)MakeBrush(0xE2, 0xF5, 0xE8)
                : video.Status == VideoStatus.Failed
                    ? MakeBrush(0xFD, 0xE2, 0xE2)
                    : MakeBrush(0xFF, 0xF5, 0xE0);
            return (text, bg);
        }

        /// <summary>构建分镜类型汇总文本（按数量降序前 5 个）。</summary>
        private static string BuildSegmentTags(Video video)
        {
            if (video.Segments.Count == 0)
            {
                return string.Empty;
            }
            var counts = new Dictionary<SemanticType, int>();
            foreach (var seg in video.Segments)
            {
                foreach (var t in seg.SemanticTypes)
                {
                    counts[t] = counts.GetValueOrDefault(t) + 1;
                }
            }
            var top = counts.OrderByDescending(kv => kv.Value).Take(5).ToList();
            if (top.Count == 0)
            {
                return string.Empty;
            }
            var parts = top.Select(kv => kv.Value > 1
                ? $"{kv.Key.ToLabel()}×{kv.Value}"
                : kv.Key.ToLabel());
            var text = "📋 " + string.Join(" · ", parts);
            if (counts.Count > 5)
            {
                text += $" +{counts.Count - 5}";
            }
            return text;
        }

        private static (Brush Background, Brush Foreground) ResolveBadgeColors(VideoStatus status) => status switch
        {
            VideoStatus.Imported => (MakeBrush(0xF0, 0xF0, 0xF2), MakeBrush(0x66, 0x66, 0x66)),
            VideoStatus.DetectingScenes or VideoStatus.Transcribing or VideoStatus.Analyzing
                => (MakeBrush(0xE7, 0xF0, 0xFF), MakeBrush(0x1D, 0x6B, 0xE5)),
            VideoStatus.Completed => (MakeBrush(0xE2, 0xF5, 0xE8), MakeBrush(0x2E, 0x8B, 0x57)),
            VideoStatus.Failed => (MakeBrush(0xFD, 0xE2, 0xE2), MakeBrush(0xD3, 0x3A, 0x3A)),
            _ => (MakeBrush(0xF0, 0xF0, 0xF2), MakeBrush(0x66, 0x66, 0x66)),
        };

        private static (Brush Overlay, string Text, Visibility Visibility) ResolveOverlay(VideoStatus status)
            => status switch
            {
                VideoStatus.DetectingScenes or VideoStatus.Transcribing or VideoStatus.Analyzing
                    => (new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)),
                        status.ToLabel(), Visibility.Visible),
                VideoStatus.Failed
                    => (new SolidColorBrush(Color.FromArgb(0xA0, 0xCC, 0, 0)),
                        "失败", Visibility.Visible),
                _ => (new SolidColorBrush(Colors.Transparent), string.Empty, Visibility.Collapsed),
            };

        private static SolidColorBrush MakeBrush(byte r, byte g, byte b) =>
            new(Color.FromRgb(r, g, b));

        private static string FormatDuration(double seconds)
        {
            var mins = (int)seconds / 60;
            var secs = (int)seconds % 60;
            return $"{mins}:{secs:D2}";
        }

        private static ImageSource? LoadThumbnail(string? path) =>
        Infrastructure.ThumbnailCache.Shared.GetImage(path);
    }
}
