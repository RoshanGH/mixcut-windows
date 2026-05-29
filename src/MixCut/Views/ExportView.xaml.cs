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

    /// <summary>用户勾选的待导出方案 ID 集合。LoadProject 时默认全选当前项目所有方案。</summary>
    private readonly HashSet<Guid> _selectedSchemeIds = new();

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

        // 默认全选当前项目所有方案（对齐 Mac v0.3.0 默认全选语义）
        // 重置 _selectedSchemeIds 保证上个项目的选择状态不会泄露过来
        _selectedSchemeIds.Clear();
        foreach (var strategy in _schemeVM.Strategies)
        {
            foreach (var scheme in strategy.Schemes)
            {
                _selectedSchemeIds.Add(scheme.Id);
            }
        }

        RefreshOverview();
        RefreshSelectionPanel();
        UpdateExportButtonText();
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
        // ExportAllButton.IsEnabled 由 UpdateExportButtonText 根据 _selectedSchemeIds 计数管理
    }

    // ---- 批量导出选择面板（v0.3.0：嵌入式策略 checkbox 列表 + 三态半选）----

    /// <summary>渲染策略 / 方案 checkbox 树。每次切项目 / 全选反选清空时调用。</summary>
    private void RefreshSelectionPanel()
    {
        StrategyTree.Items.Clear();
        var strategies = _schemeVM.Strategies;
        if (strategies.Count == 0)
        {
            SelectionPanel.Visibility = Visibility.Collapsed;
            return;
        }
        SelectionPanel.Visibility = Visibility.Visible;

        foreach (var strategy in strategies)
        {
            StrategyTree.Items.Add(BuildStrategyGroup(strategy));
        }

        UpdateSelectedCountLabel();
    }

    /// <summary>构建单个策略的折叠组：策略三态 checkbox + 子方案 checkbox 列表。</summary>
    private UIElement BuildStrategyGroup(MixStrategy strategy)
    {
        var sp = new StackPanel();

        var strategyCheck = new CheckBox
        {
            IsThreeState = true,
            Tag = strategy,
            Margin = new Thickness(0, 4, 0, 4),
            Content = new TextBlock
            {
                Text = strategy.Name,
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x33, 0x33, 0x33)),
            },
        };
        strategyCheck.IsChecked = ComputeStrategyTriState(strategy);
        strategyCheck.Click += OnStrategyCheckClick;
        sp.Children.Add(strategyCheck);

        var childPanel = new StackPanel { Margin = new Thickness(22, 0, 0, 6) };
        foreach (var scheme in strategy.OrderedSchemes)
        {
            childPanel.Children.Add(BuildSchemeCheck(scheme, strategy));
        }
        sp.Children.Add(childPanel);

        return sp;
    }

    private UIElement BuildSchemeCheck(MixScheme scheme, MixStrategy parent)
    {
        var cb = new CheckBox
        {
            Tag = (scheme, parent),
            IsChecked = _selectedSchemeIds.Contains(scheme.Id),
            Margin = new Thickness(0, 2, 0, 2),
        };
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(new TextBlock
        {
            Text = scheme.Name, FontSize = 11,
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55)),
        });
        if (scheme.IsManuallyEdited)
        {
            label.Children.Add(new TextBlock
            {
                Text = "·已修改", FontSize = 9,
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x80, 0x80, 0x80)),
                Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            });
        }
        cb.Content = label;
        cb.Click += OnSchemeCheckClick;
        return cb;
    }

    /// <summary>三态计算：全选 → true；全空 → false；部分 → null。</summary>
    private bool? ComputeStrategyTriState(MixStrategy strategy)
    {
        if (strategy.Schemes.Count == 0) return false;
        var selectedInStrategy = strategy.Schemes.Count(s => _selectedSchemeIds.Contains(s.Id));
        if (selectedInStrategy == 0) return false;
        if (selectedInStrategy == strategy.Schemes.Count) return true;
        return null;
    }

    private void OnStrategyCheckClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.Tag is not MixStrategy strategy) return;
        // WPF IsThreeState=true 默认点击循环：true → false → null → true
        // 我们要简化为：半选(null) → 全选(true)；全选(true) → 全空(false)；全空(false) → 全选(true)
        // 此时 cb.IsChecked 已经是 WPF 帮我们切到下一帧的值，需要根据上一帧推断目标态。
        // 简化策略：只要不是全选就置全选，否则清空。
        var newState = ComputeStrategyTriState(strategy) != true; // 上一帧非全选 → 全选；上一帧全选 → 清空
        cb.IsChecked = newState;

        if (newState)
        {
            foreach (var s in strategy.Schemes) _selectedSchemeIds.Add(s.Id);
        }
        else
        {
            foreach (var s in strategy.Schemes) _selectedSchemeIds.Remove(s.Id);
        }
        RefreshSelectionPanel();
        UpdateExportButtonText();
    }

    private void OnSchemeCheckClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb) return;
        if (cb.Tag is not ValueTuple<MixScheme, MixStrategy> tup) return;
        var (scheme, _) = tup;
        if (cb.IsChecked == true)
        {
            _selectedSchemeIds.Add(scheme.Id);
        }
        else
        {
            _selectedSchemeIds.Remove(scheme.Id);
        }
        RefreshSelectionPanel(); // 刷父三态
        UpdateExportButtonText();
    }

    private void OnSelectAllSchemesClick(object sender, RoutedEventArgs e)
    {
        _selectedSchemeIds.Clear();
        foreach (var strategy in _schemeVM.Strategies)
            foreach (var s in strategy.Schemes) _selectedSchemeIds.Add(s.Id);
        RefreshSelectionPanel();
        UpdateExportButtonText();
    }

    private void OnInvertSchemesClick(object sender, RoutedEventArgs e)
    {
        var allIds = _schemeVM.Strategies.SelectMany(st => st.Schemes).Select(s => s.Id).ToHashSet();
        var newSel = allIds.Except(_selectedSchemeIds).ToList();
        _selectedSchemeIds.Clear();
        foreach (var id in newSel) _selectedSchemeIds.Add(id);
        RefreshSelectionPanel();
        UpdateExportButtonText();
    }

    private void OnClearSchemesClick(object sender, RoutedEventArgs e)
    {
        _selectedSchemeIds.Clear();
        RefreshSelectionPanel();
        UpdateExportButtonText();
    }

    private void UpdateSelectedCountLabel()
    {
        var total = _schemeVM.Strategies.Sum(st => st.Schemes.Count);
        SelectedCountLabel.Text = $"已选 {_selectedSchemeIds.Count}/{total}";
    }

    private void UpdateExportButtonText()
    {
        var n = _selectedSchemeIds.Count;
        if (n == 0)
        {
            ExportAllButton.Content = "请先选择方案";
            ExportAllButton.IsEnabled = false;
        }
        else
        {
            ExportAllButton.Content = $"📦  导出选中的 {n} 个";
            ExportAllButton.IsEnabled = true;
        }
        UpdateSelectedCountLabel();
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
            UpdateExportButtonText(); // ExportAllButton 状态按当前选择数计算（N=0 时仍禁用）
        }
    }

    // ---- 批量导出 ----

    private async void OnExportAllClick(object sender, RoutedEventArgs e)
    {
        // 用户在异步导出过程中可能切项目 / 改选择，snapshot 一份避免被并发改写。
        var snapshotIds = new HashSet<Guid>(_selectedSchemeIds);
        var schemes = _schemeVM.Schemes.Where(s => snapshotIds.Contains(s.Id)).ToList();
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
        UpdateExportButtonText(); // ExportAllButton 状态按当前选择数计算

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
