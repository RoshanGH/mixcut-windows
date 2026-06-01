using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using MixCut.Models;
using MixCut.Services.Export;
using MixCut.Utilities;

namespace MixCut.Views;

/// <summary>
/// 批量导出分镜对话框。对应 macOS 版 BatchExportSheet。
/// 选目录 → 文件列表预览 → 进度 → 结果报告。
/// </summary>
public partial class BatchExportDialog : Window
{
    private readonly BatchSegmentExportService _exportService;
    private readonly AppSettings _settings;
    private readonly IReadOnlyList<BatchExportItem> _items;
    private CancellationTokenSource? _cts;
    private string? _outputDirectory;
    private bool _isExporting;
    private bool _didFinish;

    public BatchExportDialog(
        BatchSegmentExportService exportService,
        AppSettings settings,
        IReadOnlyList<Segment> segments,
        Func<Segment, int> numberProvider)
    {
        _exportService = exportService;
        _settings = settings;
        InitializeComponent();

        _items = segments
            .Where(s => s.Video is not null && !string.IsNullOrEmpty(s.Video.LocalPath))
            .Select(s => new BatchExportItem(
                Id: s.Id,
                SourcePath: s.Video!.LocalPath,
                SourceVideoName: Path.GetFileNameWithoutExtension(s.Video.Name),
                StartTime: s.StartTime,
                EndTime: s.EndTime,
                SequenceNumber: numberProvider(s)))
            .ToList();

        TitleText.Text = $"批量导出 {_items.Count} 个分镜";

        // 显示文件列表
        FileListLabel.Text = $"文件列表（{_items.Count} 个）";
        var totalDur = _items.Sum(i => i.Duration);
        TotalDurationText.Text = $"总时长 {totalDur.ToString("F1", CultureInfo.InvariantCulture)} 秒";
        FileListItems.ItemsSource = _items.Select(i => new FileListItem(
            FileName: i.FileName,
            DurationText: $"{i.Duration.ToString("F1", CultureInfo.InvariantCulture)}s"))
            .ToList();

        // 恢复上次目录
        if (!string.IsNullOrEmpty(_settings.LastBatchExportDirectory)
            && Directory.Exists(_settings.LastBatchExportDirectory))
        {
            SetOutputDirectory(_settings.LastBatchExportDirectory);
        }

        UpdatePrimaryButtonState();
    }

    private record FileListItem(string FileName, string DurationText);

    private void SetOutputDirectory(string dir)
    {
        _outputDirectory = dir;
        OutputDirText.Text = dir;
        OutputDirText.Foreground = System.Windows.Media.Brushes.Black;
        UpdatePrimaryButtonState();
    }

    private void UpdatePrimaryButtonState()
    {
        PrimaryButton.IsEnabled = !_isExporting && _outputDirectory is not null && _items.Count > 0;
    }

    private void OnPickDirectory(object sender, RoutedEventArgs e)
    {
        // .NET 8 的 WPF 自带 OpenFolderDialog（Microsoft.Win32），不再需要 WinForms 兜底。
        var dlg = new OpenFolderDialog
        {
            Title = "选择导出目录",
            InitialDirectory = _outputDirectory
                ?? (Directory.Exists(_settings.LastBatchExportDirectory)
                    ? _settings.LastBatchExportDirectory
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
        };
        if (dlg.ShowDialog(this) == true && !string.IsNullOrEmpty(dlg.FolderName))
        {
            SetOutputDirectory(dlg.FolderName);
            _settings.LastBatchExportDirectory = dlg.FolderName;
        }
    }

    private async void OnPrimary(object sender, RoutedEventArgs e)
    {
        // async void 必须 try/catch
        try
        {
            if (_didFinish)
            {
                DialogResult = true;
                Close();
                return;
            }

            if (_outputDirectory is null) return;

            _isExporting = true;
            UpdatePrimaryButtonState();
            ConfigPanel.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Visible;
            TitleText.Text = "导出中";
            PrimaryButton.Content = "请稍候...";

            _cts = new CancellationTokenSource();
            var dir = _outputDirectory;

            var result = await _exportService.ExportAllAsync(
                _items, dir,
                progress => Dispatcher.Invoke(() => OnProgress(progress)),
                _cts.Token);

            // 完成态
            _isExporting = false;
            _didFinish = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
            ResultPanel.Visibility = Visibility.Visible;
            TitleText.Text = "导出完成";

            var allOk = result.Failed.Count == 0;
            ResultIconText.Text = allOk ? "✓" : "⚠";
            ResultIconText.Foreground = allOk
                ? new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x2E, 0x8B, 0x57))
                : new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xC0, 0x6F, 0x00));
            ResultTitleText.Text = allOk ? "全部完成" : "部分完成";
            ResultSummaryText.Text = $"成功 {result.Succeeded} 个，失败 {result.Failed.Count} 个";
            ResultPathText.Text = $"输出目录：{dir}";

            if (result.Failed.Count > 0)
            {
                FailedListBox.Visibility = Visibility.Visible;
                FailedItems.ItemsSource = result.Failed
                    .Select(p => new FailedItemDisplay(p.Item1.FileName, p.Item2))
                    .ToList();
            }

            OpenFolderButton.Visibility = Visibility.Visible;
            CancelButton.Visibility = Visibility.Collapsed;
            PrimaryButton.Content = "完成";
            UpdatePrimaryButtonState();
            PrimaryButton.IsEnabled = true;

            // Toast 通知（即使对话框关闭，主窗口仍能看到结果）
            if (allOk)
            {
                Shared.ToastCenter.Shared.Show(
                    $"已导出 {result.Succeeded} 个分镜", Shared.ToastStyle.Success);
            }
            else
            {
                Shared.ToastCenter.Shared.Show(
                    $"导出 {result.Succeeded} 成功 / {result.Failed.Count} 失败",
                    Shared.ToastStyle.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            DialogResult = false;
            Close();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"批量导出失败：\n{ex.Message}",
                "MixCut", MessageBoxButton.OK, MessageBoxImage.Error);
            DialogResult = false;
            Close();
        }
        finally
        {
            // P0-23：无论成功/取消/异常都释放 CTS，避免每次导出累积一个未释放的 SafeHandle
            // （批量导出失败几十次后内核句柄耗尽）。此时 await 已结束，_cts 不会再被用到；
            // OnCancel 里的 _cts?.Cancel() 对 null 安全。
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnProgress(SegmentBatchExportProgress p)
    {
        ProgressCountText.Text = $"{p.CompletedCount} / {p.TotalCount} 已完成";
        ProgressPctText.Text = $"{(int)(p.TotalProgress * 100)}%";
        ProgressBar.Value = p.TotalProgress;
        if (p.CurrentItem is not null)
        {
            CurrentItemText.Text = $"当前：{p.CurrentItem.FileName}";
            CurrentSubText.Text = $"切片中 {p.CurrentItem.Duration.ToString("F1", CultureInfo.InvariantCulture)} 秒";
        }
        if (p.FailedItems.Count > 0)
        {
            FailedCountText.Visibility = Visibility.Visible;
            FailedCountText.Text = $"⚠ 已失败 {p.FailedItems.Count} 个";
        }
    }

    private record FailedItemDisplay(string Name, string Error);

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        DialogResult = false;
        Close();
    }

    private void OnOpenOutputFolder(object sender, RoutedEventArgs e)
    {
        if (_outputDirectory is null || !Directory.Exists(_outputDirectory)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_outputDirectory}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // 忽略
        }
    }
}
