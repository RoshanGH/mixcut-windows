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
    private readonly VariantBatchExportService _variantExportService;
    private readonly ASRService _asrService;
    private readonly DubbingViewModel _dubbingVm;
    private readonly Services.Dubbing.DubExportService _dubExport;
    private readonly IServiceProvider _services;
    private WelcomeView? _welcome;
    private readonly Dictionary<NavigationItem, FrameworkElement> _views = new();
    /// <summary>记录每个视图上次 LoadProject 的 projectId，避免 nav 切换时重复加载。</summary>
    private readonly Dictionary<NavigationItem, Guid> _viewLastLoadedProjectId = new();
    private bool _suppressSelection;

    public MainWindow(
        MainViewModel vm, AppSettings settings,
        ExportService exportService, VariantBatchExportService variantExportService,
        ASRService asrService,
        DubbingViewModel dubbingVm,
        Services.Dubbing.DubExportService dubExport,
        UpdateBannerViewModel updateBannerVm,
        IServiceProvider services)
    {
        _vm = vm;
        _settings = settings;
        _exportService = exportService;
        _variantExportService = variantExportService;
        _asrService = asrService;
        _dubbingVm = dubbingVm;
        _dubExport = dubExport;
        _services = services;
        InitializeComponent();

        // 初始化全局 Toast 容器（任何代码都可调 ToastService.Show 弹出反馈）
        MixCut.Views.Components.ToastService.Initialize(ToastHost);


        // v0.3.1：顶部更新 banner —— DataContext 注入 VM，fire-and-forget 触发静默检查（不阻塞 UI）
        UpdateBannerHost.DataContext = updateBannerVm;
        _ = updateBannerVm.CheckSilentlyAsync();

        // issue #4 Phase 3：导出命令 smoke 若失败已自动降级 CPU 编码，等其跑完后用人话提示用户。
        _ = NotifyIfExportFellBackToCpuAsync();

        ProjectList.ItemsSource = _vm.ProjectVM.Projects;
        NavList.ItemsSource = NavigationItemExtensions.All.Select(n => n.LabelWithIcon()).ToList();
        NavList.SelectedIndex = Math.Clamp(_settings.LastNavItem, 0, NavigationItemExtensions.All.Count - 1);
        UpdateNavEnabled();

        RefreshDepsWarning();

        // v0.5.0 修复：素材分析完写入 segments 后，失效依赖该数据的视图缓存。
        // 否则用户切到 SegmentLibrary / Schemes 时仍看到旧数据，必须重启应用才能刷新。
        _vm.ImportVM.SegmentsChanged += OnSegmentsChanged;

        // v0.5.0 配音：变体增删/生成后失效 SegmentLibrary/Schemes/Export/Overview 缓存（对齐 OnSegmentsChanged）。
        _dubbingVm.DubsChanged += OnDubsChanged;

        // issue #23 Agent 接入：Agent 绕过 UI 流程写库后广播全量重载（复用「数据变更 → 清缓存 +
        // 重拉项目列表 + 刷当前视图」机制），保证侧边栏/列表立即反映 Agent 的改动。
        Services.Agent.AgentGateway.UiReloadRequested += OnAgentDataChanged;

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

        // 恢复上次窗口尺寸 / 最大化状态（商业软件标配：用户调好的窗口下次还在）。
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    /// <summary>关闭时记忆窗口尺寸 / 最大化状态。最大化时存 RestoreBounds（还原后的尺寸）而非屏幕尺寸。</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            var maximized = WindowState == WindowState.Maximized;
            _settings.WindowMaximized = maximized;
            var bounds = maximized ? RestoreBounds : new Rect(Left, Top, ActualWidth, ActualHeight);
            if (bounds.Width >= 960) _settings.WindowWidth = bounds.Width;
            if (bounds.Height >= 600) _settings.WindowHeight = bounds.Height;
        }
        catch { /* 记忆失败不影响关闭 */ }
        base.OnClosing(e);
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

        // v0.5.0 配音：离开分镜库时收起右侧检视器 drawer（否则会浮在其它视图上）。
        if (_vm.SelectedNavItem != NavigationItem.SegmentLibrary)
        {
            _vm.SegmentVM.DubInspector?.Clear();
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
        // 换页 = 用户注意力已经离开：停掉任何还在播的内联预览，别让上一页的声音跟到新页面。
        Components.InlineVideoPlayer.StopAll();
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
            NavigationItem.ImportMedia => new ImportView(_vm.ImportVM, RefreshAfterProjectChange, NavigateTo),
            // Feature flag：默认 V2（MVVM 数据驱动），失败时设 AppSettings.UseNewSegmentLibrary=false 回退 V1。
            NavigationItem.SegmentLibrary => _settings.UseNewSegmentLibrary
                ? new SegmentLibraryViewV2(_vm.SegmentVM, _variantExportService, _settings, _services, NavigateTo)
                : (FrameworkElement)new SegmentLibraryView(_vm.SegmentVM, _variantExportService, _settings),
            NavigationItem.Schemes => new SchemesView(_vm.SchemeVM, _vm.SegmentVM),
            // #22：BGM 库是全局视图（不依赖项目数据），不实现 IProjectView。
            NavigationItem.BgmLibrary => new BgmLibraryView(
                (Services.Bgm.BgmLibraryService)_services.GetService(typeof(Services.Bgm.BgmLibraryService))!,
                (Services.VideoProcessing.FFmpegRunner)_services.GetService(typeof(Services.VideoProcessing.FFmpegRunner))!),
            NavigationItem.Export => new ExportView(_vm.SchemeVM, _exportService, _dubExport, _settings,
                (Services.Dubbing.VocalSeparationService)_services.GetService(typeof(Services.Dubbing.VocalSeparationService))!,
                (Services.Bgm.BgmLibraryService)_services.GetService(typeof(Services.Bgm.BgmLibraryService))!,
                NavigateTo),
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
            // 自验证锚点：这条链断过一次 —— 缓存这里失效了，但 SegmentLibraryViewV2.LoadProject
            // 内部另有一句「同项目就 return」把重载吃掉，表现为「分析完切回分镜库看不到新分镜」。
            // 该守卫已删除；留这行日志，配合 [GroupDiag] 可核对「失效 → 重载」是否真的走通。
            Serilog.Log.Information(
                "[RefreshDiag] 分镜数据已变更，失效 分镜库/方案/概览 视图缓存（当前页={Nav}）",
                _vm.SelectedNavItem);
            if (_vm.SelectedNavItem is NavigationItem.SegmentLibrary
                or NavigationItem.Schemes
                or NavigationItem.Overview)
            {
                UpdateContent();
            }
        });
    }

    /// <summary>
    /// 配音变体增删/生成后失效 Schemes/Export/Overview 缓存（它们显示变体数/组合数）。
    /// 不动 SegmentLibrary：配音 UI（设置条计数 + 变体检视器）由 DubbingViewModel 自己刷新，
    /// 强制全量重载反而会打断正在进行的配音交互。
    /// </summary>
    private void OnDubsChanged()
    {
        Dispatcher.Invoke(() =>
        {
            _viewLastLoadedProjectId.Remove(NavigationItem.Schemes);
            _viewLastLoadedProjectId.Remove(NavigationItem.Export);
            _viewLastLoadedProjectId.Remove(NavigationItem.Overview);
            if (_vm.SelectedNavItem is NavigationItem.Schemes
                or NavigationItem.Export
                or NavigationItem.Overview)
            {
                UpdateContent();
            }
            // P0-2：在分镜库且右侧配音检视器已打开时，配音数据变化（尤其批量「克隆并改写」完成）
            // 要刷新当前分镜的变体池，否则用户等半天却看到旧状态（像「点了没反应」）。
            else if (_vm.SelectedNavItem is NavigationItem.SegmentLibrary
                     && _vm.SegmentVM.DubInspector is { Segment: not null } inspector)
            {
                _ = inspector.RefreshVariantsAsync();
            }
        });
    }

    /// <summary>Agent（MCP）写库后的全量重载：清视图缓存 + 重拉项目列表 + 刷新当前视图。</summary>
    private void OnAgentDataChanged()
    {
        // BeginInvoke：广播可能发生在工具 handler 的写路径内，异步排队避免重入正在进行的操作。
        Dispatcher.BeginInvoke(RefreshAfterProjectChange);
    }

    private void RefreshAfterProjectChange()
    {
        // 数据变化（导入视频/删除分镜/生成方案 等）：清空缓存让所有视图下次切回时强制 reload
        _viewLastLoadedProjectId.Clear();
        var currentId = _vm.ProjectVM.SelectedProject?.Id;

        // 关键修复（v0.10.2）：FetchProjects 会清空并重填 Projects 集合，绑定的 ProjectList.SelectedItem
        // 会**瞬时变 null** → 触发 OnProjectSelected 把 SelectedProject 置 null。之前 UpdateNavEnabled 恰好
        // 在这个 null 瞬间被调用 → 把左侧导航灰掉禁用；而随后恢复选中时又没有再调 UpdateNavEnabled，
        // 导致导入/分析期间（RefreshAfterProjectChange 反复触发）**左侧导航突然灰掉、点不动**。
        // 修法：把「FetchProjects + 恢复选中」整段用 _suppressSelection 包住（不让瞬时 null 回调生效），
        // 且 UpdateNavEnabled 放到**恢复选中之后**再调，按真实选中状态刷新可用性。
        _suppressSelection = true;
        try
        {
            _vm.ProjectVM.FetchProjects();
            if (currentId is { } id)
            {
                var match = _vm.ProjectVM.Projects.FirstOrDefault(p => p.Id == id);
                _vm.ProjectVM.SelectedProject = match;
                ProjectList.SelectedItem = match;
            }
        }
        finally
        {
            _suppressSelection = false;
        }
        UpdateNavEnabled();
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

    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>
    /// 打开设置窗口。公开出来是为了让子视图能提供「去设置」的一键入口
    /// （例如方案页发现没配 API Key 时），而不必各自去拿 AppSettings / ASRService 依赖。
    /// </summary>
    public void OpenSettings()
    {
        new SettingsWindow(_settings, _asrService,
            (Services.Agent.AgentGateway)_services.GetService(typeof(Services.Agent.AgentGateway))!)
        { Owner = this }.ShowDialog();
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
                        Components.ToastService.Show("没有可撤销的操作", Components.ToastStyle.Info);
                    }
                    else
                    {
                        // 撤销**成功**时原本什么都不显示。恢复的内容常常在视口外（比如删了 20 个分镜后
                        // 按 Ctrl+Z），用户看不到任何变化，会怀疑没生效而继续按 —— 直到弹出
                        // 「没有可撤销的操作」才发现自己已经多撤了几步，却不知道撤到哪了。
                        Components.ToastService.Show($"已撤销：{desc}", Components.ToastStyle.Success);
                    }
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "[UndoDiag] 撤销失败");
                    Components.ToastService.Show("撤销失败，数据未改动", Components.ToastStyle.Error);
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
                System.Windows.Input.Key.D6 => 5,
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

    /// <summary>
    /// issue #4 Phase 3：若导出命令 smoke 失败已把编码降级到 CPU，等 smoke 跑完后弹一条人话提示。
    /// 不吓人（不显示 exit code / codec 私有选项报错），只告诉用户「已切软件编码、导出照常可用」。
    /// </summary>
    private async System.Threading.Tasks.Task NotifyIfExportFellBackToCpuAsync()
    {
        try
        {
            // smoke 在启动后台 Task 里跑（ffmpeg 探测 + 编码 testsrc），最多等 ~10s 拿结果。
            for (var i = 0; i < 20 && !Infrastructure.ExportCommandSmokeTest.Completed; i++)
            {
                await System.Threading.Tasks.Task.Delay(500);
            }
            if (Infrastructure.ExportCommandSmokeTest.DidFallbackToCpu)
            {
                Dispatcher.Invoke(() =>
                    MixCut.Views.Components.ToastService.Show(
                        "当前 ffmpeg 与硬件加速不兼容，已自动切回 CPU 编码，导出可正常使用",
                        MixCut.Views.Components.ToastStyle.Warning));
            }
        }
        catch
        {
            // 提示失败绝不影响任何功能（导出降级已在后台完成）。
        }
    }

    /// <summary>
    /// 右键项目列表时先选中命中项 —— WPF 的 ListBoxItem 默认不响应右键选中。
    /// 与「菜单挂在行上 + handler 从行数据取对象」双保险，杜绝「选中 A、右键 B 点删除，删掉 A」的数据事故。
    /// </summary>
    private void OnProjectListRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ProjectList, (DependencyObject)e.OriginalSource) is ListBoxItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    /// <summary>从右键菜单项取它所属的项目：菜单挂在行的 Grid 上，DataContext 即该行的 Project。</summary>
    private static Project? ProjectFromMenu(object sender) =>
        (sender as FrameworkElement)?.DataContext as Project;

    private void OnRenameProject(object sender, RoutedEventArgs e)
    {
        // 用菜单所属行的数据，而不是 SelectedItem（后者在右键别的行时会指向错误对象）。
        if (ProjectFromMenu(sender) is not Project project)
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
        // 同上：取菜单所属行的项目，绝不用 SelectedItem（删错项目 = 丢掉该项目全部素材/分镜/方案）。
        if (ProjectFromMenu(sender) is not Project project)
        {
            return;
        }
        // 破坏性确认走自绘对话框：红色主按钮 + 动词文案 + 默认焦点在「取消」（防习惯性回车误删）。
        // 并把代价说清楚 —— 用户有权在按下删除前知道自己会失去什么。
        var cost = $"{project.VisibleVideoCount} 个视频 · {project.SegmentCount} 个分镜 · {project.SchemeCount} 个方案";
        var confirmed = Shared.MixCutDialog.Confirm(
            this,
            $"删除项目「{project.Name}」？",
            $"将一并删除：{cost}。\n此操作不可恢复。",
            confirmText: "删除项目", cancelText: "取消", destructive: true, icon: "⚠");
        if (confirmed)
        {
            _vm.ProjectVM.DeleteProjectCommand.Execute(project);
            _vm.ProjectVM.SelectedProject = null;
            RefreshAfterProjectChange();
        }
    }
}
