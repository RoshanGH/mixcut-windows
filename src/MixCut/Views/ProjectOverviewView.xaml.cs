using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MixCut.Models;
using MixCut.ViewModels;

namespace MixCut.Views;

/// <summary>项目概览。对应 macOS 版 ProjectOverviewView。</summary>
public partial class ProjectOverviewView : UserControl, IProjectView
{
    private readonly MainViewModel _vm;
    private readonly Action<NavigationItem> _navigate;
    private readonly Action? _onProjectChanged;
    private Project? _currentProject;
    private bool _isEditingName;

    public ProjectOverviewView(MainViewModel vm, Action<NavigationItem> navigate,
                               Action? onProjectChanged = null)
    {
        _vm = vm;
        _navigate = navigate;
        _onProjectChanged = onProjectChanged;
        InitializeComponent();
    }

    public void LoadProject(Project project)
    {
        // 切项目时强制结束未提交的重命名编辑（CLAUDE.md 切换项目联动铁律：重置可变状态）
        EndNameEdit(commit: false);
        _currentProject = project;
        ProjectName.Text = project.Name;
        ProjectCreatedAt.Text = "创建于 " + project.CreatedAt.ToString("yyyy-MM-dd HH:mm");
        VideoCount.Text = project.VideoCount.ToString();
        SegmentCount.Text = project.SegmentCount.ToString();
        SchemeCount.Text = project.SchemeCount.ToString();
        VideoCountBadge.Text = project.VideoCount.ToString();

        ApplyStatusBadge(project.Status);
        ApplyActionButtonState(project);

        if (project.VideoCount == 0)
        {
            EmptyState.Visibility = Visibility.Visible;
            VideoListSection.Visibility = Visibility.Collapsed;
        }
        else
        {
            EmptyState.Visibility = Visibility.Collapsed;
            VideoListSection.Visibility = Visibility.Visible;
            BuildVideoCards(project);
        }

        Serilog.Log.Information(
            "[OverviewLoad] project={Pid} name=\"{Name}\" videos={V} segments={S} schemes={Sc}",
            project.Id, project.Name, project.VideoCount,
            project.SegmentCount, project.SchemeCount);
    }

    private void BuildVideoCards(Project project)
    {
        VideoGrid.Children.Clear();
        foreach (var video in project.Videos)
        {
            // v0.3.0 对齐 Mac：所有视频显示容器统一 9:16 手机端竖屏比例（信息流广告投放规格）。
            // 视频原始横屏 → UniformToFill 裁剪填充；用户看到的就是投放规格。
            const double thumbAspect = 9.0 / 16.0;
            const double targetPlayerHeight = 280;
            double playerHeight = targetPlayerHeight;
            double playerWidth = playerHeight * thumbAspect;

            var card = new Border
            {
                Width = playerWidth + 16,
                Margin = new Thickness(0, 0, 12, 12),
                Padding = new Thickness(8),
                Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)),
                CornerRadius = new CornerRadius(10),
            };
            var sp = new StackPanel();

            var playerFrame = new Border
            {
                Width = playerWidth, Height = playerHeight,
                Margin = new Thickness(0, 0, 0, 8),
                CornerRadius = new CornerRadius(6), ClipToBounds = true,
            };
            var player = new Components.InlineVideoPlayer();
            player.SetVideo(video.LocalPath, video.ThumbnailPath);
            playerFrame.Child = player;
            sp.Children.Add(playerFrame);

            sp.Children.Add(new TextBlock
            {
                Text = video.Name, FontSize = 11, FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var duration = FormatDurationText(video.Duration);
            var resolution = video.Width > 0 ? video.Resolution : "未知";
            sp.Children.Add(new TextBlock
            {
                Text = $"{duration} · {resolution} · {video.Segments.Count} 分镜",
                FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                Margin = new Thickness(0, 2, 0, 0),
            });

            card.Child = sp;
            VideoGrid.Children.Add(card);
        }
    }

    private static string FormatDurationText(double seconds)
    {
        var mins = (int)seconds / 60;
        var secs = (int)seconds % 60;
        return $"{mins}:{secs:D2}";
    }

