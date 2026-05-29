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
            $"确定要删除选中的 {count} 个分镜吗？此操作不可恢复。",
            "确认批量删除", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;
        _vm.DeleteSelectedSegments();
        _vm.RebuildGroups();
        BuildTypeChips();
        UpdateStats();
        UpdateSelectionToolbar();
        UpdateEmptyState();
        Components.ToastService.Show($"已删除 {count} 个分镜", Components.ToastStyle.Warning);
    }

    private void OnSelectionChanged()
    {
        Dispatcher.Invoke(() =>
        {
            UpdateSelectionToolbar();
            _vm.SyncCheckedToCards();
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
        if (sender is not FrameworkElement { Tag: SegmentCardViewModel card } root) return;
        card.IsHovering = true;

        if (!card.IsVideoFileAvailable) return;
        var videoHost = FindChild<ContentControl>(root, "VideoHost");
        if (videoHost is null) return;
        if (videoHost.Content is Components.InlineVideoPlayer) return;

        var player = new Components.InlineVideoPlayer { AutoPlayOnHover = true };
        player.SetSegment(card.VideoLocalPath!, card.ThumbnailPath, card.StartTime, card.EndTime);

        // 关键：监听 CardVM 时间变化，实时同步给 player。
        // 用户调 ±0.1s 时 hover 中的 player 立刻反映新片段范围（播放中则重启播放）。
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
                            card.StartTime, card.EndTime);
                    }
                }));
            }
        };
        card.PropertyChanged += handler;
        _playerHandlers.Add(player, handler);

        videoHost.Content = player;
    }

    private void OnCardMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: SegmentCardViewModel card } root) return;
        card.IsHovering = false;

        var videoHost = FindChild<ContentControl>(root, "VideoHost");
        if (videoHost?.Content is not Components.InlineVideoPlayer player) return;

        // 解绑 CardVM 监听
        if (_playerHandlers.TryGetValue(player, out var handler))
        {
            card.PropertyChanged -= handler;
            _playerHandlers.Remove(player);
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (videoHost.Content == player)
            {
                videoHost.Content = null;
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
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
