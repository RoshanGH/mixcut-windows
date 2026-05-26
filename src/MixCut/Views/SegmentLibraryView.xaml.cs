using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MixCut.Models;
using MixCut.ViewModels;

namespace MixCut.Views;

/// <summary>分镜素材库视图。对应 macOS 版 SegmentLibraryView（卡片内嵌全部编辑入口，无独立详情面板）。</summary>
public partial class SegmentLibraryView : UserControl, IProjectView
{
    private readonly SegmentLibraryViewModel _vm;
    private readonly Services.Export.BatchSegmentExportService _batchExport;
    private readonly Utilities.AppSettings _settings;

    public SegmentLibraryView(
        SegmentLibraryViewModel vm,
        Services.Export.BatchSegmentExportService batchExport,
        Utilities.AppSettings settings)
    {
        _vm = vm;
        _batchExport = batchExport;
        _settings = settings;
        InitializeComponent();
        BuildTypeChips();
        _vm.SelectionChanged += OnSelectionChanged;
        // 多选快捷键：ESC 退多选 / Ctrl+A 全选 / Ctrl+D 反选 / Ctrl+0 清空
        // 对齐 Mac SegmentLibraryView 的 keyboardShortcut。
        Focusable = true;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // 文本框里输入不抢键
        if (e.OriginalSource is TextBox) return;

        // ESC 任何时候都退多选
        if (e.Key == System.Windows.Input.Key.Escape && _vm.IsSelectionMode)
        {
            _vm.SetSelectionMode(false);
            UpdateSelectionToolbar();
            RefreshContent();
            e.Handled = true;
            return;
        }

        // Ctrl 组合键仅在多选模式下生效
        if (!_vm.IsSelectionMode) return;
        if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control) return;

