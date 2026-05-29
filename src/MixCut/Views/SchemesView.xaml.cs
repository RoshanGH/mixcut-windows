using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MixCut.Models;
using MixCut.ViewModels;
using MixCut.Views.Shared;

namespace MixCut.Views;

/// <summary>混剪方案视图。对应 macOS 版 SchemeListView + SchemeDetailView。</summary>
public partial class SchemesView : UserControl, IProjectView
{
    private readonly SchemeViewModel _vm;
    private readonly SegmentLibraryViewModel? _segmentVm;
    private Project? _project;
    private readonly HashSet<Guid> _expandedStrategies = new();

    public SchemesView(SchemeViewModel vm)
        : this(vm, null) { }

    public SchemesView(SchemeViewModel vm, SegmentLibraryViewModel? segmentVm)
    {
        _vm = vm;
        _segmentVm = segmentVm;
        InitializeComponent();
        _vm.PropertyChanged += OnVmChanged;
    }

    public void LoadProject(Project project)
    {
        _project = project;
        _vm.LoadSchemes(project);

        // 默认展开第一个策略。
        _expandedStrategies.Clear();
        if (_vm.Strategies.Count > 0)
        {
            _expandedStrategies.Add(_vm.Strategies[0].Id);
        }

        GenerateButton.IsEnabled = project.SegmentCount > 0;
        RefreshStrategyList();
        RefreshDetail();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            ProgressBanner.Visibility = _vm.IsGenerating ? Visibility.Visible : Visibility.Collapsed;
            ProgressText.Text = _vm.GenerationProgress;
            GenerateButton.IsEnabled = !_vm.IsGenerating
                && _project is not null && _project.SegmentCount > 0;
            ErrorBanner.Visibility = string.IsNullOrEmpty(_vm.ErrorMessage)
                ? Visibility.Collapsed : Visibility.Visible;
            ErrorText.Text = _vm.ErrorMessage ?? string.Empty;
        });
    }

    private void OnDismissError(object sender, RoutedEventArgs e)
    {
        _vm.ErrorMessage = null;
    }

    // ---- 生成对话框 ----

    private async void OnOpenGenerateDialog(object sender, RoutedEventArgs e)
    {
        // async void 必须自己捕获所有异常 —— 否则会穿透到 SynchronizationContext 直接终止 WPF 进程。
        try
        {
            if (_project is null || _project.SegmentCount == 0)
            {
                return;
            }
            var dialog = new GenerateSchemeDialog(_project) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            await _vm.GenerateSchemesAsync(_project, dialog.TargetVideoCount, dialog.CustomPrompt);

            // 默认展开新生成的第一个策略。
            if (_vm.Strategies.Count > 0)
            {
                _expandedStrategies.Clear();
                _expandedStrategies.Add(_vm.Strategies[0].Id);
            }
            RefreshStrategyList();
            RefreshDetail();
        }
        catch (Exception ex)
        {
            // 完整 stack 写日志（File sink 已在 App.xaml.cs 配置，Log.Error 直接写盘）。
            Serilog.Log.Error(ex, "[SchemesView.OnOpenGenerateDialog] 异常: {Message}", ex.Message);

            _vm.ErrorMessage = $"生成方案时出错：{ex.Message}";
            MessageBox.Show(
                $"生成方案失败：\n\n{ex.Message}\n\n类型：{ex.GetType().FullName}\n\n堆栈：\n{ex.StackTrace}",
                "MixCut", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---- 策略列表 ----

    private void RefreshStrategyList()
    {
        StrategyList.Children.Clear();

        var totalSchemes = _vm.Schemes.Count;
        if (totalSchemes > 0)
        {
            CountBadge.Visibility = Visibility.Visible;
            CountBadgeText.Text = $"{_vm.Strategies.Count} 策略 · {totalSchemes} 视频";
        }
        else
        {
            CountBadge.Visibility = Visibility.Collapsed;
        }

        if (_vm.Strategies.Count == 0)
        {
            // 生成中：显示 AI 占位，对齐 Mac SchemeListView 生成中全屏占位
            if (_vm.IsGenerating)
            {
                StrategyList.Children.Add(BuildGeneratingPlaceholder());
            }
            else
            {
                StrategyList.Children.Add(BuildEmptyState());
            }
            return;
        }

        foreach (var strategy in _vm.Strategies)
        {
            StrategyList.Children.Add(BuildStrategySection(strategy));
        }
    }

    /// <summary>方案生成中的占位（带 spinner + 文案 + 进度）。对齐 Mac SchemeListView 占位。</summary>
    private UIElement BuildGeneratingPlaceholder()
    {
        var sp = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 80, 0, 0),
        };
        var spinner = new ProgressBar
        {
            IsIndeterminate = true,
            Height = 4,
            Width = 200,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x6B, 0xE5)),
            Background = new SolidColorBrush(Color.FromRgb(0xE7, 0xF0, 0xFF)),
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 16),
        };
        sp.Children.Add(spinner);
        sp.Children.Add(new TextBlock
        {
            Text = "✨ AI 正在生成方案",
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x6B, 0xE5)),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        sp.Children.Add(new TextBlock
        {
            Text = "正在生成差异化策略和排列组合...",
            FontSize = 11, Foreground = Brushes.Gray,
            Margin = new Thickness(0, 4, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        // 显示具体阶段（来自 VM.GenerationProgress）
        if (!string.IsNullOrEmpty(_vm.GenerationProgress))
        {
            sp.Children.Add(new TextBlock
            {
                Text = _vm.GenerationProgress,
                FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
        return sp;
    }

    private UIElement BuildEmptyState()
    {
        var sp = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 60, 0, 0),
        };
        sp.Children.Add(new TextBlock
        {
            Text = "📋", FontSize = 36, Foreground = Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12),
        });
        sp.Children.Add(new TextBlock
        {
            Text = "暂无混剪方案", FontSize = 14, FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        sp.Children.Add(new TextBlock
        {
            Text = "点击「生成」让 AI 批量创建混剪方案",
            FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        if (_project is { SegmentCount: 0 })
        {
            var hint = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x18, 0xC0, 0x6F, 0x00)),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 12, 0, 0),
                Child = new TextBlock
                {
                    Text = "⚠ 需要先导入视频并完成分析",
                    FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x6F, 0x00)),
                },
            };
            sp.Children.Add(hint);
        }
        else if (_project is { } pj)
        {
            // 有分镜时给积极反馈，告知可生成方案。
            var hint = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x18, 0x1D, 0x6B, 0xE5)),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 12, 0, 0),
                Child = new TextBlock
                {
                    Text = $"✓ {pj.VideoCount} 个视频, {pj.SegmentCount} 个分镜可用",
                    FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x6B, 0xE5)),
                },
            };
            sp.Children.Add(hint);
        }
        return sp;
    }

    private UIElement BuildStrategySection(MixStrategy strategy)
    {
        var sp = new StackPanel();
        var isExpanded = _expandedStrategies.Contains(strategy.Id);

        // 头部
        var headerBg = isExpanded
            ? new SolidColorBrush(Color.FromRgb(0xEC, 0xF2, 0xFE))
            : Brushes.Transparent;
        var header = new Border
        {
            Background = headerBg, Padding = new Thickness(14, 10, 14, 10),
            Cursor = Cursors.Hand, Tag = strategy,
        };
        header.MouseLeftButtonDown += OnStrategyHeaderClick;

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var chevron = new TextBlock
        {
            Text = isExpanded ? "▾" : "▸", FontSize = 11, Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Width = 12,
        };
        Grid.SetColumn(chevron, 0);
        headerGrid.Children.Add(chevron);

        var info = new StackPanel();
        info.Children.Add(new TextBlock
        {
            Text = strategy.Name, FontSize = 12, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        info.Children.Add(new TextBlock
        {
            Text = $"🎨 {strategy.Style}   👥 {strategy.TargetAudience}",
            FontSize = 10, Foreground = Brushes.Gray, Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(info, 1);
        headerGrid.Children.Add(info);

        var count = new TextBlock
        {
            Text = $"{strategy.SchemeCount} 个视频",
            FontSize = 10, Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(count, 2);
        headerGrid.Children.Add(count);

        header.Child = headerGrid;
        header.ContextMenu = BuildStrategyContextMenu(strategy);
        sp.Children.Add(header);

        // 变体列表
        if (isExpanded)
        {
            foreach (var scheme in strategy.OrderedSchemes)
            {
                sp.Children.Add(BuildSchemeRow(scheme));
            }
        }

        sp.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)) });
        return sp;
    }

    private ContextMenu BuildStrategyContextMenu(MixStrategy strategy)
    {
        var menu = new ContextMenu();
        var deleteItem = new MenuItem { Header = "删除策略", Tag = strategy };
        deleteItem.Click += (_, _) =>
        {
            var confirm = MessageBox.Show(
                $"删除策略「{strategy.Name}」及其全部 {strategy.SchemeCount} 个变体？",
                "确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm == MessageBoxResult.OK)
            {
                _vm.DeleteStrategy(strategy);
                RefreshStrategyList();
                if (_vm.SelectedScheme is null)
                {
                    RefreshDetail();
                }
            }
        };
        menu.Items.Add(deleteItem);
        return menu;
    }

    private UIElement BuildSchemeRow(MixScheme scheme)
    {
        var isSelected = _vm.SelectedScheme?.Id == scheme.Id;
        var border = new Border
        {
            Background = isSelected
                ? new SolidColorBrush(Color.FromArgb(0x20, 0x1D, 0x6B, 0xE5))
                : Brushes.Transparent,
            Padding = new Thickness(28, 7, 14, 7),
            Cursor = Cursors.Hand,
            Tag = scheme,
        };
        border.MouseLeftButtonDown += OnSchemeRowClick;
        border.ContextMenu = BuildSchemeContextMenu(scheme);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var idx = new TextBlock
        {
            Text = "#" + scheme.VariationIndex,
            FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(idx, 0);
        grid.Children.Add(idx);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = scheme.Name, FontSize = 11, FontWeight = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        info.Children.Add(new TextBlock
        {
            Text = $"{scheme.SegmentCount} 分镜 · " +
                   $"{scheme.EstimatedDuration.ToString("F0", CultureInfo.InvariantCulture)}s",
            FontSize = 10, Foreground = Brushes.Gray, Margin = new Thickness(0, 2, 0, 0),
        });
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        border.Child = grid;
        return border;
    }

    private ContextMenu BuildSchemeContextMenu(MixScheme scheme)
    {
        var menu = new ContextMenu();

        // 复制方案名（对齐 Mac SchemeListView contextMenu）
        var copyItem = new MenuItem { Header = "📋 复制方案名" };
        copyItem.Click += (_, _) =>
        {
            try
            {
                System.Windows.Clipboard.SetText(scheme.Name ?? string.Empty);
                Components.ToastService.Show("方案名已复制", Components.ToastStyle.Success);
            }
            catch { /* ignore */ }
        };
        menu.Items.Add(copyItem);
        menu.Items.Add(new Separator());

        var deleteItem = new MenuItem { Header = "🗑 删除变体", Tag = scheme };
        deleteItem.Click += (_, _) =>
        {
            var confirm = MessageBox.Show($"删除变体「{scheme.Name}」？",
                "确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm == MessageBoxResult.OK)
            {
                _vm.DeleteScheme(scheme);
                RefreshStrategyList();
                if (_vm.SelectedScheme is null)
                {
                    RefreshDetail();
                }
                Components.ToastService.Show($"已删除变体「{scheme.Name}」", Components.ToastStyle.Warning);
            }
        };
        menu.Items.Add(deleteItem);
        return menu;
    }

    private void OnStrategyHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: MixStrategy strategy })
        {
            if (!_expandedStrategies.Add(strategy.Id))
            {
                _expandedStrategies.Remove(strategy.Id);
            }
            _vm.SelectedStrategy = strategy;
            RefreshStrategyList();
        }
    }

    private void OnSchemeRowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: MixScheme scheme })
        {
            _vm.SelectedScheme = scheme;
            RefreshStrategyList();
            RefreshDetail();
        }
    }

    // ---- 详情面板 ----

    private void RefreshDetail()
    {
        var scheme = _vm.SelectedScheme;
        if (scheme is null)
        {
            EmptyDetail.Visibility = Visibility.Visible;
            DetailScroller.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyDetail.Visibility = Visibility.Collapsed;
        DetailScroller.Visibility = Visibility.Visible;
        DetailPanel.Children.Clear();

        // 头部：标题 + 复制按钮 + 总时长（对齐 Mac SchemeDetailView.schemeHeader + "复制方案名"）
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameText = new TextBlock
        {
            Text = scheme.Name, FontSize = 20, FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(nameText, 0);
        headerGrid.Children.Add(nameText);

        // 复制方案名按钮（对齐 Mac retryASR 行的复制方案名入口）
        var copyNameBtn = new Button
        {
            Content = "📋",
            ToolTip = "复制方案名",
            Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF2)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(12, 0, 8, 0),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        copyNameBtn.Click += (_, _) =>
        {
            try
            {
                System.Windows.Clipboard.SetText(scheme.Name ?? string.Empty);
                copyNameBtn.Content = "✓";
                Components.ToastService.Show("方案名已复制", Components.ToastStyle.Success);
                // 短暂闪一下后恢复
                var revertTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                revertTimer.Tick += (_, _) => { copyNameBtn.Content = "📋"; revertTimer.Stop(); };
                revertTimer.Start();
            }
            catch { /* ignore */ }
        };
        Grid.SetColumn(copyNameBtn, 1);
        headerGrid.Children.Add(copyNameBtn);

        var durationStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        durationStack.Children.Add(new TextBlock
        {
            Text = scheme.EstimatedDuration.ToString("F1", CultureInfo.InvariantCulture),
            FontSize = 24, Foreground = Brushes.Gray, FontWeight = FontWeights.Light,
        });
        durationStack.Children.Add(new TextBlock
        {
            Text = "s", FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontWeight = FontWeights.Light, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(2, 0, 0, 3),
        });
        Grid.SetColumn(durationStack, 2);
        headerGrid.Children.Add(durationStack);
        DetailPanel.Children.Add(headerGrid);

        // 方案描述（对齐 Mac scheme.schemeDescription）
        if (!string.IsNullOrEmpty(scheme.SchemeDescription))
        {
            DetailPanel.Children.Add(new TextBlock
            {
                Text = scheme.SchemeDescription, FontSize = 13, Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap, LineHeight = 18, Margin = new Thickness(0, 8, 0, 0),
            });
        }

        // 元信息
        var meta = new WrapPanel { Margin = new Thickness(0, 12, 0, 14) };
        meta.Children.Add(MakeMetaPill("🎨 " + scheme.Style));
        meta.Children.Add(MakeMetaPill("👥 " + scheme.TargetAudience));
        meta.Children.Add(MakeMetaPill($"🎞 {scheme.SegmentCount} 分镜"));
        DetailPanel.Children.Add(meta);

        // 叙事结构文字
        if (!string.IsNullOrEmpty(scheme.NarrativeStructure))
        {
            DetailPanel.Children.Add(new TextBlock
            {
                Text = "叙事结构", FontSize = 12, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 4),
            });
            DetailPanel.Children.Add(new TextBlock
            {
                Text = scheme.NarrativeStructure, FontSize = 12, Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            });
        }

        // 叙事结构可视化条（按时长比例的彩色块）
        var orderedForBar = scheme.OrderedSegments.Where(ss => ss.Segment is not null).ToList();
        if (orderedForBar.Count > 0)
        {
            var barGrid = new Grid { Height = 26, Margin = new Thickness(0, 0, 0, 6) };
            foreach (var ss in orderedForBar)
            {
                var duration = Math.Max(0.5, ss.Segment!.Duration);
                barGrid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(duration, GridUnitType.Star),
                });
            }
            for (var i = 0; i < orderedForBar.Count; i++)
            {
                var seg = orderedForBar[i].Segment!;
                var color = (Color)ColorConverter.ConvertFromString(seg.PrimarySemanticType.ToColorHex());
                var block = new Border
                {
                    Background = new SolidColorBrush(color),
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(i == 0 ? 0 : 1, 0, i == orderedForBar.Count - 1 ? 0 : 1, 0),
                    ToolTip = $"{string.Join("/", seg.SemanticTypes.Select(t => t.ToLabel()))}" +
                              $" · {seg.Duration.ToString("F1", CultureInfo.InvariantCulture)}s",
                    Child = new TextBlock
                    {
                        Text = seg.PrimarySemanticType.ToLabel(),
                        Foreground = Brushes.White, FontSize = 9, FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                };
                Grid.SetColumn(block, i);
                barGrid.Children.Add(block);
            }
            DetailPanel.Children.Add(barGrid);

            // 图例
            var legendUsed = orderedForBar
                .Select(ss => ss.Segment!.PrimarySemanticType)
                .Distinct()
                .ToList();
            var legend = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
            foreach (var type in legendUsed)
            {
                var color = (Color)ColorConverter.ConvertFromString(type.ToColorHex());
                var pill = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 10, 0),
                };
                pill.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 7, Height = 7, Fill = new SolidColorBrush(color),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                pill.Children.Add(new TextBlock
                {
                    Text = "  " + type.ToLabel(), FontSize = 10, Foreground = Brushes.Gray,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                legend.Children.Add(pill);
            }
            DetailPanel.Children.Add(legend);
        }

        // 分镜序列（横向滚动 Storyboard，对齐 Mac 版）
        DetailPanel.Children.Add(new TextBlock
        {
            Text = "分镜序列（按播放顺序）", FontSize = 12, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 6, 0, 8),
        });

        var storyboardScroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 0, 10),
        };
        var storyboardPanel = new StackPanel { Orientation = Orientation.Horizontal };
        var ordered = scheme.OrderedSegments;
        var pos = 1;
        foreach (var schemeSeg in ordered)
        {
            storyboardPanel.Children.Add(BuildStoryboardCard(pos, schemeSeg));
            pos++;
        }
        storyboardScroller.Content = storyboardPanel;
        DetailPanel.Children.Add(storyboardScroller);

        if (!string.IsNullOrEmpty(scheme.StrategyReasoning))
        {
            DetailPanel.Children.Add(BuildInfoSection("💡 策略说明", scheme.StrategyReasoning));
        }
        if (!string.IsNullOrEmpty(scheme.Differentiation))
        {
            DetailPanel.Children.Add(BuildInfoSection("🔀 差异化", scheme.Differentiation));
        }
    }

    private static UIElement BuildInfoSection(string title, string text)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF9)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 12, 0, 0),
        };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = title, FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            Margin = new Thickness(0, 0, 0, 6),
        });
        sp.Children.Add(new TextBlock
        {
            Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
        });
        border.Child = sp;
        return border;
    }

    private static UIElement MakeMetaPill(string text)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF4)),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 6, 6),
            Child = new TextBlock
            {
                Text = text, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            },
        };
    }

    /// <summary>横向 Storyboard 卡片：缩略图 + 序号 + 类型 + 台词 + 右键移除。对齐 Mac 版 StoryboardCard。</summary>
    private UIElement BuildStoryboardCard(int position, SchemeSegment schemeSeg)
    {
        const double cardWidth = 156;
        var seg = schemeSeg.Segment;

        var border = new Border
        {
            Width = cardWidth,
            Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFC)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xE7)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 8, 0),
        };

        if (seg is null)
        {
            border.Background = new SolidColorBrush(Color.FromRgb(0xFD, 0xE2, 0xE2));
            var miss = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 30, 0, 30),
            };
            miss.Children.Add(new TextBlock
            {
                Text = "#" + position, FontSize = 11, FontWeight = FontWeights.Bold,
                Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center,
            });
            miss.Children.Add(new TextBlock
            {
                Text = "⚠ 分镜数据缺失",
                FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x6F, 0x00)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0),
            });
            border.Child = miss;
            return border;
        }

        var aspect = seg.Video is { Width: > 0, Height: > 0 } v
            ? (double)v.Width / v.Height
            : 9.0 / 16.0;
        const double thumbMaxHeight = 240;
        var thumbHeight = Math.Min(thumbMaxHeight, cardWidth / aspect);

        var sp = new StackPanel();

        // 缩略图 + 序号叠加
        var thumbBorder = new Border
        {
            Width = cardWidth, Height = thumbHeight,
            Background = Brushes.Black,
            CornerRadius = new CornerRadius(8, 8, 0, 0),
            ClipToBounds = true,
        };
        var thumbGrid = new Grid();
        if (seg.Video is { LocalPath: var path } && File.Exists(path))
        {
            var player = new Components.InlineVideoPlayer();
            player.SetSegment(path, seg.ThumbnailPath, seg.StartTime, seg.EndTime);
            thumbGrid.Children.Add(player);
        }
        else if (LoadThumbStatic(seg.ThumbnailPath) is { } img)
        {
            thumbGrid.Children.Add(new Image
            {
                Source = img, Stretch = System.Windows.Media.Stretch.UniformToFill,
            });
        }
        // 序号角标
        thumbGrid.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(4),
            Child = new TextBlock
            {
                Text = "#" + position, Foreground = Brushes.White,
                FontSize = 10, FontWeight = FontWeights.Bold,
            },
        });
        thumbBorder.Child = thumbGrid;
        sp.Children.Add(thumbBorder);

        // 信息区
        var info = new StackPanel { Margin = new Thickness(8, 6, 8, 8) };

        // 类型标签
        var badges = new WrapPanel();
        foreach (var t in seg.SemanticTypes.Take(2))
        {
            var color = (Color)ColorConverter.ConvertFromString(t.ToColorHex());
            badges.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x28, color.R, color.G, color.B)),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(0, 0, 3, 3),
                Child = new TextBlock
                {
                    Text = t.ToLabel(), FontSize = 9, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(color),
                },
            });
        }
        if (seg.SemanticTypes.Count > 2)
        {
            badges.Children.Add(new TextBlock
            {
                Text = $"+{seg.SemanticTypes.Count - 2}",
                FontSize = 9, Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        info.Children.Add(badges);

        // 台词（2 行截断）
        info.Children.Add(new TextBlock
        {
            Text = seg.Text, FontSize = 10, Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 32, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 4, 0, 0),
            ToolTip = seg.Text,
        });

        // 时长 + 时间区间
        info.Children.Add(new TextBlock
        {
            Text = $"⏱ {seg.Duration.ToString("F1", CultureInfo.InvariantCulture)}s · " +
                   $"{seg.StartTime.ToString("F1", CultureInfo.InvariantCulture)}-" +
                   $"{seg.EndTime.ToString("F1", CultureInfo.InvariantCulture)}s",
            FontSize = 9, Foreground = Brushes.Gray, Margin = new Thickness(0, 6, 0, 0),
        });

        // 紧凑时间调整行（IN ± / OUT ±）—— 对齐 Mac StoryboardTimeRow。
        // 仅在能访问 SegmentLibraryVM 时显示。
        if (_segmentVm is not null)
        {
            info.Children.Add(BuildMiniTimeRow(seg));
        }

        sp.Children.Add(info);

        border.Child = sp;

        // 右键菜单：从方案中移除（对齐 Mac StoryboardCard.contextMenu）
        var menu = new ContextMenu();
        var removeItem = new MenuItem { Header = "从方案中移除" };
        removeItem.Click += (_, _) =>
        {
            if (_vm.SelectedScheme is { } scheme)
            {
                if (!_vm.RemoveSegment(schemeSeg, scheme))
                {
                    ToastCenter.Shared.Show("方案至少保留 1 个分镜", ToastStyle.Warning);
                    return;
                }
                RefreshDetail();
            }
        };
        menu.Items.Add(removeItem);
        border.ContextMenu = menu;

        return border;
    }

    /// <summary>
    /// Storyboard 卡片内的紧凑时间调整行（±0.1s）。对齐 Mac StoryboardTimeRow。
    /// </summary>
    private UIElement BuildMiniTimeRow(Segment seg)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        Button MiniBtn(string content)
        {
            return new Button
            {
                Content = content, Width = 16, Height = 16, Padding = new Thickness(0),
                FontSize = 8, FontWeight = FontWeights.Bold,
                Background = new SolidColorBrush(Color.FromArgb(0x10, 0, 0, 0)),
                BorderThickness = new Thickness(0), Foreground = Brushes.Gray,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
        }
        TextBlock TimeText(double sec) => new()
        {
            Text = sec.ToString("F1", CultureInfo.InvariantCulture),
            FontSize = 9, Foreground = Brushes.Gray,
            FontFamily = new System.Windows.Media.FontFamily("Consolas, monospace"),
            Width = 28, TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var inMinus = MiniBtn("−");
        inMinus.Click += (_, _) => { _segmentVm!.AdjustStartTime(seg, -0.1); RefreshDetail(); };
        var inText = TimeText(seg.StartTime);
        var inPlus = MiniBtn("＋");
        inPlus.Click += (_, _) => { _segmentVm!.AdjustStartTime(seg, 0.1); RefreshDetail(); };

        var sep = new TextBlock
        {
            Text = "–", FontSize = 8, Foreground = Brushes.LightGray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 2, 0),
        };

        var outMinus = MiniBtn("−");
        outMinus.Click += (_, _) => { _segmentVm!.AdjustEndTime(seg, -0.1); RefreshDetail(); };
        var outText = TimeText(seg.EndTime);
        var outPlus = MiniBtn("＋");
        outPlus.Click += (_, _) => { _segmentVm!.AdjustEndTime(seg, 0.1); RefreshDetail(); };

        row.Children.Add(inMinus);
        row.Children.Add(inText);
        row.Children.Add(inPlus);
        row.Children.Add(sep);
        row.Children.Add(outMinus);
        row.Children.Add(outText);
        row.Children.Add(outPlus);

        return row;
    }

    private static System.Windows.Media.ImageSource? LoadThumbStatic(string? path) =>
        Infrastructure.ThumbnailCache.Shared.GetImage(path);
}
