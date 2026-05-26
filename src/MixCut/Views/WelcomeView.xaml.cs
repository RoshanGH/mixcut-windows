using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MixCut.Models;
using MixCut.Services.ASR;
using MixCut.Utilities;
using MixCut.ViewModels;

namespace MixCut.Views;

/// <summary>欢迎页（无项目时显示）。对应 macOS 版 WelcomeView。</summary>
public partial class WelcomeView : UserControl
{
    private readonly ProjectViewModel _projectVM;
    private readonly AppSettings _settings;
    private readonly ASRService _asrService;
    private readonly Action _onProjectCreated;

    public WelcomeView(
        ProjectViewModel projectVM,
        AppSettings settings,
        ASRService asrService,
        Action onProjectCreated)
    {
        _projectVM = projectVM;
        _settings = settings;
        _asrService = asrService;
        _onProjectCreated = onProjectCreated;
        InitializeComponent();
        Loaded += (_, _) => RefreshStatus();
    }

    private void RefreshStatus()
    {
        var hasApiKey = _settings.HasApiKey(_settings.ActiveProvider);
        var hasModel = _asrService.IsModelAvailable();

        ApiKeyWarning.Visibility = hasApiKey ? Visibility.Collapsed : Visibility.Visible;
        WhisperWarning.Visibility = hasModel ? Visibility.Collapsed : Visibility.Visible;
        WarningBox.Visibility = (hasApiKey && hasModel) ? Visibility.Collapsed : Visibility.Visible;

        BuildRecentProjects();
    }

    /// <summary>最近 3 个项目作为快捷入口卡片。对齐 macOS v0.2.4 WelcomeView.recentProjects。</summary>
    private void BuildRecentProjects()
    {
        var recent = _projectVM.Projects
            .OrderByDescending(p => p.UpdatedAt)
            .Take(3)
            .ToList();
        if (recent.Count == 0)
        {
            RecentProjectsSection.Visibility = Visibility.Collapsed;
            return;
        }
        RecentProjectsSection.Visibility = Visibility.Visible;
        var cards = new List<UIElement>();
        foreach (var p in recent)
        {
            cards.Add(BuildProjectCard(p));
        }
        RecentProjectsList.ItemsSource = cards;
    }

    private UIElement BuildProjectCard(Project project)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE3)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = project,
            MinWidth = 140,
        };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = "🎞  " + project.Name,
            FontSize = 12, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        sp.Children.Add(new TextBlock
        {
            Text = $"{project.VideoCount} 视频 · {project.SegmentCount} 分镜",
            FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            Margin = new Thickness(0, 4, 0, 0),
        });
        sp.Children.Add(new TextBlock
        {
            Text = "上次更新：" + project.UpdatedAt.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture),
            FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            Margin = new Thickness(0, 2, 0, 0),
        });
        border.Child = sp;
        border.MouseLeftButtonDown += (_, _) =>
        {
            _projectVM.SelectedProject = project;
            _settings.LastSelectedProjectId = project.Id;
            _onProjectCreated();   // 复用 onChanged 回调让外层刷新
        };
        // Hover 微动
        border.MouseEnter += (_, _) =>
        {
            border.Background = new SolidColorBrush(Color.FromRgb(0xEC, 0xF2, 0xFE));
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x1D, 0x6B, 0xE5));
        };
        border.MouseLeave += (_, _) =>
        {
            border.Background = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7));
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE3));
        };
        return border;
    }

    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        var dialog = new NewProjectDialog(_projectVM) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
        {
            _onProjectCreated();
        }
    }
}
