using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MixCut.Models;
using MixCut.ViewModels;
using MixCut.ViewModels.Cards;

namespace MixCut.Views;

/// <summary>
/// SegmentLibraryView V2 —— MVVM 数据驱动版。
/// 关键设计：DataTemplate 默认轻量（只放 Image + 占位），hover 才动态挂 InlineVideoPlayer。
/// 这样 100 张卡片不会有 100 个 MediaElement 同时存在。
/// </summary>
public partial class SegmentLibraryViewV2 : UserControl, IProjectView
{
    private readonly SegmentLibraryViewModel _vm;
    private readonly Services.Export.BatchSegmentExportService _batchExport;
    private readonly Utilities.AppSettings _settings;
    private Project? _currentProject;

    public SegmentLibraryViewV2(
        SegmentLibraryViewModel vm,
        Services.Export.BatchSegmentExportService batchExport,
        Utilities.AppSettings settings)
    {
        _vm = vm;
        _batchExport = batchExport;
        _settings = settings;
        InitializeComponent();
        DataContext = _vm;

        BuildTypeChips();
        _vm.SelectionChanged += OnSelectionChanged;
        // 右键单删 / Ctrl+Z 恢复后，VM 广播此事件，View 刷新统计 / 类型 chip / 空态（VM 已自行 RebuildGroups）。
        _vm.SegmentsStructurallyChanged += OnSegmentsStructurallyChanged;

        Focusable = true;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public void LoadProject(Project project)
    {
        if (_currentProject?.Id == project.Id) return;
        _currentProject = project;

        _vm.SetSelectionMode(false);
        _vm.ClearSelection();
        _vm.LoadSegments(project);
        BuildTypeChips();
        // 卡片立即渲染（黑底先出，CardVM 后台异步加载缩略图，加载完 INPC 刷新）
        _vm.RebuildGroups();
        UpdateStats();
        UpdateSelectionToolbar();
        UpdateEmptyState();
        // 已有 thumbnail 立即走 cache
        var paths = _vm.FilteredSegments
            .Select(s => s.ThumbnailPath)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToList();
        foreach (var p in paths)
        {
            _ = Infrastructure.ThumbnailCache.Shared.LoadAsync(p);
        }

        // 后台补生成缺失的 segment thumbnail（历史数据修复）
        _ = _vm.RepairMissingThumbnailsAsync();

        // 自验证：3 秒后写一行诊断汇总到日志（path / cached / missing 数）
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(3000);
            var total = paths.Count;
            var cached = paths.Count(p => Infrastructure.ThumbnailCache.Shared.PeekImage(p) is not null);
            var fileExists = paths.Count(p => System.IO.File.Exists(p));
            Serilog.Log.Information(
                "[ThumbDiag] project={Pid} totalSegments={Total} thumbPaths={Paths} fileExists={Exist} cachedNow={Cached} missing={Miss}",
                project.Id, _vm.FilteredSegments.Count, total, fileExists, cached, total - cached);
        });
    }

    // ============ 工具栏 ============

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _vm.Filter.SearchText = SearchBox.Text;
        ClearSearchButton.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Collapsed : Visibility.Visible;
        _vm.ApplyFilter();
        _vm.RebuildGroups();
        UpdateStats();
        UpdateEmptyState();
    }

    private void OnClearSearch(object sender, RoutedEventArgs e) => SearchBox.Text = string.Empty;

    private void OnViewModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _vm.IsGridView = ViewModeCombo.SelectedIndex == 0;
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _vm.SortByQuality = SortCombo.SelectedIndex == 1;
        _vm.ApplyFilter();
        _vm.RebuildGroups();
    }

    private void OnResetFilter(object sender, RoutedEventArgs e)
    {
        _vm.ResetFilter();
        SearchBox.Text = string.Empty;
        BuildTypeChips();
        _vm.RebuildGroups();
        UpdateStats();
        UpdateEmptyState();
    }

    // ============ 多选 ============

    private void OnToggleSelectionMode(object sender, RoutedEventArgs e)
    {
        _vm.SetSelectionMode(!_vm.IsSelectionMode);
        _vm.SyncSelectionModeToCards();
        UpdateSelectionToolbar();
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        _vm.SelectAllVisible();
        _vm.SyncCheckedToCards();
    }

    private void OnInvertSelection(object sender, RoutedEventArgs e)
    {
        _vm.InvertSelectionVisible();
        _vm.SyncCheckedToCards();
    }

    private void OnClearSelection(object sender, RoutedEventArgs e)
    {
        _vm.ClearSelection();
        _vm.SyncCheckedToCards();
    }

    private void OnBatchExport(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedSegmentIds.Count == 0) return;
        var dialog = new BatchExportDialog(
            _batchExport, _settings, _vm.SelectedSegments, _vm.NumberFor)
        {
            Owner = Window.GetWindow(this),
        };
        dialog.ShowDialog();
    }

    private void OnBatchDelete(object sender, RoutedEventArgs e)
    {
        var count = _vm.SelectedSegmentIds.Count;
        if (count == 0) return;
        var confirm = MessageBox.Show(
            $"确定要删除选中的 {count} 个分镜吗？删除后可按 Ctrl+Z 撤销。",
            "确认批量删除", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;
        // P0-16：删除失败（DB 保存异常）时不谎报成功，给人话提示 + 重试入口。
        var deleted = _vm.DeleteSelectedSegments();
        if (deleted is null)
        {
            Components.ToastService.Show("删除失败，请重试", Components.ToastStyle.Error);
            return;
        }
        RefreshAfterSegmentChange();

        // P0-10：压入撤销栈，Ctrl+Z 可一键恢复被删分镜。
        if (deleted.Count > 0)
        {
            Infrastructure.UndoStack.UndoManager.Shared.Push(
                new Infrastructure.UndoStack.DelegateUndoAction(
                    $"删除 {deleted.Count} 个分镜",
                    () =>
                    {
                        var n = _vm.RestoreSegments(deleted);
                        RefreshAfterSegmentChange();
                        Components.ToastService.Show(
                            n > 0 ? $"已恢复 {n} 个分镜" : "恢复失败，请重试",
                            n > 0 ? Components.ToastStyle.Success : Components.ToastStyle.Error);
                    }));
        }
        Components.ToastService.Show($"已删除 {count} 个分镜", Components.ToastStyle.Warning,
            "撤销", () => Infrastructure.UndoStack.UndoManager.Shared.Undo());
    }

    /// <summary>分镜增删后统一刷新分组/类型 chip/统计/工具栏/空态（删除与撤销恢复共用）。</summary>
    private void RefreshAfterSegmentChange()
    {
        _vm.RebuildGroups();
        BuildTypeChips();
        UpdateStats();
        UpdateSelectionToolbar();
        UpdateEmptyState();
    }

    private void OnSelectionChanged()
    {
        Dispatcher.Invoke(() =>
        {
            UpdateSelectionToolbar();
            _vm.SyncCheckedToCards();
        });
    }

    /// <summary>
    /// VM 触发的结构性变更（右键单删 / Ctrl+Z 恢复）后刷新 View 侧 chrome。
    /// VM 已自行 RebuildGroups（卡片增减已生效），这里只补统计 / 类型 chip / 工具栏 / 空态，避免重复 RebuildGroups。
    /// </summary>
    private void OnSegmentsStructurallyChanged()
    {
        Dispatcher.Invoke(() =>
        {
            BuildTypeChips();
            UpdateStats();
            UpdateSelectionToolbar();
            UpdateEmptyState();
        });
    }

    private void UpdateSelectionToolbar()
    {
        if (_vm.IsSelectionMode)
        {
            SelectionToolbar.Visibility = Visibility.Visible;
            SelectionModeButton.Content = "✕ 退出多选";
        }
        else
        {
            SelectionToolbar.Visibility = Visibility.Collapsed;
            SelectionModeButton.Content = "☑ 多选";
        }
        var n = _vm.SelectedSegmentIds.Count;
        SelectionCountText.Text = $"已选 {n}";
        BatchExportButton.IsEnabled = n > 0;
        BatchDeleteButton.IsEnabled = n > 0;
        CombineSchemeButton.IsEnabled = n >= 2;
    }

    private async void OnCombineSchemeClick(object sender, RoutedEventArgs e)
    {
        // async void 必须包 try/catch（CLAUDE.md §UI 标准）
        try
        {
            var selected = _vm.SelectedSegmentsInOrder;
            if (selected.Count < 2)
            {
                return;
            }

            var sheet = new SegmentLibrary.ArrangeOrderSheet(selected)
            {
                Owner = Window.GetWindow(this),
            };
            if (sheet.ShowDialog() != true || sheet.Result is null)
            {
                return;
            }

            if (_currentProject is not Project project
                || Window.GetWindow(this) is not MainWindow mainWin)
            {
                return;
            }

            Components.ToastService.Show("正在生成自定义方案…", Components.ToastStyle.Info);

            var schemeVm = mainWin.SchemeViewModel;
            schemeVm.LoadSchemes(project);

            var scheme = await schemeVm.CreateCustomSchemeAsync(sheet.Result, project);
            if (scheme is not null)
            {
                Components.ToastService.Show(
                    $"已生成方案「{scheme.Name}」", Components.ToastStyle.Success);
                mainWin.NavigateToSchemesAndSelect(scheme);
                _vm.SetSelectionMode(false);
                _vm.SyncSelectionModeToCards();
                UpdateSelectionToolbar();
            }
            else
            {
                Components.ToastService.Show(
                    "生成失败，请检查 AI Key 或网络", Components.ToastStyle.Warning);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[SegmentLibraryViewV2.OnCombineSchemeClick] 异常: {Message}", ex.Message);
            Components.ToastService.Show($"组合失败: {ex.Message}", Components.ToastStyle.Warning);
        }
    }

    // ============ 类型 chip ============

    private void BuildTypeChips()
    {
        var counts = _vm.CountByType();
        var chips = new List<UIElement>();
        foreach (var type in SemanticTypeExtensions.All)
        {
            var isSelected = _vm.Filter.SemanticTypes.Contains(type);
            var count = counts.GetValueOrDefault(type, 0);
            var isEmpty = count == 0;
            var color = (Color)ColorConverter.ConvertFromString(type.ToColorHex());
            var displayColor = isEmpty ? Color.FromRgb(0x99, 0x99, 0x99) : color;

            var stack = new StackPanel { Orientation = Orientation.Horizontal };
            stack.Children.Add(new TextBlock
            {
                Text = type.ToLabel(),
                FontSize = 11,
                FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Medium,
                Foreground = new SolidColorBrush(isSelected ? Colors.White : displayColor),
                VerticalAlignment = VerticalAlignment.Center,
            });
            stack.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(
                    (byte)(isSelected ? 0x40 : 0x20), displayColor.R, displayColor.G, displayColor.B)),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(5, 0, 5, 0),
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = count.ToString(CultureInfo.InvariantCulture),
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(isSelected ? Colors.White : displayColor),
                },
            });

            var btn = new Button
            {
                Tag = type,
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(9, 4, 9, 4),
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(
                    (byte)(isSelected ? 0x66 : 0x26), displayColor.R, displayColor.G, displayColor.B)),
                Background = new SolidColorBrush(Color.FromArgb(
                    (byte)(isSelected ? 0xC0 : 0x10), displayColor.R, displayColor.G, displayColor.B)),
                Content = stack,
                Opacity = isEmpty ? 0.35 : 1.0,
            };
            btn.Click += OnTypeChipClick;
            chips.Add(btn);
        }
        TypeChipList.ItemsSource = chips;
    }

    private void OnTypeChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SemanticType type })
        {
            if (!_vm.Filter.SemanticTypes.Add(type))
            {
                _vm.Filter.SemanticTypes.Remove(type);
            }
            _vm.ApplyFilter();
            _vm.RebuildGroups();
            BuildTypeChips();
            UpdateStats();
            UpdateEmptyState();
        }
    }

    private void UpdateStats()
    {
        var stats = _vm.Statistics();
        StatsText.Text =
            $"{_vm.FilteredSegments.Count} / {stats.Total} 个分镜 · 平均质量 " +
            stats.AverageQuality.ToString("F1", CultureInfo.InvariantCulture);
        ResetButton.Visibility =
            (_vm.Filter.SemanticTypes.Count > 0 || !string.IsNullOrEmpty(_vm.Filter.SearchText))
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateEmptyState()
    {
        var isEmpty = _vm.FilteredSegments.Count == 0;
        EmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        GroupsScroller.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
    }

    // ============ 卡片事件 + 懒加载播放器 ============

    private void OnCardClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SegmentCardViewModel card })
        {
            card.HandleCardClick();
        }
    }

    /// <summary>
    /// hover 中的 player 与 CardVM 时间字段的双向同步：调时间时实时刷新 player 的 segment 范围。
    /// 用 ConditionalWeakTable 把 handler 绑到 player 实例上，MouseLeave 时取消订阅。
    /// </summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Components.InlineVideoPlayer, PropertyChangedEventHandler> _playerHandlers = new();

    private void OnCardMouseEnter(object sender, MouseEventArgs e)
    {
        // 全局点击播放：hover 不再创建播放器、不自动播，仅用于卡片悬停高亮。
        if (sender is FrameworkElement { Tag: SegmentCardViewModel card }) card.IsHovering = true;
    }

    private void OnCardMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SegmentCardViewModel card }) card.IsHovering = false;
    }

    /// <summary>
    /// 卡片上始终显示的 ▶ 被点击：在该卡 VideoHost 里创建内联播放器并立即播放（全局点击播放，不依赖 hover）。
    /// 播放时隐藏 ▶ 与时长角标（控制栏已显示时长）；播完 / 停止 / 被其他卡抢占由 player.Idle 还原。
    /// </summary>
    private void OnSegmentPlayClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SegmentCardViewModel card } playBtn) return;
        if (!card.IsVideoFileAvailable) return;

        var thumbGrid = FindAncestorByName(playBtn, "ThumbGrid");
        if (thumbGrid is null) return;
        var videoHost = FindChild<ContentControl>(thumbGrid, "VideoHost");
        if (videoHost is null) return;
        var badge = FindChild<Border>(thumbGrid, "CardDurationBadge");

        // 已经在播这张卡 → 忽略重复点击，避免重复 Open 抖动。
        if (videoHost.Content is Components.InlineVideoPlayer { IsPlaying: true })
        {
            return;
        }

        Serilog.Log.Information(
            "[SegPlayDiag] 点击播放 seq={Seq} startFrame={SF} videoHostHash={H}",
            card.SequenceNumber, card.Segment.StartFrame, videoHost.GetHashCode());

        if (videoHost.Content is not Components.InlineVideoPlayer player)
        {
            player = new Components.InlineVideoPlayer
            {
                AutoPlayOnHover = false,
                VideoStretch = System.Windows.Media.Stretch.UniformToFill,
            };
            player.SetSegment(card.VideoLocalPath!, card.ThumbnailPath,
                card.Segment.StartFrame, card.Segment.EndFrame, card.Segment.EffectiveFps);

            // ±0.1s 微调时实时同步给正在播放的 player。
            PropertyChangedEventHandler handler = (_, args) =>
            {
                if (args.PropertyName is nameof(SegmentCardViewModel.StartTime)
                                      or nameof(SegmentCardViewModel.EndTime))
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (videoHost.Content == player && card.IsVideoFileAvailable)
                        {
                            player.SetSegment(card.VideoLocalPath!, card.ThumbnailPath,
                                card.Segment.StartFrame, card.Segment.EndFrame, card.Segment.EffectiveFps);
                        }
                    }));
                }
            };
            card.PropertyChanged += handler;
            _playerHandlers.Add(player, handler);

            // 播完 / 停止 / 被其他卡抢占 → 拆掉播放器，还原静态缩略图 + ▶ + 时长角标。
            player.Idle += (_, _) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (videoHost.Content == player)
                    {
                        TeardownHoverPlayer(videoHost, player, card);
                    }
                    playBtn.Visibility = Visibility.Visible;
                    if (badge is not null) badge.Visibility = Visibility.Visible;
                }), System.Windows.Threading.DispatcherPriority.Background);
            };

            videoHost.Content = player;
        }

        // 隐藏 ▶ 与时长角标（避免与控制栏时长重叠），开始播放。
        playBtn.Visibility = Visibility.Collapsed;
        if (badge is not null) badge.Visibility = Visibility.Collapsed;
        // 用已布局好的 videoHost 尺寸预置解码目标（竖屏框），免去昂贵的同步 UpdateLayout（数十卡 ~150-200ms）。
        // 刚塞进去的 player 自身 ActualWidth=0，但 videoHost 早已布局、尺寸确定。
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(videoHost);
        player.PrimeDecodeSize(
            (int)Math.Round(videoHost.ActualWidth * dpi.DpiScaleX),
            (int)Math.Round(videoHost.ActualHeight * dpi.DpiScaleY));
        player.Play();
    }

    /// <summary>从子元素沿可视树向上查找指定 x:Name 的祖先。</summary>
    private static FrameworkElement? FindAncestorByName(DependencyObject? d, string name)
    {
        while (d is not null)
        {
            if (d is FrameworkElement fe && fe.Name == name) return fe;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    /// <summary>拆掉点击创建的内联播放器：解绑时间同步监听 + 从 VideoHost 移除（还原静态缩略图）。</summary>
    private void TeardownHoverPlayer(ContentControl videoHost, Components.InlineVideoPlayer player, SegmentCardViewModel card)
    {
        if (_playerHandlers.TryGetValue(player, out var handler))
        {
            card.PropertyChanged -= handler;
            _playerHandlers.Remove(player);
        }
        if (videoHost.Content == player)
        {
            videoHost.Content = null;
        }
    }

    // ============ 时间编辑框 commit ============

    private void OnStartLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: SegmentCardViewModel card } tb)
        {
            card.CommitStartCommand.Execute(tb.Text);
            tb.Text = card.StartTime.ToString("F1", CultureInfo.InvariantCulture);
        }
    }

    private void OnEndLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: SegmentCardViewModel card } tb)
        {
            card.CommitEndCommand.Execute(tb.Text);
            tb.Text = card.EndTime.ToString("F1", CultureInfo.InvariantCulture);
        }
    }

    private void OnStartKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void OnEndKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    // ============ QuickEdit ============

    private void OnQuickEditClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SegmentCardViewModel card } btn) return;

        var menu = new ContextMenu();
        foreach (var type in SemanticTypeExtensions.All)
        {
            var item = new MenuItem
            {
                Header = type.ToLabel(),
                IsCheckable = true,
                IsChecked = card.SemanticTypes.Contains(type),
            };
            item.Click += (_, _) => card.ToggleSemanticCommand.Execute(type);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        foreach (var pos in PositionTypeExtensions.All)
        {
            var item = new MenuItem
            {
                Header = pos.ToLabel(),
                IsCheckable = true,
                IsChecked = card.PositionType == pos,
            };
            item.Click += (_, _) => card.UpdatePositionCommand.Execute(pos);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = btn;
        menu.IsOpen = true;
    }

    // ============ 快捷键 ============

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;

        if (e.Key == Key.Escape && _vm.IsSelectionMode)
        {
            _vm.SetSelectionMode(false);
            _vm.SyncSelectionModeToCards();
            UpdateSelectionToolbar();
            e.Handled = true;
            return;
        }

        if (!_vm.IsSelectionMode) return;
        if (Keyboard.Modifiers != ModifierKeys.Control) return;

        switch (e.Key)
        {
            case Key.A:
                _vm.SelectAllVisible();
                _vm.SyncCheckedToCards();
                e.Handled = true;
                break;
            case Key.D:
                _vm.InvertSelectionVisible();
                _vm.SyncCheckedToCards();
                e.Handled = true;
                break;
            case Key.D0:
            case Key.NumPad0:
                _vm.ClearSelection();
                _vm.SyncCheckedToCards();
                e.Handled = true;
                break;
        }
    }

    // ============ Helper ============

    /// <summary>在 Visual Tree 里按 name 找子元素。用于 DataTemplate 实例化的元素查找。</summary>
    private static T? FindChild<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t && t.Name == name) return t;
            var found = FindChild<T>(child, name);
            if (found is not null) return found;
        }
        return null;
    }
}
