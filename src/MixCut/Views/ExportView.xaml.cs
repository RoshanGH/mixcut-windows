using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MixCut.Infrastructure;
using MixCut.Models;
using MixCut.Services.Export;
using MixCut.ViewModels;

namespace MixCut.Views;

/// <summary>导出视图。对应 macOS 版 ExportView（单方案 + 批量导出）。</summary>
public partial class ExportView : UserControl, IProjectView
{
    private readonly SchemeViewModel _schemeVM;
    private readonly ExportService _exportService;
    private string? _lastOutputDir;

    public ExportView(SchemeViewModel schemeVM, ExportService exportService)
    {
        _schemeVM = schemeVM;
        _exportService = exportService;
        InitializeComponent();

        foreach (var r in Enum.GetValues<ExportResolution>())
        {
            ResolutionCombo.Items.Add(r.Label());
        }
        ResolutionCombo.SelectedIndex = 0;
        foreach (var c in Enum.GetValues<ExportCodec>())
        {
            CodecCombo.Items.Add(c.Label());
        }
        CodecCombo.SelectedIndex = 0;
        foreach (var q in Enum.GetValues<ExportQuality>())
        {
            QualityCombo.Items.Add(q.Label());
        }
        QualityCombo.SelectedIndex = 2;

        // 显示硬件加速探测结果（NVIDIA / Intel / AMD / MF / 无）
        HardwareStatusText.Text = "硬件加速：" + HardwareEncoderProbe.HardwareDescription;
        HardwareStatusText.Foreground = HardwareEncoderProbe.HasAnyHardware
            ? new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2E, 0x8B, 0x57))     // 绿：有硬件
            : new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0xC0, 0x6F, 0x00));    // 橙：仅软件
        UpdateQualityHint();
    }

    /// <summary>编码器/质量/分辨率变化时刷新质量提示（码率 + 文件大小估算）。</summary>
    private void OnConfigChanged(object? sender, SelectionChangedEventArgs e) => UpdateQualityHint();

    private void UpdateQualityHint()
    {
        if (QualityHintText is null) return;
        var cfg = BuildConfig();
        QualityHintText.Text = cfg.QualityHint;
        // 配置变了 → 预估大小也要刷新
        RefreshEstimatedSize();
    }

    public void LoadProject(Project project)
    {
        _schemeVM.LoadSchemes(project);
        RefreshOverview();
    }

    /// <summary>按当前 ExportConfig + 总时长刷新预估大小，对齐 Mac ExportView 概览第四项。</summary>
    private void RefreshEstimatedSize()
    {
        if (EstimatedSizeText is null) return;
        var totalDuration = _schemeVM.Schemes.Sum(s => s.EstimatedDuration);
        if (totalDuration <= 0)
        {
            EstimatedSizeText.Text = "—";
            return;
        }
        var sizeMB = BuildConfig().EstimatedTotalSizeMB(totalDuration);
        EstimatedSizeText.Text = sizeMB >= 1024
            ? (sizeMB / 1024).ToString("F1", CultureInfo.InvariantCulture) + " GB"
            : sizeMB.ToString("F0", CultureInfo.InvariantCulture) + " MB";
    }

    private void RefreshOverview()
    {
        var schemes = _schemeVM.Schemes;
        StrategyCountText.Text = _schemeVM.Strategies.Count.ToString(CultureInfo.InvariantCulture);
        SchemeCountText.Text = schemes.Count.ToString(CultureInfo.InvariantCulture);
        var totalDuration = schemes.Sum(s => s.EstimatedDuration);
        TotalDurationText.Text = totalDuration.ToString("F0", CultureInfo.InvariantCulture) + "s";
        RefreshEstimatedSize();

        var hasAny = schemes.Count > 0;
        NoSchemeHint.Visibility = hasAny ? Visibility.Collapsed : Visibility.Visible;

        SchemeCombo.ItemsSource = schemes;
        SchemeCombo.DisplayMemberPath = nameof(MixScheme.Name);
        if (schemes.Count > 0)
        {
            SchemeCombo.SelectedIndex = 0;
        }

        ExportSingleButton.IsEnabled = hasAny;
        ExportAllButton.IsEnabled = hasAny;
    }

    private ExportConfig BuildConfig() => new()
    {
        Resolution = (ExportResolution)Math.Max(0, ResolutionCombo.SelectedIndex),
        Codec = (ExportCodec)Math.Max(0, CodecCombo.SelectedIndex),
        Quality = (ExportQuality)Math.Max(0, QualityCombo.SelectedIndex),
    };

    // ---- 单方案导出 ----

    private async void OnExportSingleClick(object sender, RoutedEventArgs e)
    {
        if (SchemeCombo.SelectedItem is not MixScheme scheme)
        {
            MessageBox.Show("请先选择一个方案", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var input = ExportInput.FromScheme(scheme);
        if (input is null)
        {
            MessageBox.Show("方案中没有有效的分镜（可能视频文件不存在）", "无法导出",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Filter = "MP4 视频|*.mp4",
            FileName = SanitizeFileName(scheme.Name) + ".mp4",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        ExportSingleButton.IsEnabled = false;
        ExportAllButton.IsEnabled = false;
        CompletePanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ProgressSection.Visibility = Visibility.Visible;
        ProgressTitle.Text = "导出中…";

        try
        {
            await _exportService.ExportAsync(input, dialog.FileName, BuildConfig(),
                p => Dispatcher.Invoke(() =>
                {
                    ProgressStatusText.Text = p.Description;
                    ProgressBar.Value = p.Progress;
                    ProgressDetailText.Text = $"目标文件：{Path.GetFileName(dialog.FileName)}";
                }));

            _lastOutputDir = Path.GetDirectoryName(dialog.FileName);
            CompletePanel.Visibility = Visibility.Visible;
            CompleteText.Text = Path.GetFileName(dialog.FileName);
            Components.ToastService.Show("✓ 导出完成", Components.ToastStyle.Success);
        }
        catch (Exception ex)
        {
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorText.Text = ex.Message;
            Components.ToastService.Show("导出失败：" + ex.Message, Components.ToastStyle.Error);
        }
        finally
        {
            ProgressSection.Visibility = Visibility.Collapsed;
            ExportSingleButton.IsEnabled = true;
            ExportAllButton.IsEnabled = true;
        }
    }

    // ---- 批量导出 ----

    private async void OnExportAllClick(object sender, RoutedEventArgs e)
    {
        var schemes = _schemeVM.Schemes;
        if (schemes.Count == 0)
        {
            return;
        }

        var dialog = new OpenFolderDialog { Title = "选择输出文件夹" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var outputDir = dialog.FolderName;
        _lastOutputDir = outputDir;

        // 准备所有导出任务：过滤掉无效方案 + 计算文件名。
        var config = BuildConfig();
        var tasks = new List<(MixScheme Scheme, ExportInput Input, string OutputPath)>();
        var skipped = 0;
        for (var i = 0; i < schemes.Count; i++)
        {
            var scheme = schemes[i];
            var input = ExportInput.FromScheme(scheme);
            if (input is null)
            {
                skipped++;
                continue;
            }
            // 命名：策略名_变体序号_方案名.mp4
            var strategyName = SanitizeFileName(scheme.Strategy?.Name ?? "未分组");
            var schemeName = SanitizeFileName(scheme.Name);
            var fileName = $"{strategyName}_{scheme.VariationIndex:D2}_{schemeName}.mp4";
            tasks.Add((scheme, input, Path.Combine(outputDir, fileName)));
        }

        if (tasks.Count == 0)
        {
            MessageBox.Show("没有有效的方案可导出（视频文件可能丢失）",
                "无法导出", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // v0.5.0：走 ConcurrencyPolicy 统一策略，有 GPU 编码时加成（NVENC/QSV/AMF +3 路）。
        var concurrency = Infrastructure.ConcurrencyPolicy.MaxExportConcurrency(tasks.Count);

        CompletePanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ProgressSection.Visibility = Visibility.Visible;
        ProgressTitle.Text = $"批量导出（共 {tasks.Count} 个 · {concurrency} 路并行）";
        ExportSingleButton.IsEnabled = false;
        ExportAllButton.IsEnabled = false;

        var success = 0;
        var errors = new List<string>();
        var completed = 0;
        var currentTaskNames = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();

        using var semaphore = new SemaphoreSlim(concurrency);
        var exportJobs = tasks.Select(async (task, slot) =>
        {
            await semaphore.WaitAsync();
            try
            {
                currentTaskNames[slot] = task.Scheme.Name;
                UpdateConcurrentProgress();

                await _exportService.ExportAsync(task.Input, task.OutputPath, config,
                    _ => UpdateConcurrentProgress());
                Interlocked.Increment(ref success);
            }
            catch (Exception ex)
            {
                lock (errors) { errors.Add($"{task.Scheme.Name}: {ex.Message}"); }
            }
            finally
            {
                currentTaskNames.TryRemove(slot, out _);
                Interlocked.Increment(ref completed);
                UpdateConcurrentProgress();
                semaphore.Release();
            }
        });
        await Task.WhenAll(exportJobs);

        ProgressSection.Visibility = Visibility.Collapsed;
        ExportSingleButton.IsEnabled = true;
        ExportAllButton.IsEnabled = true;

        if (errors.Count == 0)
        {
            CompletePanel.Visibility = Visibility.Visible;
            CompleteText.Text = $"共导出 {success} 个视频" +
                                (skipped > 0 ? $"（跳过 {skipped} 个无效方案）" : string.Empty) +
                                $"\n输出目录：{outputDir}";
            Components.ToastService.Show($"✓ 批量导出完成 {success} 个", Components.ToastStyle.Success);
        }
        else
        {
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorText.Text = $"成功 {success}/{tasks.Count} 个：\n"
                             + string.Join("\n", errors.Take(5))
                             + (errors.Count > 5 ? $"\n…还有 {errors.Count - 5} 个错误" : string.Empty);
            Components.ToastService.Show(
                success > 0
                    ? $"⚠ 部分失败：成功 {success}/{tasks.Count}"
                    : "导出全部失败",
                success > 0 ? Components.ToastStyle.Warning : Components.ToastStyle.Error);
        }

        void UpdateConcurrentProgress()
        {
            var done = completed;
            var inProgress = string.Join("、", currentTaskNames.Values.Take(2));
            if (currentTaskNames.Count > 2)
            {
                inProgress += $" 等 {currentTaskNames.Count} 个";
            }
            Dispatcher.Invoke(() =>
            {
                ProgressBar.Value = (double)done / tasks.Count;
                ProgressStatusText.Text = $"已完成 {done}/{tasks.Count}";
                ProgressDetailText.Text = string.IsNullOrEmpty(inProgress)
                    ? string.Empty : "进行中：" + inProgress;
            });
        }
    }

    private void OnOpenOutputFolder(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastOutputDir) || !Directory.Exists(_lastOutputDir))
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe", Arguments = _lastOutputDir, UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // 忽略
        }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return string.IsNullOrWhiteSpace(name) ? "MixCut" : name;
    }
}
