using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MixCut.Infrastructure;
using MixCut.Models;
using MixCut.Services.Export;
using MixCut.Utilities;
using MixCut.ViewModels;

namespace MixCut.Views;

/// <summary>导出视图。对应 macOS 版 ExportView（单方案 + 批量导出）。</summary>
public partial class ExportView : UserControl, IProjectView
{
    private readonly SchemeViewModel _schemeVM;
    private readonly ExportService _exportService;
    private readonly Services.Dubbing.DubExportService _dubExport;
    private readonly AppSettings _settings;
    private string? _lastOutputDir;

    /// <summary>QW-11：批量导出的取消令牌。ExportService 早就支持 CancellationToken，
    /// 但 view 从来没传过 —— 现在接上，「取消」按钮 / ESC 能真正中断长任务。</summary>
    private CancellationTokenSource? _exportCts;

    /// <summary>用户勾选的待导出方案 ID 集合。LoadProject 时默认全选当前项目所有方案。</summary>
    private readonly HashSet<Guid> _selectedSchemeIds = new();

    public ExportView(SchemeViewModel schemeVM, ExportService exportService,
        Services.Dubbing.DubExportService dubExport, AppSettings settings)
    {
        _schemeVM = schemeVM;
        _exportService = exportService;
        _dubExport = dubExport;
        _settings = settings;
        InitializeComponent();

        foreach (var r in Enum.GetValues<ExportResolution>())
        {
            ResolutionCombo.Items.Add(r.Label());
        }
        // 默认选 1080p（enum 顺序 Original=0, P1080=1）：与 ExportConfig 默认对齐，
        // 避免「代码默认 1080p、UI 却显示原始分辨率」的矛盾。4K 仍可手选「原始分辨率」。
        ResolutionCombo.SelectedIndex = (int)ExportResolution.P1080;
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
        TotalDurationText.Text = Utilities.FrameTime.HumanDuration(totalDuration);
        RefreshEstimatedSize();

        var hasAny = schemes.Count > 0;
        NoSchemeHint.Visibility = hasAny ? Visibility.Collapsed : Visibility.Visible;

        // v0.6.0 起单方案导出区块移除（筛选导出已覆盖此场景：选 1 个等同于原「单方案导出」）。
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
        UpdateDubButtonText();
        UpdateSelectedCountLabel();
    }

    /// <summary>配音组合导出按钮：N = 选中方案的组合数之和（每方案按 SchemeComboPlanner 上限封顶）。</summary>
    private void UpdateDubButtonText()
    {
        if (ExportDubButton is null) return;
        var selected = _schemeVM.Schemes.Where(s => _selectedSchemeIds.Contains(s.Id)).ToList();
        var totalCombos = 0;
        var anyDub = false;
        foreach (var s in selected)
        {
            var feasible = Services.Dubbing.SchemeComboPlanner.FeasibleCount(s);
            totalCombos += Math.Min(feasible, Services.Dubbing.SchemeComboPlanner.MaxCombos);
            // 该方案有可用配音变体（feasible > 分镜全锁定/无变体时的 1）→ 才算「有配音可导」
            if (s.OrderedSegments.Any(ss => ss.Segment is { IsVoiceLocked: false } seg && seg.EffectiveDubVariants.Count > 0))
            {
                anyDub = true;
            }
        }
        if (selected.Count == 0 || !anyDub)
        {
            ExportDubButton.Content = "🎤  导出配音组合";
            ExportDubButton.IsEnabled = false;
        }
        else
        {
            ExportDubButton.Content = $"🎤  导出配音组合（共 {totalCombos} 条）";
            ExportDubButton.IsEnabled = true;
        }
    }

    private ExportConfig BuildConfig() => new()
    {
        Resolution = (ExportResolution)Math.Max(0, ResolutionCombo.SelectedIndex),
        Codec = (ExportCodec)Math.Max(0, CodecCombo.SelectedIndex),
        Quality = (ExportQuality)Math.Max(0, QualityCombo.SelectedIndex),
    };

    // ---- 批量导出（v0.6.0 起单方案区块已删除，统一走筛选导出） ----

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
        // QW-3：跨会话记忆上次导出目录，免去每次重新点开 5 层目录。
        if (!string.IsNullOrEmpty(_settings.LastExportDirForSchemes)
            && Directory.Exists(_settings.LastExportDirForSchemes))
        {
            dialog.InitialDirectory = _settings.LastExportDirForSchemes;
        }
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var outputDir = dialog.FolderName;
        _lastOutputDir = outputDir;
        _settings.LastExportDirForSchemes = outputDir;   // QW-3：记住本次选择，下次默认定位到这里

