using System.Windows;
using System.Windows.Controls;
using MixCut.Models;
using MixCut.Services.ASR;
using MixCut.Services.Export;
using MixCut.Utilities;
using MixCut.ViewModels;

namespace MixCut.Views;

/// <summary>主窗口。侧边栏（项目列表 + 导航）+ 内容区。对应 macOS 版 ContentView。</summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly AppSettings _settings;
    private readonly ExportService _exportService;
    private readonly BatchSegmentExportService _batchExportService;
    private readonly ASRService _asrService;
    private WelcomeView? _welcome;
    private readonly Dictionary<NavigationItem, FrameworkElement> _views = new();
    /// <summary>记录每个视图上次 LoadProject 的 projectId，避免 nav 切换时重复加载。</summary>
    private readonly Dictionary<NavigationItem, Guid> _viewLastLoadedProjectId = new();
    private bool _suppressSelection;

    public MainWindow(
        MainViewModel vm, AppSettings settings,
        ExportService exportService, BatchSegmentExportService batchExportService,
        ASRService asrService,
        UpdateBannerViewModel updateBannerVm)
    {
        _vm = vm;
        _settings = settings;
        _exportService = exportService;
        _batchExportService = batchExportService;
        _asrService = asrService;
        InitializeComponent();

        // 初始化全局 Toast 容器（任何代码都可调 ToastService.Show 弹出反馈）
        MixCut.Views.Components.ToastService.Initialize(ToastHost);

        // v0.3.1：顶部更新 banner —— DataContext 注入 VM，fire-and-forget 触发静默检查（不阻塞 UI）
        UpdateBannerHost.DataContext = updateBannerVm;
        _ = updateBannerVm.CheckSilentlyAsync();

        ProjectList.ItemsSource = _vm.ProjectVM.Projects;
        NavList.ItemsSource = NavigationItemExtensions.All.Select(n => n.LabelWithIcon()).ToList();
        NavList.SelectedIndex = Math.Clamp(_settings.LastNavItem, 0, NavigationItemExtensions.All.Count - 1);
        UpdateNavEnabled();

        RefreshDepsWarning();

        // v0.5.0 修复：素材分析完写入 segments 后，失效依赖该数据的视图缓存。
        // 否则用户切到 SegmentLibrary / Schemes 时仍看到旧数据，必须重启应用才能刷新。
        _vm.ImportVM.SegmentsChanged += OnSegmentsChanged;

        if (_settings.LastSelectedProjectId is { } lastId)
        {
            var match = _vm.ProjectVM.Projects.FirstOrDefault(p => p.Id == lastId);
            if (match is not null)
            {
                _suppressSelection = true;
                _vm.ProjectVM.SelectedProject = match;
                ProjectList.SelectedItem = match;
                _suppressSelection = false;
                UpdateNavEnabled();
            }
        }
        UpdateContent();
    }

    private void OnProjectSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
        {
            return;
        }
        _vm.ProjectVM.SelectedProject = ProjectList.SelectedItem as Project;
        // 持久化最近选中的项目（应用重启时恢复）
        _settings.LastSelectedProjectId = _vm.ProjectVM.SelectedProject?.Id;
        // P0-10：撤销栈是项目级的（捕获了某项目的 DB 数据），切项目必须清空，防在 B 撤销 A 的删除写脏数据。
        Infrastructure.UndoStack.UndoManager.Shared.Clear();
        UpdateNavEnabled();
        UpdateContent();
    }

    /// <summary>当前未选中项目时，禁用工作区导航（对齐 macOS SidebarView 的 opacity 0.35）。</summary>
    private void UpdateNavEnabled()
    {
        var hasProject = _vm.ProjectVM.SelectedProject is not null;
        NavList.IsEnabled = hasProject;
        NavList.Opacity = hasProject ? 1.0 : 0.4;
    }

    private void OnNavSelected(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedIndex >= 0)
        {
            _vm.SelectedNavItem = NavigationItemExtensions.All[NavList.SelectedIndex];
            _settings.LastNavItem = NavList.SelectedIndex;
        }
        UpdateContent();
    }

    private void UpdateContent()
    {
        var project = _vm.ProjectVM.SelectedProject;
        if (project is null)
        {
            _welcome ??= new WelcomeView(_vm.ProjectVM, _settings, _asrService, RefreshAfterProjectChange);
            SetContentWithFade(_welcome);
            return;
        }

        var view = GetView(_vm.SelectedNavItem);
        if (view is IProjectView projectView)
        {
            // 只在 project 变化时才 LoadProject。nav 切换且 project 未变 → skip，避免每次切 nav 都全量重载（用户感知的"卡顿"主因）
            if (!_viewLastLoadedProjectId.TryGetValue(_vm.SelectedNavItem, out var lastId)
                || lastId != project.Id)
            {
                projectView.LoadProject(project);
                _viewLastLoadedProjectId[_vm.SelectedNavItem] = project.Id;
            }
        }
        SetContentWithFade(view);
    }

    /// <summary>
    /// P0-5：切换内容区时做一次 160ms 淡入，替代生硬的瞬间切换，对齐剪映 / FCP 级丝滑。
    /// 仅在目标 view 与当前不同才动画，避免同视图重复刷新时无谓闪烁。
    /// </summary>
    private void SetContentWithFade(FrameworkElement view)
    {
        if (ReferenceEquals(ContentArea.Content, view))
        {
            return;
        }
        ContentArea.Content = view;
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0,
            new Duration(TimeSpan.FromMilliseconds(160)))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
            },
        };
        ContentArea.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private FrameworkElement GetView(NavigationItem item)
    {
        if (_views.TryGetValue(item, out var existing))
        {
            return existing;
        }

        FrameworkElement view = item switch
        {
            NavigationItem.Overview => new ProjectOverviewView(_vm, NavigateTo, RefreshAfterProjectChange),
            NavigationItem.ImportMedia => new ImportView(_vm.ImportVM, RefreshAfterProjectChange),
            // Feature flag：默认 V2（MVVM 数据驱动），失败时设 AppSettings.UseNewSegmentLibrary=false 回退 V1。
            NavigationItem.SegmentLibrary => _settings.UseNewSegmentLibrary
                ? new SegmentLibraryViewV2(_vm.SegmentVM, _batchExportService, _settings)
                : (FrameworkElement)new SegmentLibraryView(_vm.SegmentVM, _batchExportService, _settings),
            NavigationItem.Schemes => new SchemesView(_vm.SchemeVM, _vm.SegmentVM),
            NavigationItem.Export => new ExportView(_vm.SchemeVM, _exportService, _settings),
            _ => new ProjectOverviewView(_vm, NavigateTo, RefreshAfterProjectChange),
        };
        _views[item] = view;
        return view;
    }

    /// <summary>暴露 SchemeViewModel 给子视图（如 SegmentLibrary 组合方案场景）调用，避免依赖静态 host。</summary>
    public SchemeViewModel SchemeViewModel => _vm.SchemeVM;

    /// <summary>从内容视图请求切换导航（如概览页的快速操作按钮）。</summary>
    public void NavigateTo(NavigationItem item)
    {
        NavList.SelectedIndex = (int)item;
    }

    /// <summary>
    /// 从分镜库「✨ 组合为方案」流程跳转到 Schemes 板块并选中指定方案。
    /// 对齐 Mac NavigationCoordinator.navigateToSchemes(selecting:).
    /// 1) 失效 Schemes 视图缓存，确保下次 UpdateContent 时强制 LoadProject 重查 DB
    /// 2) 切到 Schemes nav（触发 UpdateContent → LoadProject → 显示新方案）
    /// 3) 调用 SchemesView.SelectScheme 把焦点定位到新建方案
    /// </summary>
    public void NavigateToSchemesAndSelect(MixScheme scheme)
    {
        // 失效缓存：新方案是在 Schemes 视图外创建的，缓存命中会跳过 LoadProject
        _viewLastLoadedProjectId.Remove(NavigationItem.Schemes);

        NavList.SelectedIndex = (int)NavigationItem.Schemes;

        // 切到 Schemes 后 UpdateContent 已触发；这时 view 是最新的，调 SelectScheme 选中
        if (_views.TryGetValue(NavigationItem.Schemes, out var v) && v is SchemesView schemesView)
        {
            schemesView.SelectScheme(scheme);
        }
    }

    /// <summary>
    /// 分析完成 / segments 写库后失效 SegmentLibrary 和 Schemes 的视图缓存。
    /// 用户下次切过去时强制 LoadProject 重查 DB，避免看到旧数据。
    /// 如果当前正在这俩视图，立刻刷一遍 UpdateContent。
    /// </summary>
    private void OnSegmentsChanged()
    {
        Dispatcher.Invoke(() =>
        {
            _viewLastLoadedProjectId.Remove(NavigationItem.SegmentLibrary);
            _viewLastLoadedProjectId.Remove(NavigationItem.Schemes);
            _viewLastLoadedProjectId.Remove(NavigationItem.Overview);
            if (_vm.SelectedNavItem is NavigationItem.SegmentLibrary
                or NavigationItem.Schemes
                or NavigationItem.Overview)
            {
                UpdateContent();
            }
        });
    }

    private void RefreshAfterProjectChange()
    {
        // 数据变化（导入视频/删除分镜/生成方案 等）：清空缓存让所有视图下次切回时强制 reload
        _viewLastLoadedProjectId.Clear();
        var currentId = _vm.ProjectVM.SelectedProject?.Id;
        _vm.ProjectVM.FetchProjects();
        UpdateNavEnabled();
        if (currentId is { } id)
        {
            _suppressSelection = true;
            var match = _vm.ProjectVM.Projects.FirstOrDefault(p => p.Id == id);
            _vm.ProjectVM.SelectedProject = match;
            ProjectList.SelectedItem = match;
            _suppressSelection = false;
        }
        UpdateContent();
    }

    private void OnNewProjectClick(object sender, RoutedEventArgs e)
    {
        var dialog = new NewProjectDialog(_vm.ProjectVM) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            RefreshAfterProjectChange();
            var created = _vm.ProjectVM.SelectedProject;
            if (created is not null)
            {
                ProjectList.SelectedItem = _vm.ProjectVM.Projects.FirstOrDefault(p => p.Id == created.Id);
            }
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(_settings, _asrService) { Owner = this }.ShowDialog();
    }

    /// <summary>侧边栏「微信」入口：弹出微信号卡片（issue #5）。</summary>
    private void OnWeChatClick(object sender, RoutedEventArgs e)
    {
        WeChatPopup.IsOpen = true;
    }

    /// <summary>复制微信号到剪贴板，文案临时变「已复制 ✓」（关卡片时由 OnWeChatPopupClosed 复位）。</summary>
    private void OnCopyWeChatClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(WeChatIdBox.Text);
            CopyWeChatButton.Content = "已复制 ✓";
        }
        catch (Exception ex)
        {
            // 剪贴板偶发被其他进程占用会抛 COMException —— 不让异常逃逸，给用户兜底提示。
            Serilog.Log.Warning(ex, "[WeChat] 复制微信号到剪贴板失败");
            CopyWeChatButton.Content = "复制失败，请手动选择";
        }
    }

    /// <summary>卡片关闭时重置「已复制」状态，下次打开恢复初始文案。</summary>
    private void OnWeChatPopupClosed(object sender, EventArgs e)
    {
        CopyWeChatButton.Content = "复制微信号";
    }

    private void OnNewProjectCommand(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
        => OnNewProjectClick(sender, e);

    private void OnSettingsCommand(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
        => OnSettingsClick(sender, e);

    private void OnShowShortcutsCommand(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
    {
        var dialog = new KeyboardShortcutsDialog { Owner = this };
        dialog.ShowDialog();
    }

    /// <summary>点击侧边栏 logo 回到欢迎页（清掉当前选中项目）。对齐 macOS v0.2.4。</summary>
    private void OnLogoClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _vm.ProjectVM.SelectedProject = null;
        _settings.LastSelectedProjectId = null;
        ProjectList.SelectedItem = null;
        UpdateNavEnabled();
        UpdateContent();
    }

    /// <summary>显示依赖未配置警告（API Key / Whisper 模型）。对齐 macOS v0.2.4 sidebar warning。</summary>
    private void RefreshDepsWarning()
    {
        var hasApiKey = _settings.HasApiKey(_settings.ActiveProvider);
        var hasModel = _asrService.IsModelAvailable();
        DepsWarningBadge.Visibility = (hasApiKey && hasModel)
            ? Visibility.Collapsed : Visibility.Visible;

        var lines = new List<string>();
        if (!hasApiKey) lines.Add("⚠ 未配置 AI API Key");
        if (!hasModel) lines.Add("⚠ 语音模型未下载");
        DepsWarningBadge.ToolTip = lines.Count > 0
            ? string.Join("\n", lines) + "\n（点击打开设置）"
            : null;
    }

    private void OnDepsWarningClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;  // 阻止冒泡到 logo click
        OnSettingsClick(sender, e);
        RefreshDepsWarning();
    }

    /// <summary>双击项目列表项 = 重命名。对齐 macOS v0.2.4 项目侧边栏双击。</summary>
    private void OnProjectDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // 只在双击在 ListBoxItem 上时触发（避免点空白处也触发）
        if (e.OriginalSource is System.Windows.DependencyObject src
            && FindAncestor<ListBoxItem>(src) is { DataContext: Project })
        {
            OnRenameProject(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private static T? FindAncestor<T>(System.Windows.DependencyObject? cur) where T : System.Windows.DependencyObject
    {
        while (cur is not null)
        {
            if (cur is T t) return t;
            cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
        }
        return null;
    }

    /// <summary>Ctrl+数字 跳转到对应工作区（5 个 NavigationItem）；F2 重命名当前项目。</summary>
    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.KeyboardDevice.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            // P0-10：Ctrl+Z 全局撤销（目前覆盖分镜批量删除，后续扩展方案/项目删除）。
            if (e.Key == System.Windows.Input.Key.Z)
            {
                try
                {
                    var desc = Infrastructure.UndoStack.UndoManager.Shared.Undo();
                    if (desc is null)
                    {
                        Views.Shared.ToastCenter.Shared.Show("没有可撤销的操作", Views.Shared.ToastStyle.Info);
                    }
                }
                catch
                {
                    Views.Shared.ToastCenter.Shared.Show("撤销失败", Views.Shared.ToastStyle.Error);
                }
                e.Handled = true;
                return;
            }

            var idx = e.Key switch
            {
                System.Windows.Input.Key.D1 => 0,
                System.Windows.Input.Key.D2 => 1,
                System.Windows.Input.Key.D3 => 2,
                System.Windows.Input.Key.D4 => 3,
                System.Windows.Input.Key.D5 => 4,
                _ => -1,
            };
            if (idx >= 0 && idx < NavigationItemExtensions.All.Count)
            {
                NavList.SelectedIndex = idx;
                e.Handled = true;
                return;
            }
        }
        if (e.Key == System.Windows.Input.Key.F2 && ProjectList.SelectedItem is Project)
        {
            OnRenameProject(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void OnArchiveProject(object sender, RoutedEventArgs e)
    {
        if (ProjectList.SelectedItem is Project project)
        {
            _vm.ProjectVM.ArchiveProjectCommand.Execute(project);
            RefreshAfterProjectChange();
        }
    }

    private void OnRenameProject(object sender, RoutedEventArgs e)
    {
        if (ProjectList.SelectedItem is not Project project)
        {
            return;
        }
        var dialog = new RenameDialog(project.Name) { Owner = this };
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.NewName))
        {
            _vm.ProjectVM.RenameProject(project, dialog.NewName);
            RefreshAfterProjectChange();
        }
    }

    private void OnDeleteProject(object sender, RoutedEventArgs e)
    {
        if (ProjectList.SelectedItem is not Project project)
        {
            return;
        }
        var confirm = MessageBox.Show(
            $"确定要删除项目「{project.Name}」吗？\n所有视频、分镜和方案数据都将被删除，此操作不可恢复。",
            "确认删除项目", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm == MessageBoxResult.OK)
        {
            _vm.ProjectVM.DeleteProjectCommand.Execute(project);
            _vm.ProjectVM.SelectedProject = null;
            RefreshAfterProjectChange();
        }
    }
}