    private void ApplyStatusBadge(ProjectStatus status)
    {
        StatusText.Text = status.ToLabel();
        (Color background, Color text) = status switch
        {
            ProjectStatus.Created => (Color.FromRgb(0xEE, 0xEE, 0xEF), Color.FromRgb(0x66, 0x66, 0x66)),
            ProjectStatus.Importing or ProjectStatus.Analyzing or ProjectStatus.Generating
                => (Color.FromRgb(0xFF, 0xF1, 0xDC), Color.FromRgb(0xC0, 0x6F, 0x00)),
            ProjectStatus.Ready => (Color.FromRgb(0xE7, 0xF0, 0xFF), Color.FromRgb(0x1D, 0x6B, 0xE5)),
            ProjectStatus.Completed => (Color.FromRgb(0xE2, 0xF5, 0xE8), Color.FromRgb(0x2E, 0x8B, 0x57)),
            ProjectStatus.Archived => (Color.FromRgb(0xF0, 0xF0, 0xF2), Color.FromRgb(0x99, 0x99, 0x99)),
            _ => (Color.FromRgb(0xEE, 0xEE, 0xEF), Color.FromRgb(0x66, 0x66, 0x66)),
        };
        StatusBadge.Background = new SolidColorBrush(background);
        StatusText.Foreground = new SolidColorBrush(text);
        StatusDot.Fill = new SolidColorBrush(text);
    }

    private void ApplyActionButtonState(Project project)
    {
        SchemesButton.IsEnabled = project.SegmentCount > 0;
        ExportButton.IsEnabled = project.SchemeCount > 0;
        SchemesButton.Opacity = SchemesButton.IsEnabled ? 1 : 0.5;
        ExportButton.Opacity = ExportButton.IsEnabled ? 1 : 0.5;
    }

    private void OnGoImport(object sender, RoutedEventArgs e) => _navigate(NavigationItem.ImportMedia);
    private void OnGoSchemes(object sender, RoutedEventArgs e) => _navigate(NavigationItem.Schemes);
    private void OnGoExport(object sender, RoutedEventArgs e) => _navigate(NavigationItem.Export);

    // StatCard 点击直接跳转对应工作区（对齐 macOS v0.2.4）
    private void OnStatCardImport(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => _navigate(NavigationItem.ImportMedia);
    private void OnStatCardSegmentLibrary(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => _navigate(NavigationItem.SegmentLibrary);
    private void OnStatCardSchemes(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => _navigate(NavigationItem.Schemes);

    // ---- 双击项目名重命名（对齐剪映/Final Cut Pro 桌面级体验）----

    private void OnProjectNameMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && _currentProject is not null)
        {
            BeginNameEdit();
            e.Handled = true;
        }
    }

    private void BeginNameEdit()
    {
        if (_currentProject is null || _isEditingName) return;
        _isEditingName = true;
        ProjectNameEditor.Text = _currentProject.Name;
        ProjectNameEditor.Visibility = Visibility.Visible;
        ProjectName.Visibility = Visibility.Collapsed;
        ProjectNameEditor.Focus();
        ProjectNameEditor.SelectAll();
    }

    private void OnProjectNameEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            EndNameEdit(commit: true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndNameEdit(commit: false);
            e.Handled = true;
        }
    }

    private void OnProjectNameEditorLostFocus(object sender, RoutedEventArgs e)
    {
        // 失焦默认提交（同 Finder / Explorer 重命名行为）
        EndNameEdit(commit: true);
    }

    private void EndNameEdit(bool commit)
    {
        if (!_isEditingName) return;
        _isEditingName = false;

        try
        {
            if (commit && _currentProject is not null)
            {
                var newName = ProjectNameEditor.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(newName) && newName != _currentProject.Name)
                {
                    var oldName = _currentProject.Name;
                    _vm.ProjectVM.RenameProject(_currentProject, newName);
                    ProjectName.Text = newName;
                    Serilog.Log.Information(
                        "[ProjectRename] {Pid} \"{Old}\" -> \"{New}\"",
                        _currentProject.Id, oldName, newName);
                    _onProjectChanged?.Invoke();
                }
            }
        }
        finally
        {
            ProjectNameEditor.Visibility = Visibility.Collapsed;
            ProjectName.Visibility = Visibility.Visible;
        }
    }

    /// <summary>视频卡片显示模型（含缩略图与显示文本）。</summary>
    private sealed class VideoCardModel
    {
        public string Name { get; }
        public string Detail { get; }
        public ImageSource? ThumbnailImage { get; }

        public VideoCardModel(Video video)
        {
            Name = video.Name;
            var duration = FormatDuration(video.Duration);
            var resolution = video.Width > 0 ? video.Resolution : "未知";
            Detail = $"{duration} · {resolution} · {video.Segments.Count} 分镜";
            ThumbnailImage = LoadThumbnail(video.ThumbnailPath);
        }

        private static string FormatDuration(double seconds)
        {
            var mins = (int)seconds / 60;
            var secs = (int)seconds % 60;
            return $"{mins}:{secs:D2}";
        }

        private static ImageSource? LoadThumbnail(string? path) =>
            Infrastructure.ThumbnailCache.Shared.GetImage(path);

        private static ImageSource? LoadThumbnail_Unused(string? path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }
            try
            {
                using var stream = File.OpenRead(path);
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
