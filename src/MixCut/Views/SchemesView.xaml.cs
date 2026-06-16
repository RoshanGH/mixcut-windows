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
    private System.Threading.CancellationTokenSource? _generateCts;

    // Phase 4b 场景 B drawer 状态
    private System.Collections.ObjectModel.ObservableCollection<ViewModels.Cards.SegmentPickerItem>? _pickerItems;
    private DrawerIntent _drawerIntent = DrawerIntent.None;
    private int _drawerInsertPosition;
    private SchemeSegment? _drawerReplaceTarget;

    private enum DrawerIntent
    {
        None,
        Insert,  // _drawerInsertPosition 有效
        Replace, // _drawerReplaceTarget 有效
    }

    public SchemesView(SchemeViewModel vm)
        : this(vm, null) { }

    public SchemesView(SchemeViewModel vm, SegmentLibraryViewModel? segmentVm)
    {
        _vm = vm;
        _segmentVm = segmentVm;
        InitializeComponent();
        _vm.PropertyChanged += OnVmChanged;

        // Phase 4b：分镜选择抽屉事件 + 数据源
        PickerDrawer.SegmentPicked += OnDrawerSegmentPicked;
        PickerDrawer.CloseRequested += OnDrawerCloseRequested;
        _pickerItems = new System.Collections.ObjectModel.ObservableCollection<ViewModels.Cards.SegmentPickerItem>();
        PickerDrawer.Items = _pickerItems;
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

    /// <summary>
    /// 从外部（如分镜库「✨ 组合为方案」流程）跳转到本视图并选中指定方案。
    /// 对齐 Mac NavigationCoordinator.navigateToSchemes(selecting:).
    /// </summary>
    public void SelectScheme(MixScheme scheme)
    {
        _vm.SelectedStrategy = scheme.Strategy ?? _vm.Strategies.FirstOrDefault(s => s.Id == scheme.StrategyId);
        _vm.SelectedScheme = scheme;

        // 确保该方案所在策略是展开状态，否则详情面板看不出在哪个策略下
        if (_vm.SelectedStrategy is { } strat)
        {
            _expandedStrategies.Add(strat.Id);
        }

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

    private void OnCancelGenerate(object sender, RoutedEventArgs e)
    {
        _generateCts?.Cancel();
        if (ProgressText is not null) { ProgressText.Text = "正在取消…"; }
    }

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

            // P0-2：传入取消令牌，让生成进度 banner 上的「取消」按钮真正生效（后端已全透传 token，
            // 此前 View 没传 → 取消回滚逻辑是死代码）。取消时 GenerateSchemesAsync 内部回滚不抛，
            // 下面刷新照常。
            _generateCts?.Dispose();
            _generateCts = new System.Threading.CancellationTokenSource();
            try
            {
                await _vm.GenerateSchemesAsync(
                    _project, dialog.TargetVideoCount, dialog.CustomPrompt, _generateCts.Token);
            }
            finally
            {
                _generateCts.Dispose();
                _generateCts = null;
            }

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

            var friendly = TranslateGenerateError(ex);
            _vm.ErrorMessage = $"生成方案时出错：{friendly}";
            // QW-1：不再把 C# stack trace / 异常类型 / 命名空间直接弹给用户（这是全项目唯一一处泄漏）。
            // 翻译成人话 + 给出可操作的下一步；完整 stack 已由上面的 Serilog.Log.Error 写盘。
            MessageBox.Show(
                $"生成方案失败。\n\n{friendly}\n\n建议操作：\n" +
                "• 检查「设置 → API」中的 Key 是否有效\n" +
                "• 检查网络连接是否正常\n" +
                "• 如多次失败，请联系开发者并附上日志",
                "方案生成失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>把方案生成异常翻译成用户能看懂的人话（QW-1）。完整堆栈仍写日志，不弹给用户。</summary>
    private static string TranslateGenerateError(Exception ex)
    {
        var msg = ex.Message ?? string.Empty;
        if (msg.Contains("401") || msg.Contains("Unauthorized")) return "API Key 无效或已过期，请到「设置 → API」检查。";
        if (msg.Contains("403") || msg.Contains("Forbidden")) return "API 访问被拒绝，请确认 Key 权限或额度是否充足。";
        if (msg.Contains("429")) return "请求过于频繁，请稍后再试。";
        if (ex is TaskCanceledException || msg.Contains("timeout", StringComparison.OrdinalIgnoreCase) || msg.Contains("超时"))
            return "请求超时，请检查网络连接后重试。";
        if (ex is System.Net.Http.HttpRequestException) return "网络请求失败，请检查网络连接。";
        return string.IsNullOrWhiteSpace(msg) ? "发生未知错误。" : msg;
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

        foreach (var strategy in _vm.OrderedStrategiesForDisplay)
        {
            StrategyList.Children.Add(BuildStrategySection(strategy));
        }

        // issue #6：「＋ 添加结构」入口（自定义叙事结构）
        StrategyList.Children.Add(BuildAddNarrativeStructureEntry());
    }

    /// <summary>「＋ 添加结构」入口：打开叙事结构编辑器（issue #6）。</summary>
    private UIElement BuildAddNarrativeStructureEntry()
    {
        var btn = new Button
        {
            Content = "＋ 添加结构（自定义叙事结构）",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(14, 8, 14, 12),
            Padding = new Thickness(12, 8, 12, 8),
            FontSize = 12,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1D, 0x6B, 0xE5)),
            BorderThickness = new Thickness(1),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1D, 0x6B, 0xE5)),
            Cursor = Cursors.Hand,
        };
        btn.Click += OnAddNarrativeStructure;
        return btn;
    }

    private void OnAddNarrativeStructure(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_project is null)
            {
                return;
            }
            var segments = _vm.LoadProjectSegments(_project);
            if (segments.Count == 0)
            {
                Components.ToastService.Show("请先在分镜素材库分析视频，生成带标签的分镜", Components.ToastStyle.Info);
                return;
            }
            var win = new Schemes.NarrativeStructureEditorWindow(_vm, _project, segments)
            {
                Owner = Window.GetWindow(this),
            };
            var ok = win.ShowDialog();
            if (ok == true && win.CreatedStructure is not null)
            {
                _expandedStrategies.Add(win.CreatedStructure.Id);
                RefreshStrategyList();
                Components.ToastService.Show($"已生成结构「{win.CreatedStructure.NarrativeDisplayName}」", Components.ToastStyle.Success);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[NarrativeGen] 打开编辑器失败");
            Components.ToastService.Show("打开叙事结构编辑器失败", Components.ToastStyle.Error);
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
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        if (strategy.IsCustomGroup || strategy.IsNarrativeTemplate)
        {
            nameRow.Children.Add(new TextBlock
            {
                Text = strategy.IsNarrativeTemplate ? "📐" : "✨",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            });
        }
        // 叙事结构：名字即段位序列（NarrativeDisplayName）；其它策略用 Name
        nameRow.Children.Add(new TextBlock
        {
            Text = strategy.IsNarrativeTemplate ? strategy.NarrativeDisplayName : strategy.Name,
            FontSize = 12, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        info.Children.Add(nameRow);
        var subtitle = strategy.IsNarrativeTemplate
            ? $"📐 自定义叙事结构 · {strategy.NarrativeSlots.Count} 段"
            : $"🎨 {strategy.Style}   👥 {strategy.TargetAudience}";
        info.Children.Add(new TextBlock
        {
            Text = subtitle,
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
            // 自定义组合策略空状态引导（v0.3.0 对齐）
            if (strategy.IsCustomGroup && strategy.SchemeCount == 0)
            {
                sp.Children.Add(BuildCustomGroupEmptyHint());
            }
        }

        sp.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)) });
        return sp;
    }

    /// <summary>自定义组合策略空状态引导卡（v0.3.0 对齐 Mac SchemeListView empty hint）。</summary>
    private UIElement BuildCustomGroupEmptyHint()
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0x7C, 0x3A, 0xED)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(28, 6, 14, 8),
        };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = "还没有自定义组合",
            FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED)),
        });
        sp.Children.Add(new TextBlock
        {
            Text = "去分镜素材库挑几个分镜，按勾选顺序自动生成方案",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            Margin = new Thickness(0, 4, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        });
        var gotoBtn = new Button
        {
            Content = "前往分镜素材库",
            Background = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 5, 10, 5),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = Cursors.Hand,
        };
        gotoBtn.Click += (_, _) =>
        {
            var mainWin = Window.GetWindow(this) as MainWindow;
            mainWin?.NavigateTo(NavigationItem.SegmentLibrary);
        };
        sp.Children.Add(gotoBtn);
        border.Child = sp;
        return border;
    }

    private ContextMenu BuildStrategyContextMenu(MixStrategy strategy)
    {
        var menu = new ContextMenu();
        var deleteItem = new MenuItem { Header = "删除策略", Tag = strategy };
        if (strategy.IsCustomGroup)
        {
            // 自定义组合是系统级容器策略，不允许用户删除/重命名
            deleteItem.IsEnabled = false;
            deleteItem.ToolTip = "「自定义组合」是系统策略，不可删除";
            ToolTipService.SetShowOnDisabled(deleteItem, true);
        }
        else
        {
            deleteItem.Click += (_, _) =>
            {
                var confirm = MessageBox.Show(
                    $"删除策略「{strategy.Name}」及其全部 {strategy.SchemeCount} 个变体？删除后可按 Ctrl+Z 撤销。",
                    "确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.OK) return;
                var snapshot = _vm.DeleteStrategy(strategy);
                RefreshStrategyList();
                if (_vm.SelectedScheme is null)
                {
                    RefreshDetail();
                }
                if (snapshot is not null)
                {
                    // P0-10：压入撤销栈，Ctrl+Z 恢复整组策略（含全部变体 + 方案分镜）。
                    Infrastructure.UndoStack.UndoManager.Shared.Push(
                        new Infrastructure.UndoStack.DelegateUndoAction(
                            $"删除策略「{snapshot.Name}」",
                            () =>
                            {
                                if (_vm.RestoreStrategy(snapshot))
                                {
                                    RefreshStrategyList();
                                    RefreshDetail();
                                    Components.ToastService.Show("已恢复策略", Components.ToastStyle.Success);
                                }
                                else
                                {
                                    Components.ToastService.Show("恢复失败，请重试", Components.ToastStyle.Error);
                                }
                            }));
                    Components.ToastService.Show("已删除策略", Components.ToastStyle.Warning,
                        "撤销", () => Infrastructure.UndoStack.UndoManager.Shared.Undo());
                }
            };
        }
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
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(new TextBlock
        {
            Text = scheme.Name, FontSize = 11, FontWeight = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (scheme.IsManuallyEdited)
        {
            nameRow.Children.Add(new TextBlock
            {
                Text = "·已修改",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
            });
        }
        info.Children.Add(nameRow);
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
            catch (Exception clipEx)
            {
                // QW-8：剪贴板被其他程序独占时 SetText 会抛 —— 记 Warning 便于诊断，不再静默吞。
                Serilog.Log.Warning(clipEx, "[SchemesView] 复制方案名到剪贴板失败（右键菜单）");
            }
        };
        menu.Items.Add(copyItem);
        menu.Items.Add(new Separator());

        var deleteItem = new MenuItem { Header = "🗑 删除变体", Tag = scheme };
        deleteItem.Click += (_, _) =>
        {
            var confirm = MessageBox.Show($"删除变体「{scheme.Name}」？删除后可按 Ctrl+Z 撤销。",
                "确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.OK) return;
            var snapshot = _vm.DeleteScheme(scheme);
            RefreshStrategyList();
            if (_vm.SelectedScheme is null)
            {
                RefreshDetail();
            }
            if (snapshot is not null)
            {
                // P0-10：压入撤销栈，Ctrl+Z 恢复该变体（含方案分镜）。
                Infrastructure.UndoStack.UndoManager.Shared.Push(
                    new Infrastructure.UndoStack.DelegateUndoAction(
                        $"删除变体「{snapshot.Name}」",
                        () =>
                        {
                            if (_vm.RestoreScheme(snapshot))
                            {
                                RefreshStrategyList();
                                RefreshDetail();
                                Components.ToastService.Show("已恢复变体", Components.ToastStyle.Success);
                            }
                            else
                            {
                                Components.ToastService.Show("恢复失败，请重试", Components.ToastStyle.Error);
                            }
                        }));
            }
            Components.ToastService.Show($"已删除变体「{scheme.Name}」", Components.ToastStyle.Warning,
                "撤销", () => Infrastructure.UndoStack.UndoManager.Shared.Undo());
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
            catch (Exception clipEx)
            {
                // QW-8：剪贴板被其他程序独占时 SetText 会抛 —— 记 Warning 便于诊断，不再静默吞。
                Serilog.Log.Warning(clipEx, "[SchemesView] 复制方案名到剪贴板失败（详情面板）");
            }
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
            // Phase 4b：每个分镜前插一个行间 ⊕（hover 显示，插入位置 = 当前分镜的 position）
            storyboardPanel.Children.Add(BuildInsertGapAt(pos));
            storyboardPanel.Children.Add(BuildStoryboardCard(pos, schemeSeg));
            pos++;
        }
        // 尾部追加位置（position = count + 1）
        storyboardPanel.Children.Add(BuildInsertGapAt(pos));

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

    /// <summary>
    /// 在 storyboard 横向序列中插入一个行间 ⊕ 按钮。
    /// hover 时 12→28px 浮出（CSS-like），点击弹出右侧抽屉以「在 position 处插入分镜」意图。
    /// </summary>
    private UIElement BuildInsertGapAt(int position)
    {
        var btn = new Schemes.InsertGapButton
        {
            InsertPosition = position,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        btn.Clicked += OnInsertGapClicked;
        return btn;
    }

    private void OnInsertGapClicked(object? sender, int position)
    {
        if (_vm.SelectedScheme is not { } scheme || _project is null)
        {
            return;
        }
        OpenPickerForInsert(scheme, position);
    }

    private static Button MakeCircleButton(string content, string tooltip)
    {
        return new Button
        {
            Content = content,
            Width = 28, Height = 28,
            Background = new SolidColorBrush(Color.FromArgb(0xA6, 0, 0, 0)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 0, 4, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = tooltip,
            FontSize = 12,
        };
    }

    /// <summary>横向 Storyboard 卡片：缩略图 + 序号 + 类型 + 台词 + 右键移除。对齐 Mac 版 StoryboardCard。</summary>
    private UIElement BuildStoryboardCard(int position, SchemeSegment schemeSeg)
    {
        const double cardWidth = 196;
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

        // v0.3.0 对齐 Mac：所有视频显示容器统一 9:16 手机端竖屏比例（信息流广告投放规格）。
        // 不再用视频原始比例 —— 视频原本是横屏的会被 UniformToFill 裁剪填充，符合广告投放规范。
        const double thumbAspect = 9.0 / 16.0;
        var thumbHeight = cardWidth / thumbAspect;

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
            player.SetSegment(path, seg.ThumbnailPath, seg.StartFrame, seg.EndFrame, seg.EffectiveFps);
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

        // 时长；具体 IN/OUT 时间码放在下方帧边界面板，避免一行塞满信息。
        info.Children.Add(new TextBlock
        {
            Text = $"{seg.Duration.ToString("F1", CultureInfo.InvariantCulture)} 秒",
            FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x4A)),
            Margin = new Thickness(0, 7, 0, 0),
            ToolTip = $"实际最后一帧：{seg.LastFrameTimecode}",
        });

        // 紧凑时间调整行（IN ± / OUT ±）—— 对齐 Mac StoryboardTimeRow。
        // 仅在能访问 SegmentLibraryVM 时显示。
        if (_segmentVm is not null)
        {
            info.Children.Add(BuildMiniTimeRow(seg));
        }

        sp.Children.Add(info);

        border.Child = sp;

        // Phase 4b：hover 浮出的替换/删除按钮（右上角圆形 28x28）
        // 叠加到 thumbGrid（thumb 区域）— thumbBorder.Child 已是 thumbGrid，仍可 Children.Add
        var hoverButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 6, 0),
            Visibility = Visibility.Collapsed,
        };
        var replaceBtn = MakeCircleButton("🔁", "替换为其它分镜");
        replaceBtn.Click += (_, _) =>
        {
            if (_vm.SelectedScheme is { } sch)
            {
                OpenPickerForReplace(sch, schemeSeg);
            }
        };
        var deleteBtn = MakeCircleButton("🗑", "从方案中删除");
        deleteBtn.Click += (_, _) =>
        {
            if (_vm.SelectedScheme is { } sch)
            {
                if (!_vm.RemoveSegment(schemeSeg, sch))
                {
                    ToastCenter.Shared.Show("方案至少保留 1 个分镜", ToastStyle.Warning);
                    return;
                }
                RefreshDetail();
            }
        };
        hoverButtons.Children.Add(replaceBtn);
        hoverButtons.Children.Add(deleteBtn);
        thumbGrid.Children.Add(hoverButtons);

        // hover 显示/隐藏（覆盖整个卡片范围）
        border.MouseEnter += (_, _) => hoverButtons.Visibility = Visibility.Visible;
        border.MouseLeave += (_, _) => hoverButtons.Visibility = Visibility.Collapsed;

        // 右键菜单：替换为... + 从方案中移除（对齐 Mac StoryboardCard.contextMenu）
        var menu = new ContextMenu();
        var replaceMenuItem = new MenuItem { Header = "替换为..." };
        replaceMenuItem.Click += (_, _) =>
        {
            if (_vm.SelectedScheme is { } sch)
            {
                OpenPickerForReplace(sch, schemeSeg);
            }
        };
        menu.Items.Add(replaceMenuItem);

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
    /// Storyboard 卡片内的逐帧时间调整行。每次点击只移动一帧。
    /// </summary>
    private UIElement BuildMiniTimeRow(Segment seg)
    {
        var panel = new StackPanel();
        var container = new Border
        {
            Margin = new Thickness(0, 7, 0, 0),
            Padding = new Thickness(7),
            Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF7, 0xFA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0xE5, 0xEB)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Child = panel,
        };

        Button MiniBtn(string content)
        {
            return new Button
            {
                Content = content, Width = 26, Height = 26, Padding = new Thickness(0),
                FontSize = 13, FontWeight = FontWeights.SemiBold,
                Background = new SolidColorBrush(Color.FromRgb(0xE7, 0xF0, 0xFF)),
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x6B, 0xE5)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
        }

        UIElement FrameRow(
            string label, string timecode, string minusTip, string plusTip,
            Action minusAction, Action plusAction)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });

            grid.Children.Add(new TextBlock
            {
                Text = label, FontSize = 9, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x6B, 0xE5)),
                VerticalAlignment = VerticalAlignment.Center,
            });

            var minus = MiniBtn("−");
            minus.ToolTip = minusTip;
            minus.Click += (_, _) => minusAction();
            Grid.SetColumn(minus, 1);
            grid.Children.Add(minus);

            var timeBorder = new Border
            {
                Height = 26, Margin = new Thickness(5, 0, 5, 0),
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0xDC, 0xE3)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = new TextBlock
                {
                    Text = timecode, FontSize = 10, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x23)),
                    FontFamily = new System.Windows.Media.FontFamily("Consolas, monospace"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Grid.SetColumn(timeBorder, 2);
            grid.Children.Add(timeBorder);

            var plus = MiniBtn("+");
            plus.ToolTip = plusTip;
            plus.Click += (_, _) => plusAction();
            Grid.SetColumn(plus, 3);
            grid.Children.Add(plus);
            return grid;
        }

        panel.Children.Add(new TextBlock
        {
            Text = $"帧边界 · {seg.EffectiveFps:0.###} fps",
            FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x4A)),
            Margin = new Thickness(0, 0, 0, 6),
        });
        panel.Children.Add(FrameRow(
            "IN", seg.StartTimecode, "起点向前一帧", "起点向后一帧",
            () => { _segmentVm!.AdjustStartFrame(seg, -1); RefreshDetail(); },
            () => { _segmentVm!.AdjustStartFrame(seg, 1); RefreshDetail(); }));

        var outRow = FrameRow(
            "OUT", seg.EndTimecode, "终点向前一帧", "终点向后一帧",
            () => { _segmentVm!.AdjustEndFrame(seg, -1); RefreshDetail(); },
            () => { _segmentVm!.AdjustEndFrame(seg, 1); RefreshDetail(); });
        if (outRow is FrameworkElement outElement)
        {
            outElement.Margin = new Thickness(0, 5, 0, 0);
        }
        panel.Children.Add(outRow);
        panel.Children.Add(new TextBlock
        {
            Text = $"播放停在 {seg.LastFrameTimecode}",
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x80)),
            Margin = new Thickness(28, 6, 0, 0),
        });

        return container;
    }

    // ---- Phase 4b：分镜选择抽屉控制 ----

    private void OpenPickerForInsert(MixScheme scheme, int position)
    {
        _drawerIntent = DrawerIntent.Insert;
        _drawerInsertPosition = position;
        _drawerReplaceTarget = null;
        PickerDrawer.HeaderText = $"选择分镜插入到位置 {position}";
        PopulatePickerItems(scheme);
        PickerDrawer.Visibility = Visibility.Visible;
    }

    private void OpenPickerForReplace(MixScheme scheme, SchemeSegment target)
    {
        _drawerIntent = DrawerIntent.Replace;
        _drawerInsertPosition = 0;
        _drawerReplaceTarget = target;
        PickerDrawer.HeaderText = "选择新分镜替换";
        PopulatePickerItems(scheme);
        PickerDrawer.Visibility = Visibility.Visible;
    }

    private void PopulatePickerItems(MixScheme scheme)
    {
        if (_pickerItems is null || _project is null) return;
        _pickerItems.Clear();

        // 收集项目所有分镜（来自 _segmentVm 优先，没有就走 _project.ProjectVideos）
        var allSegments = new List<Segment>();
        if (_project.ProjectVideos is { } pvs)
        {
            foreach (var pv in pvs)
            {
                if (pv.Video?.Segments is { } segs)
                {
                    allSegments.AddRange(segs);
                }
            }
        }

        var inSchemeIds = new HashSet<Guid>(scheme.SchemeSegments
            .Where(ss => ss.SegmentId is not null)
            .Select(ss => ss.SegmentId!.Value));

        var totalCount = allSegments.Count;
        var alreadyCount = 0;

        foreach (var seg in allSegments)
        {
            var alreadyIn = inSchemeIds.Contains(seg.Id);
            if (alreadyIn) alreadyCount++;
            var item = new ViewModels.Cards.SegmentPickerItem(seg, alreadyIn)
            {
                ThumbnailImage = LoadThumbBitmapStatic(seg.ThumbnailPath),
            };
            _pickerItems.Add(item);
        }

        PickerDrawer.StatsText = $"共 {totalCount} 个分镜 · 已在方案 {alreadyCount} 个";
    }

    private static System.Windows.Media.Imaging.BitmapImage? LoadThumbBitmapStatic(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        // 借用 ThumbnailCache（已经返回 ImageSource，需要 cast 或重新加载）
        // 简化：直接从文件流构造（drawer 一次性加载所有缩略图，不耗内存）
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.DecodePixelWidth = 120;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private void OnDrawerSegmentPicked(object? sender, Segment segment)
    {
        if (_vm.SelectedScheme is not { } scheme) return;

        switch (_drawerIntent)
        {
            case DrawerIntent.Insert:
                if (_vm.InsertSegment(segment, _drawerInsertPosition, scheme))
                {
                    ToastCenter.Shared.Show($"已插入「{TrimText(segment.Text, 15)}」", ToastStyle.Success);
                    RefreshDetail();
                    // 抽屉保持打开供连续操作；刷新已在方案项的置灰态
                    PopulatePickerItems(scheme);
                }
                else
                {
                    ToastCenter.Shared.Show("无法插入：分镜已在方案中", ToastStyle.Warning);
                }
                break;
            case DrawerIntent.Replace:
                if (_drawerReplaceTarget is { } target)
                {
                    if (_vm.ReplaceSegment(target, segment, scheme))
                    {
                        ToastCenter.Shared.Show("已替换", ToastStyle.Success);
                        RefreshDetail();
                        CloseDrawer();
                    }
                    else
                    {
                        ToastCenter.Shared.Show("替换失败：新分镜已在方案中", ToastStyle.Warning);
                    }
                }
                break;
        }
    }

    private void OnDrawerCloseRequested(object? sender, EventArgs e) => CloseDrawer();

    private void CloseDrawer()
    {
        PickerDrawer.Visibility = Visibility.Collapsed;
        _drawerIntent = DrawerIntent.None;
        _drawerInsertPosition = 0;
        _drawerReplaceTarget = null;
    }

    private static string TrimText(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= max ? text : text.Substring(0, max) + "…";
    }

    private static System.Windows.Media.ImageSource? LoadThumbStatic(string? path) =>
        Infrastructure.ThumbnailCache.Shared.GetImage(path);
}