        // 准备所有导出任务：过滤掉无效方案 + 计算文件名。
        var config = BuildConfig();
        var tasks = new List<(MixScheme Scheme, ExportInput Input, string OutputPath)>();
        var skipped = 0;
        var segmentSkipped = 0;
        for (var i = 0; i < schemes.Count; i++)
        {
            var scheme = schemes[i];
            var input = ExportInput.FromScheme(scheme);
            if (input is null)
            {
                skipped++;
                continue;
            }
            segmentSkipped += input.SkippedCount; // 方案内源文件丢失的分镜数，累计后告知用户
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

        if (segmentSkipped > 0)
        {
            Components.ToastService.Show(
                $"有 {segmentSkipped} 个分镜的源文件已丢失，导出时已自动跳过", Components.ToastStyle.Warning);
        }

        // QW-4：导出前检查文件冲突。目录里已存在同名 .mp4（上次导出 / 别的项目）会被静默覆盖，
        // 用户数小时的渲染结果可能瞬间消失。列出冲突文件让用户确认后再继续。
        var conflicts = tasks.Where(t => File.Exists(t.OutputPath)).ToList();
        if (conflicts.Count > 0)
        {
            var preview = string.Join("\n",
                conflicts.Take(5).Select(c => "• " + Path.GetFileName(c.OutputPath)));
            if (conflicts.Count > 5)
            {
                preview += $"\n…还有 {conflicts.Count - 5} 个";
            }
            var confirm = MessageBox.Show(
                $"以下 {conflicts.Count} 个文件已存在，继续导出会覆盖它们：\n\n{preview}\n\n确定要覆盖吗？",
                "文件已存在", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK)
            {
                return;
            }
        }

        // v0.5.0：走 ConcurrencyPolicy 统一策略，有 GPU 编码时加成（NVENC/QSV/AMF +3 路）。
        // 并按本批最高输出分辨率额外封顶（4K filter graph 极吃内存，多路并发会 OOM）。
        var maxPixels = tasks.Max(t =>
        {
            var res = Services.Export.ExportService.ResolveResolution(
                config.Resolution, t.Input.MaxWidth, t.Input.MaxHeight);
            var parts = res.Split(':');
            return (parts.Length == 2
                    && long.TryParse(parts[0], out var w) && w > 0
                    && long.TryParse(parts[1], out var h) && h > 0)
                ? w * h : 1920L * 1080L;
        });
        var concurrency = Infrastructure.ConcurrencyPolicy.MaxExportConcurrency(tasks.Count, maxPixels);
        Serilog.Log.Information(
            "[ExportConcurrency] tasks={Tasks} pixels={Pixels} concurrency={Concurrency} 说明={Explain}",
            tasks.Count, maxPixels, concurrency,
            Infrastructure.ConcurrencyPolicy.ExplainExportFormula(maxPixels));

        CompletePanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ProgressSection.Visibility = Visibility.Visible;
        ProgressTitle.Text = $"批量导出（共 {tasks.Count} 个 · {concurrency} 路并行）";
        ExportAllButton.IsEnabled = false;

        // QW-11：每次导出新建取消令牌，「取消」按钮 / ESC 触发后整批 ffmpeg 立即收手。
        _exportCts?.Dispose();
        _exportCts = new CancellationTokenSource();
        var token = _exportCts.Token;
        CancelExportButton.IsEnabled = true;

        var success = 0;
        var errors = new List<string>();
        var completed = 0;
        var canceled = false;
        var currentTaskNames = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();

        using var semaphore = new SemaphoreSlim(concurrency);
        var exportJobs = tasks.Select(async (task, slot) =>
        {
            await semaphore.WaitAsync(token);
            try
            {
                currentTaskNames[slot] = task.Scheme.Name;
                UpdateConcurrentProgress();

                await _exportService.ExportAsync(task.Input, task.OutputPath, config,
                    _ => UpdateConcurrentProgress(), token);
                Interlocked.Increment(ref success);
            }
            catch (OperationCanceledException)
            {
                // 用户主动取消 —— 不计入失败，单独走「已取消」分支。
                canceled = true;
            }
            catch (Exception ex)
            {
                // 翻译成人话 + 可操作建议；原始 exit/stderr 已在 [ffmpeg-fail] 日志。
                var friendly = Services.Export.ExportErrorMessage.ToFriendly(ex);
                lock (errors) { errors.Add($"{task.Scheme.Name}: {friendly}"); }
            }
            finally
            {
                currentTaskNames.TryRemove(slot, out _);
                Interlocked.Increment(ref completed);
                UpdateConcurrentProgress();
                semaphore.Release();
            }
        });

        // semaphore.WaitAsync(token) 在取消后会抛 OperationCanceledException，
        // 用 Task.WhenAll 收集时整体也会抛，统一在这里兜成「已取消」状态。
        try
        {
            await Task.WhenAll(exportJobs);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        ProgressSection.Visibility = Visibility.Collapsed;
        CancelExportButton.IsEnabled = false;
        UpdateExportButtonText(); // ExportAllButton 状态按当前选择数计算

        if (canceled)
        {
            Components.ToastService.Show(
                $"已取消导出（已完成 {success}/{tasks.Count} 个）", Components.ToastStyle.Warning);
        }
        else if (errors.Count == 0)
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

    // ---- 配音组合导出（v0.5.0）：跨选中方案笛卡尔积展开成 N 条，逐条出片 ----

    private async void OnExportDubCombosClick(object sender, RoutedEventArgs e)
    {
        var snapshotIds = new HashSet<Guid>(_selectedSchemeIds);
        var schemes = _schemeVM.Schemes.Where(s => snapshotIds.Contains(s.Id)).ToList();
        if (schemes.Count == 0) return;

        // 展开每个方案的全部配音组合（笛卡尔积，每方案封顶 MaxCombos）。
        var jobs = new List<(string Name, Services.Dubbing.DubExportInput Input, string FileBase)>();
        var truncatedSchemes = 0;
        foreach (var scheme in schemes)
        {
            var plan = Services.Dubbing.SchemeComboPlanner.Build(scheme);
            if (plan.Truncated) truncatedSchemes++;
            var strategyName = SanitizeFileName(scheme.Strategy?.Name ?? "未分组");
            var schemeName = SanitizeFileName(scheme.Name);
            foreach (var combo in plan.Combos)
            {
                var input = Services.Dubbing.DubExportInput.From(scheme, combo.Choices);
                if (input is null) continue;
                jobs.Add(($"{scheme.Name} {combo.NameSuffix}", input, $"{strategyName}_{schemeName}{SanitizeFileName(combo.NameSuffix)}"));
            }
        }

        if (jobs.Count == 0)
        {
            MessageBox.Show("没有可导出的配音组合（请先在分镜库「克隆并改写配音」生成变体）",
                "无法导出", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 确认弹窗（PRD §7.2）：先告知将生成多少条。
        var truncNote = truncatedSchemes > 0 ? $"\n（有 {truncatedSchemes} 个方案组合数超上限，已按每方案前 {Services.Dubbing.SchemeComboPlanner.MaxCombos} 条截取）" : "";
        var confirm = MessageBox.Show(
            $"将生成 {jobs.Count} 条视频\n\n选中 {schemes.Count} 个方案，画面不变，按每个分镜的「原声 + 各改写版」全部排列组合逐条生成。{truncNote}",
            "导出配音组合", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (confirm != MessageBoxResult.OK) return;

        var dialog = new OpenFolderDialog { Title = "选择输出文件夹" };
        if (!string.IsNullOrEmpty(_settings.LastExportDirForSchemes) && Directory.Exists(_settings.LastExportDirForSchemes))
        {
            dialog.InitialDirectory = _settings.LastExportDirForSchemes;
        }
        if (dialog.ShowDialog() != true) return;
        var outputDir = dialog.FolderName;
        _lastOutputDir = outputDir;
        _settings.LastExportDirForSchemes = outputDir;

        var config = BuildConfig();
        // 落地输出路径 + 文件名冲突检查。
        var tasks = jobs.Select(j => (j.Name, j.Input, Path.Combine(outputDir, j.FileBase + ".mp4"))).ToList();
        var conflicts = tasks.Where(t => File.Exists(t.Item3)).ToList();
        if (conflicts.Count > 0)
        {
            var preview = string.Join("\n", conflicts.Take(5).Select(c => "• " + Path.GetFileName(c.Item3)));
            if (conflicts.Count > 5) preview += $"\n…还有 {conflicts.Count - 5} 个";
            if (MessageBox.Show($"以下 {conflicts.Count} 个文件已存在，继续会覆盖：\n\n{preview}\n\n确定覆盖吗？",
                    "文件已存在", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            {
                return;
            }
        }

        // 自适应并发：配音导出每条都是多分镜渲染（较重），按硬件 + 最高分辨率封顶。
        var maxPixels = tasks.Max(t => (long)t.Input.MaxWidth * Math.Max(1, t.Input.MaxHeight));
        if (maxPixels <= 0) maxPixels = 1080L * 1920L;
        var concurrency = Infrastructure.ConcurrencyPolicy.MaxExportConcurrency(tasks.Count, maxPixels);

        CompletePanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ProgressSection.Visibility = Visibility.Visible;
        ProgressTitle.Text = $"导出配音组合（共 {tasks.Count} 条 · {concurrency} 路并行）";
        ExportAllButton.IsEnabled = false;
        ExportDubButton.IsEnabled = false;

        _exportCts?.Dispose();
        _exportCts = new CancellationTokenSource();
        var token = _exportCts.Token;
        CancelExportButton.IsEnabled = true;

        var success = 0;
        var errors = new List<string>();
        var completed = 0;
        var canceled = false;
        var current = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();

        using var semaphore = new SemaphoreSlim(concurrency);
        var exportJobs = tasks.Select(async (task, slot) =>
        {
            await semaphore.WaitAsync(token);
            try
            {
                current[slot] = task.Name;
                Report();
                await _dubExport.ExportAsync(task.Input, task.Item3, config, _ => Report(), token);
                Interlocked.Increment(ref success);
            }
            catch (OperationCanceledException) { canceled = true; }
            catch (Exception ex)
            {
                lock (errors) { errors.Add($"{task.Name}: {ex.Message}"); }
            }
            finally
            {
                current.TryRemove(slot, out _);
                Interlocked.Increment(ref completed);
                Report();
                semaphore.Release();
            }
        });

        try { await Task.WhenAll(exportJobs); }
        catch (OperationCanceledException) { canceled = true; }

        ProgressSection.Visibility = Visibility.Collapsed;
        CancelExportButton.IsEnabled = false;
        UpdateExportButtonText();

        if (canceled)
        {
            Components.ToastService.Show($"已取消（已完成 {success}/{tasks.Count} 条）", Components.ToastStyle.Warning);
        }
        else if (errors.Count == 0)
        {
            CompletePanel.Visibility = Visibility.Visible;
            CompleteText.Text = $"共导出 {success} 条配音视频\n输出目录：{outputDir}";
            Components.ToastService.Show($"✓ 配音导出完成 {success} 条", Components.ToastStyle.Success);
        }
        else
        {
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorText.Text = $"成功 {success}/{tasks.Count} 条：\n" + string.Join("\n", errors.Take(5))
                             + (errors.Count > 5 ? $"\n…还有 {errors.Count - 5} 个错误" : "");
            Components.ToastService.Show(success > 0 ? $"⚠ 部分失败：成功 {success}/{tasks.Count}" : "配音导出全部失败",
                success > 0 ? Components.ToastStyle.Warning : Components.ToastStyle.Error);
        }

        void Report()
        {
            var done = completed;
            var inProgress = string.Join("、", current.Values.Take(2));
            if (current.Count > 2) inProgress += $" 等 {current.Count} 个";
            Dispatcher.Invoke(() =>
            {
                ProgressBar.Value = (double)done / tasks.Count;
                ProgressStatusText.Text = $"已完成 {done}/{tasks.Count}";
                ProgressDetailText.Text = string.IsNullOrEmpty(inProgress) ? "" : "进行中：" + inProgress;
            });
        }
    }

    /// <summary>QW-11：取消批量导出。按钮点击或 ESC 触发，整批正在跑的 ffmpeg 收到 token 后停。</summary>
    private void OnCancelExportClick(object sender, RoutedEventArgs e)
    {
        if (_exportCts is null || _exportCts.IsCancellationRequested)
        {
            return;
        }
        CancelExportButton.IsEnabled = false;
        ProgressStatusText.Text = "正在取消…";
        Serilog.Log.Information("[ExportCancel] 用户取消批量导出");
        _exportCts.Cancel();
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