        switch (e.Key)
        {
            case System.Windows.Input.Key.A:
                _vm.SelectAllVisible();
                RefreshContent();
                e.Handled = true;
                break;
            case System.Windows.Input.Key.D:
                _vm.InvertSelectionVisible();
                RefreshContent();
                e.Handled = true;
                break;
            case System.Windows.Input.Key.D0:
            case System.Windows.Input.Key.NumPad0:
                _vm.ClearSelection();
                RefreshContent();
                e.Handled = true;
                break;
        }
    }

    public void LoadProject(Project project)
    {
        // 切项目时必须先退出多选模式 + 清空选中，否则上个项目的多选状态会带过来污染新项目。
        // 对齐 macOS v0.2.5 commit 6530fd6 的修复。
        _vm.SetSelectionMode(false);
        _vm.ClearSelection();

        _vm.LoadSegments(project);
        BuildTypeChips();
        RefreshContent();
        UpdateStats();
        UpdateSelectionToolbar();
    }

    // ---- 多选模式（v0.2.4 批量导出） ----

    private void OnToggleSelectionMode(object sender, RoutedEventArgs e)
    {
        _vm.SetSelectionMode(!_vm.IsSelectionMode);
        UpdateSelectionToolbar();
        RefreshContent();
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        _vm.SelectAllVisible();
        RefreshContent();
    }

    private void OnInvertSelection(object sender, RoutedEventArgs e)
    {
        _vm.InvertSelectionVisible();
        RefreshContent();
    }

    private void OnClearSelection(object sender, RoutedEventArgs e)
    {
        _vm.ClearSelection();
        RefreshContent();
    }

    private void OnBatchExport(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedSegmentIds.Count == 0) return;
        var dialog = new BatchExportDialog(
            _batchExport, _settings,
            _vm.SelectedSegments,
            _vm.NumberFor)
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
        BuildTypeChips();
        RefreshContent();
        UpdateStats();
        UpdateSelectionToolbar();
        Components.ToastService.Show($"已删除 {count} 个分镜", Components.ToastStyle.Warning);
    }

    private void OnSelectionChanged()
    {
        Dispatcher.Invoke(UpdateSelectionToolbar);
    }

    private void OnCardClickedInSelectionMode(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: Segment seg })
        {
            _vm.ToggleSelection(seg);
            RefreshContent();
        }
    }

    private void UpdateSelectionToolbar()
    {
        if (_vm.IsSelectionMode)
        {
            SelectionToolbar.Visibility = Visibility.Visible;
            SelectionModeButton.Content = "✕ 退出多选";
            SelectionModeButton.Background = new SolidColorBrush(Color.FromRgb(0xE7, 0xF0, 0xFF));
            SelectionModeButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0x1D, 0x6B, 0xE5));
            SelectionModeButton.Foreground = new SolidColorBrush(Color.FromRgb(0x1D, 0x6B, 0xE5));
        }
        else
        {
            SelectionToolbar.Visibility = Visibility.Collapsed;
            SelectionModeButton.Content = "☑ 多选";
            SelectionModeButton.Background = Brushes.White;
            SelectionModeButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
            SelectionModeButton.Foreground = Brushes.Black;
        }
        var n = _vm.SelectedSegmentIds.Count;
        SelectionCountText.Text = $"已选 {n}";
        BatchExportButton.IsEnabled = n > 0;
        BatchDeleteButton.IsEnabled = n > 0;
    }

    // ---- 筛选工具栏 ----

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _vm.Filter.SearchText = SearchBox.Text;
        ClearSearchButton.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Collapsed : Visibility.Visible;
        _vm.ApplyFilter();
        RefreshContent();
        UpdateStats();
    }

    private void OnClearSearch(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
    }

    private void OnViewModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }
        _vm.IsGridView = ViewModeCombo.SelectedIndex == 0;
        RefreshContent();
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }
        _vm.SortByQuality = SortCombo.SelectedIndex == 1;
        _vm.ApplyFilter();
        RefreshContent();
    }

    private void OnResetFilter(object sender, RoutedEventArgs e)
    {
        _vm.ResetFilter();
        SearchBox.Text = string.Empty;
        BuildTypeChips();
        RefreshContent();
        UpdateStats();
    }

    // ---- 语义类型筛选芯片 ----

    /// <summary>
    /// 语义类型筛选 chip：label + 数量徽章；count==0 时整 chip 降到 35% 透明 + 灰化（模拟 Mac saturation）。
    /// 对齐 macOS 版 FilterChip。
    /// </summary>
    private void BuildTypeChips()
    {
        var counts = _vm.CountByType();
        var chips = new List<UIElement>();
        foreach (var type in SemanticTypeExtensions.All)
        {
            var isSelected = _vm.Filter.SemanticTypes.Contains(type);
            var color = (Color)ColorConverter.ConvertFromString(type.ToColorHex());
            var count = counts.GetValueOrDefault(type, 0);
            var isEmpty = count == 0;

            // count==0 时用浅灰色取代彩色（WPF 没有 saturation filter，用色彩混合模拟）
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
            // 数量徽章（小胶囊）
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
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(isSelected ? Colors.White : displayColor),
                },
            });

            var btn = new Button
            {
                Tag = type,
                Margin = new Thickness(0, 0, 6, 0),
                Padding = new Thickness(9, 4, 9, 4),
                Cursor = System.Windows.Input.Cursors.Hand,
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
            BuildTypeChips();
            RefreshContent();
            UpdateStats();
        }
    }

    // ---- 统计文本 + 重置按钮 ----

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

    // ---- 分组列表渲染 ----

    private void RefreshContent()
    {
        ContentPanel.Children.Clear();

        if (_vm.FilteredSegments.Count == 0)
        {
            ContentPanel.Children.Add(new TextBlock
            {
                Text = "没有符合条件的分镜",
                Foreground = Brushes.Gray, FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 60, 0, 0),
            });
            return;
        }

        var groups = _vm.GroupedSegments();
        foreach (var group in groups)
        {
            ContentPanel.Children.Add(BuildGroupSection(group));
        }
    }

    private UIElement BuildGroupSection(VideoSegmentGroup group)
    {
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, 22) };

        // 视频标题栏
        var header = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var thumb = new Border
        {
            Width = 44, Height = 28, Background = Brushes.Black,
            CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 10, 0),
            ClipToBounds = true,
        };
        var img = LoadThumbnail(group.Video.ThumbnailPath);
        if (img is not null)
        {
            thumb.Child = new Image { Source = img, Stretch = Stretch.UniformToFill };
        }
        Grid.SetColumn(thumb, 0);
        header.Children.Add(thumb);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = group.Video.Name, FontWeight = FontWeights.SemiBold, FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        info.Children.Add(new TextBlock
        {
            Text = $"{group.Segments.Count} 个分镜 · " +
                   $"{group.Video.Duration.ToString("F0", CultureInfo.InvariantCulture)}s",
            FontSize = 10, Foreground = Brushes.Gray,
        });
        Grid.SetColumn(info, 1);
        header.Children.Add(info);
        section.Children.Add(header);

        // 分镜卡片
        Panel host = _vm.IsGridView
            ? new WrapPanel()
            : new StackPanel();
        foreach (var seg in group.Segments)
        {
            host.Children.Add(BuildSegmentCard(seg));
        }
        section.Children.Add(host);

        return section;
    }

    private UIElement BuildSegmentCard(Segment seg)
    {
        // 列表模式：宽卡片纯文字
        if (!_vm.IsGridView)
        {
            return BuildListRow(seg);
        }

        // 网格模式：缩略图 + 时长 + 类型徽章 + 质量分 + 时间微调（对齐 Mac 版）
        var aspect = seg.Video is { Width: > 0, Height: > 0 } v
            ? (double)v.Width / v.Height
            : 9.0 / 16.0;

        const double cardWidth = 210;
        const double thumbMaxHeight = 320;
        var thumbWidth = cardWidth - 16;
        var thumbHeight = Math.Min(thumbMaxHeight, thumbWidth / aspect);

        // 多选模式下选中态走 SelectedSegmentIds；非多选模式仍走 SelectedSegment 高亮。
        var isMultiSelect = _vm.IsSelectionMode;
        var isCardSelected = isMultiSelect
            ? _vm.SelectedSegmentIds.Contains(seg.Id)
            : _vm.SelectedSegment?.Id == seg.Id;
        var border = new Border
        {
            Background = isCardSelected
                ? new SolidColorBrush(Color.FromArgb(0x18, 0x1D, 0x6B, 0xE5))
                : Brushes.White,
            BorderBrush = isCardSelected
                ? new SolidColorBrush(Color.FromRgb(0x1D, 0x6B, 0xE5))
                : new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xE7)),
            BorderThickness = new Thickness(isCardSelected ? 1.5 : 1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 10, 10),
            Width = cardWidth,
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = seg,
        };
        // 多选模式下点击卡片切换选择；非多选模式走原 OnCardClicked。
        if (isMultiSelect)
        {
            border.MouseLeftButtonDown += OnCardClickedInSelectionMode;
        }
        else
        {
            border.MouseLeftButtonDown += OnCardClicked;
        }
        // 悬停背景变化
        border.MouseEnter += (s, _) =>
        {
            if (s is Border b && b.Tag is Segment seg2 && _vm.SelectedSegment?.Id != seg2.Id)
            {
                b.Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFC));
            }
        };
        border.MouseLeave += (s, _) =>
        {
            if (s is Border b && b.Tag is Segment seg2 && _vm.SelectedSegment?.Id != seg2.Id)
            {
                b.Background = Brushes.White;
            }
        };
        // 右键菜单
        border.ContextMenu = BuildSegmentContextMenu(seg);

        var sp = new StackPanel();

        // 缩略图 + 内嵌播放器（点 ▶ 仅播放该分镜的时间范围）
        var thumbBorder = new Border
        {
            Width = thumbWidth, Height = thumbHeight,
            Background = Brushes.Black,
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
        };
        // 用 Grid 把视频/缩略图层 + 编号徽章 + 多选标记叠起来
        var thumbGrid = new Grid();
        if (seg.Video is { LocalPath: var path } && File.Exists(path))
        {
            // 分镜库卡片：hover 350ms 自动播放 + 全局唯一播放，对齐 Mac SegmentInlinePlayer。
            var player = new Components.InlineVideoPlayer { AutoPlayOnHover = true };
            player.SetSegment(path, seg.ThumbnailPath, seg.StartTime, seg.EndTime);
            thumbGrid.Children.Add(player);
        }
        else
        {
            // 无视频文件时退回纯缩略图
            if (LoadSegmentThumb(seg.ThumbnailPath) is { } img)
            {
                thumbGrid.Children.Add(new Image { Source = img, Stretch = System.Windows.Media.Stretch.UniformToFill });
            }
            else
            {
                thumbGrid.Children.Add(new TextBlock
                {
                    Text = "🎞", FontSize = 24, Foreground = Brushes.Gray,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            thumbGrid.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 6, 6),
                Child = new TextBlock
                {
                    Text = seg.Duration.ToString("F1", CultureInfo.InvariantCulture) + "s",
                    Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.SemiBold,
                },
            });
        }

        // #N 编号徽章（左上角，每视频独立编号）
        var num = _vm.NumberFor(seg);
        if (num > 0)
        {
            thumbGrid.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xC0, 0x1D, 0x6B, 0xE5)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1, 5, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(6, 6, 0, 0),
                Child = new TextBlock
                {
                    Text = "#" + num.ToString(CultureInfo.InvariantCulture),
                    Foreground = Brushes.White,
                    FontSize = 10, FontWeight = FontWeights.Bold,
                    FontFamily = new FontFamily("Consolas"),
                },
            });
        }

        // 多选模式：右上角圆形选中标记
        if (isMultiSelect)
        {
            thumbGrid.Children.Add(new Border
            {
                Width = 24, Height = 24,
                Background = isCardSelected
                    ? new SolidColorBrush(Color.FromRgb(0x1D, 0x6B, 0xE5))
                    : new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xC0, 0x1D, 0x6B, 0xE5)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(999),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 6, 0),
                Child = isCardSelected
                    ? new TextBlock
                    {
                        Text = "✓",
                        Foreground = Brushes.White,
                        FontSize = 14, FontWeight = FontWeights.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    }
                    : null,
            });
        }

        thumbBorder.Child = thumbGrid;
        sp.Children.Add(thumbBorder);

        // 类型徽章 + 位置 + 质量评分（同一 FlowLayout，对齐 Mac 版）
        // 多类型分镜全量展示（Mac FlowLayout 行为），不再 Take(2) 截断。
        var badges = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var t in seg.SemanticTypes)
        {
            badges.Children.Add(MakeTypeBadge(t.ToLabel(), t.ToColorHex()));
        }
        badges.Children.Add(MakeTypeBadge(seg.PositionType.ToLabel(), "#888888"));
        badges.Children.Add(MakeQualityBadge(seg.QualityScore));
        sp.Children.Add(badges);

        // 台词块（对齐 Mac SegmentCard 右侧 rightPanel）
        sp.Children.Add(BuildTranscriptBlock(seg));

        // 时间微调行：hover/选中才显示，对齐 Mac BoundaryAdjustRow + Rectangle 占位
        var boundary = BuildBoundaryAdjustRow(seg);
        var boundaryHost = new Grid { Height = 24, Margin = new Thickness(0, 4, 0, 0) };
        boundary.Visibility = isCardSelected ? Visibility.Visible : Visibility.Collapsed;
        boundaryHost.Children.Add(boundary);
        sp.Children.Add(boundaryHost);

        // 卡片 hover 时浮现 boundary
        border.MouseEnter += (_, _) => boundary.Visibility = Visibility.Visible;
        border.MouseLeave += (_, _) =>
        {
            if (!isCardSelected) boundary.Visibility = Visibility.Collapsed;
        };

        border.Child = sp;
        return border;
    }

    /// <summary>列表模式的紧凑行（带小缩略图）。</summary>
    private UIElement BuildListRow(Segment seg)
    {
        var border = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xE7)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 8),
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = seg,
        };
        border.MouseLeftButtonDown += OnCardClicked;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var thumbBorder = new Border
        {
            Width = 72, Height = 100,
            Background = Brushes.Black, CornerRadius = new CornerRadius(4),
            ClipToBounds = true, Margin = new Thickness(0, 0, 10, 0),
        };
        if (LoadSegmentThumb(seg.ThumbnailPath) is { } thumb)
        {
            thumbBorder.Child = new Image { Source = thumb, Stretch = System.Windows.Media.Stretch.UniformToFill };
        }
        Grid.SetColumn(thumbBorder, 0);
        grid.Children.Add(thumbBorder);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        head.Children.Add(new TextBlock
        {
            Text = seg.SegmentIndex, FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 8, 0),
        });
        head.Children.Add(new TextBlock
        {
            Text = $"{seg.StartTime.ToString("F1", CultureInfo.InvariantCulture)}s-" +
                   $"{seg.EndTime.ToString("F1", CultureInfo.InvariantCulture)}s " +
                   $"({seg.Duration.ToString("F1", CultureInfo.InvariantCulture)}s)",
            FontSize = 11, Foreground = Brushes.Gray,
        });
        info.Children.Add(head);

        var rowBadges = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        foreach (var t in seg.SemanticTypes)
        {
            rowBadges.Children.Add(MakeTypeBadge(t.ToLabel(), t.ToColorHex()));
        }
        rowBadges.Children.Add(MakeTypeBadge(seg.PositionType.ToLabel(), "#888888"));
        info.Children.Add(rowBadges);

        info.Children.Add(new TextBlock
        {
            Text = seg.Text, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            MaxHeight = 50, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var quality = new TextBlock
        {
            Text = "★ " + seg.QualityScore.ToString("F1", CultureInfo.InvariantCulture),
            FontSize = 12, FontWeight = FontWeights.Bold,
            Foreground = QualityColor(seg.QualityScore),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(quality, 2);
        grid.Children.Add(quality);

        border.Child = grid;
        return border;
    }

    /// <summary>分镜卡片底部的边界微调行：IN / OUT 各一组（-/可编辑/+）+ ⋯ 菜单。对齐 Mac BoundaryAdjustRow。</summary>
    private UIElement BuildBoundaryAdjustRow(Segment seg)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 0),
        };

        row.Children.Add(BuildSingleTimeAdjuster(
            seg.StartTime,
            minus: () => _vm.AdjustStartTime(seg, -0.1),
            plus: () => _vm.AdjustStartTime(seg, +0.1),
            commit: v => _vm.SetStartTime(seg, v),
            seg));

        row.Children.Add(new TextBlock
        {
            Text = "–", FontSize = 10, Foreground = Brushes.LightGray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0, 3, 0),
        });

        row.Children.Add(BuildSingleTimeAdjuster(
            seg.EndTime,
            minus: () => _vm.AdjustEndTime(seg, -0.1),
            plus: () => _vm.AdjustEndTime(seg, +0.1),
            commit: v => _vm.SetEndTime(seg, v),
            seg));

        // ⋯ 菜单（快速改语义/位置类型）
        var moreBtn = new Button
        {
            Content = "⋯", FontSize = 10, Padding = new Thickness(0),
            Width = 22, Height = 18, Margin = new Thickness(6, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF2)),
            BorderThickness = new Thickness(0),
            ContextMenu = BuildQuickEditMenu(seg),
        };
        moreBtn.Click += (s, _) =>
        {
            if (s is Button b && b.ContextMenu is { } menu)
            {
                menu.PlacementTarget = b;
                menu.IsOpen = true;
            }
        };
        row.Children.Add(moreBtn);

        return row;
    }

    /// <summary>单个时间调整器：[-] [可编辑文本框] [+]。</summary>
    private UIElement BuildSingleTimeAdjuster(double currentSec, Action minus, Action plus,
        Action<double> commit, Segment seg)
    {
        var group = new StackPanel { Orientation = Orientation.Horizontal };

        var minusBtn = new Button
        {
            Content = "−", FontSize = 10, Padding = new Thickness(0),
            Width = 18, Height = 18,
            Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF4)),
            BorderThickness = new Thickness(0),
        };
        minusBtn.Click += (_, _) =>
        {
            minus();
            RefreshContent();
        };
        group.Children.Add(minusBtn);

        var box = new TextBox
        {
            Text = currentSec.ToString("F1", CultureInfo.InvariantCulture),
            Width = 36, Height = 18, FontSize = 9,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
            TextAlignment = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Margin = new Thickness(2, 0, 2, 0),
        };
        // 提交：失焦或按回车
        box.LostKeyboardFocus += (s, _) => TryCommit(box, currentSec, commit, seg);
        box.KeyDown += (s, ev) =>
        {
            if (ev.Key == System.Windows.Input.Key.Enter)
            {
                System.Windows.Input.Keyboard.ClearFocus();
                ev.Handled = true;
            }
        };
        group.Children.Add(box);

        var plusBtn = new Button
        {
            Content = "+", FontSize = 10, Padding = new Thickness(0),
            Width = 18, Height = 18,
            Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF4)),
            BorderThickness = new Thickness(0),
        };
        plusBtn.Click += (_, _) =>
        {
            plus();
            RefreshContent();
        };
        group.Children.Add(plusBtn);

        return group;
    }

    private void TryCommit(TextBox box, double currentSec, Action<double> commit, Segment seg)
    {
        if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0)
        {
            if (Math.Abs(v - currentSec) > 0.01)
            {
                commit(v);
                RefreshContent();
                return;
            }
        }
        box.Text = currentSec.ToString("F1", CultureInfo.InvariantCulture);
    }

    private ContextMenu BuildQuickEditMenu(Segment seg)
    {
        var menu = new ContextMenu();
        foreach (var type in SemanticTypeExtensions.All)
        {
            var item = new MenuItem
            {
                Header = type.ToLabel(),
                IsCheckable = true,
                IsChecked = seg.SemanticTypes.Contains(type),
            };
            item.Click += (_, _) =>
            {
                _vm.ToggleSemanticType(seg, type);
                RefreshContent();
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        foreach (var pos in PositionTypeExtensions.All)
        {
            var item = new MenuItem
            {
                Header = pos.ToLabel(),
                IsCheckable = true,
                IsChecked = seg.PositionType == pos,
            };
            item.Click += (_, _) =>
            {
                _vm.UpdatePositionType(seg, pos);
                RefreshContent();
            };
            menu.Items.Add(item);
        }
        return menu;
    }

    // 阈值对齐 Mac QualityBadge：9.0+ 绿 / 8.0+ 蓝 / 7.0+ 橙 / <7 红
    private static SolidColorBrush QualityColor(double score) => score switch
    {
        >= 9.0 => new SolidColorBrush(Color.FromRgb(0x2E, 0x8B, 0x57)),
        >= 8.0 => new SolidColorBrush(Color.FromRgb(0x1D, 0x6B, 0xE5)),
        >= 7.0 => new SolidColorBrush(Color.FromRgb(0xC0, 0x6F, 0x00)),
        _ => new SolidColorBrush(Color.FromRgb(0xD3, 0x3A, 0x3A)),
    };

    private static ImageSource? LoadSegmentThumb(string? path) =>
        Infrastructure.ThumbnailCache.Shared.GetImage(path);

    private static Border MakeQualityBadge(double score)
    {
        // 阈值对齐 Mac QualityBadge：9.0+ 绿 / 8.0+ 蓝 / 7.0+ 橙 / <7 红
        var color = score switch
        {
            >= 9.0 => Color.FromRgb(0x2E, 0x8B, 0x57),
            >= 8.0 => Color.FromRgb(0x1D, 0x6B, 0xE5),
            >= 7.0 => Color.FromRgb(0xC0, 0x6F, 0x00),
            _ => Color.FromRgb(0xD3, 0x3A, 0x3A),
        };
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x28, color.R, color.G, color.B)),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(7, 1, 7, 1),
            Margin = new Thickness(0, 0, 4, 4),
            Child = new TextBlock
            {
                Text = "★ " + score.ToString("F1", CultureInfo.InvariantCulture),
                FontSize = 10, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(color),
            },
        };
    }

    private ContextMenu BuildSegmentContextMenu(Segment seg)
    {
        var menu = new ContextMenu();

        // 复制台词（无台词时禁用）
        var copy = new MenuItem
        {
            Header = "📋 复制台词",
            IsEnabled = !string.IsNullOrWhiteSpace(seg.Text),
        };
        copy.Click += (_, _) =>
        {
            try
            {
                System.Windows.Clipboard.SetText(seg.Text ?? string.Empty);
                Components.ToastService.Show("台词已复制", Components.ToastStyle.Success);
            }
            catch
            {
                // Clipboard 偶发 OpenClipboard 被占用，吞掉即可
            }
        };
        menu.Items.Add(copy);

        // 在 Explorer 中显示原视频（对齐 Mac "在 Finder 中显示原视频"）
        if (seg.Video is { LocalPath: var path } && !string.IsNullOrEmpty(path) && File.Exists(path))
        {
            var explore = new MenuItem { Header = "📂 在 Explorer 中显示原视频" };
            explore.Click += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{path}\"",
                        UseShellExecute = true,
                    });
                }
                catch
                {
                    // 路径含特殊字符等罕见情况，忽略
                }
            };
            menu.Items.Add(explore);
        }

        menu.Items.Add(new Separator());

        var delete = new MenuItem { Header = "🗑 删除分镜", Tag = seg };
        delete.Click += (_, _) =>
        {
            var confirm = MessageBox.Show($"删除分镜 {seg.SegmentIndex}？",
                "确认", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (confirm == MessageBoxResult.OK)
            {
                _vm.DeleteSegment(seg);
                if (_vm.SelectedSegment?.Id == seg.Id)
                {
                    _vm.SelectedSegment = null;
                }
                RefreshContent();
                UpdateStats();
                Components.ToastService.Show("已删除分镜", Components.ToastStyle.Warning);
            }
        };
        menu.Items.Add(delete);
        return menu;
    }

    /// <summary>
    /// 分镜卡片台词块：💬 icon + 字数 + 内容滚动文本（最高 56px）。
    /// 对齐 Mac SegmentCard 右侧 rightPanel。无台词时显示居中"暂无台词"。
    /// </summary>
    private static UIElement BuildTranscriptBlock(Segment seg)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xFA)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xF0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 5, 7, 5),
            Margin = new Thickness(0, 6, 0, 0),
        };

        if (string.IsNullOrWhiteSpace(seg.Text))
        {
            var emptyRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 4),
            };
            emptyRow.Children.Add(new TextBlock
            {
                Text = "🔇",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)),
                Margin = new Thickness(0, 0, 4, 0),
            });
            emptyRow.Children.Add(new TextBlock
            {
                Text = "暂无台词",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            border.Child = emptyRow;
            return border;
        }

        var sp = new StackPanel();

        // 头部：💬 台词 · N字
        var head = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 3),
        };
        head.Children.Add(new TextBlock
        {
            Text = "💬",
            FontSize = 9,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        });
        head.Children.Add(new TextBlock
        {
            Text = "台词",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(new TextBlock
        {
            Text = $" · {seg.Text.Length}字",
            FontSize = 9,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
        });
        sp.Children.Add(head);

        // 滚动文本内容（最高 56px ≈ 3-4 行）
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 56,
        };
        scroll.Content = new TextBlock
        {
            Text = seg.Text,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            LineHeight = 16,
            // 可选中复制
            IsHitTestVisible = true,
        };
        sp.Children.Add(scroll);

        border.Child = sp;
        return border;
    }

    private static Border MakeTypeBadge(string text, string colorHex)
    {
        var color = (Color)ColorConverter.ConvertFromString(colorHex);
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x28, color.R, color.G, color.B)),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(7, 1, 7, 1),
            Margin = new Thickness(0, 0, 4, 4),
            Child = new TextBlock
            {
                Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(color),
            },
        };
    }

    private void OnCardClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: Segment seg })
        {
            _vm.SelectedSegment = seg;
            RefreshContent(); // 刷新卡片以显示选中高亮
        }
    }

    // 已删除：右侧 320px 详情面板及其辅助方法 (BuildDetailPanel / BuildInfoRow / BuildEditableTranscript / MakeAdjustRow)。
    // 对齐 macOS 版（Mac UI 同样无右侧整体详情面板）。
    // 所有编辑/查看入口已迁移到分镜卡片本身：
    //   - 语义类型 / 位置类型 / 质量分徽章（卡片底部 WrapPanel）
    //   - 起点 / 终点 ±0.5s 调整 + 数字编辑（BuildBoundaryAdjustRow）
    //   - 右键删除（BuildSegmentContextMenu）
    //   - ⋯ 快速编辑菜单（BuildQuickEditMenu，含语义类型/位置类型切换）

    private static ImageSource? LoadThumbnail(string? path) =>
        Infrastructure.ThumbnailCache.Shared.GetImage(path);
}
