# MVVM 数据驱动重构 — 设计文档

**日期**：2026-05-25  
**作者**：Claude（与用户协同设计）  
**状态**：设计阶段，待用户审核  
**触发**：用户反馈「Windows 版工业垃圾，分镜素材库调整时间卡的要死」+「按商业 toC app 标准审视整个 app」

---

## 1. 背景与问题

### 1.1 当前架构问题

Windows 版（`Mixed_cut_windows`）大部分视图采用**手工 build UI 树**模式：

```csharp
private void RefreshContent() {
    ContentPanel.Children.Clear();                // 全量清空
    foreach (var group in _vm.GroupedSegments()) {
        ContentPanel.Children.Add(BuildGroupSection(group));   // 重新 new 所有 Border/Player/...
    }
}
```

任何用户交互（包括点 `±0.1s`、选中卡片、切换语义类型）都调用 `RefreshContent()`，导致：

- **31 张分镜卡片每次重建** → 重新 `new InlineVideoPlayer`（含 `MediaElement` 初始化）、`new ContextMenu`、多个 `new SolidColorBrush`、所有 hover/click handler 闭包重新分配
- 用户感知：点击 `+0.1s` 卡顿 800-1500ms，连点几次直接锁死
- GC 压力大，长时间使用后 UI 越来越慢

### 1.2 调用频度统计

`SegmentLibraryView.xaml.cs` 中 `RefreshContent()` 被调用 **23 次**，覆盖所有高频路径：

| 触发场景 | 行号 | 频次 |
|---|---|---|
| ±0.1s 时间调整 | 756, 793 | **极高（连点）** |
| TextBox commit | 807 | 高 |
| 卡片点击选中 | 1079 | 高 |
| QuickEdit 改语义/位置 | 828, 844 | 中 |
| 多选 toggle/全选/反选 | 192-227 | 中 |

### 1.3 为什么必须重构

按商业级 toC 桌面应用（剪映 Windows / CapCut / DaVinci Resolve / PowerDirector）的标准：
- ✅ 单次交互响应 < 16ms（一帧）
- ✅ 长列表必须虚拟化
- ✅ 主题/暗黑模式切换无需重启
- ✅ 平滑过渡动画 60fps

当前 Windows 版做不到任何一项。架构债不还，后续每个新功能都会让性能更差。

---

## 2. 目标 & 非目标

### 2.1 目标（量化）

| # | 目标 | 验收阈值 | 测量方法 |
|---|---|---|---|
| 1 | ±0.1s 调整时间响应 | 100 卡场景，连点 30 次 p99 ≤ 16ms | DispatcherTimer + Stopwatch on render loop |
| 2 | 长列表滚动平滑 | 500 卡滚动平均 ≥ 50fps，p95 帧间隔 ≤ 20ms | `CompositionTarget.Rendering` 间隔采样 |
| 3 | 极限列表可用 | 1000 卡列表可滚动可交互（≥ 30fps） | 同上 |
| 4 | 切选中无可见延迟 | 50 次切换平均 ≤ 8ms | 同 1 |
| 5 | 主题资源化 | 0 处硬编码 hex 色（grep 验证） | `grep -r '#[0-9A-Fa-f]\{6\}' Views/*.xaml` 仅在 Theme/*.xaml 出现 |
| 6 | 暗黑模式切换 | 切换瞬时（< 200ms），全视图 0 残留旧色 | 手测 + screenshot diff |
| 7 | 过渡动画 | 工作区切换 320ms ease-out fade、hover/选中 ColorAnimation 200ms | 视频录帧检查 |
| 8 | 内存稳定 | 切换 100 次项目后内存增量 < 50MB | dotMemory snapshot |

### 2.2 非目标

- ❌ 不改 Service 层（ASR / AI / Export 已 OK）
- ❌ 不改 Model 层（已对齐 Mac）
- ❌ 不动 Database schema
- ❌ 不重写 Whisper / FFmpeg 流水线
- ❌ 不引入新的 MVVM 框架（继续用 CommunityToolkit.Mvvm）

---

## 3. 架构总览

### 3.1 三层分离

```
┌────────────────────────────────────────────────────────┐
│  View 层（XAML 声明式）                                │
│  - DataTemplate 定义每种数据如何渲染                   │
│  - Style 统一外观                                      │
│  - DataTrigger / Storyboard 处理状态切换 + 动画        │
│  - 唯一允许的 code-behind：事件路由 + 复杂手势         │
└────────────────────────────────────────────────────────┘
              ↑ Binding（双向） + Command
              │
┌────────────────────────────────────────────────────────┐
│  ViewModel 层（C#，INPC）                              │
│  - 每张卡片 = 一个 CardViewModel 实例                 │
│  - [ObservableProperty] 暴露绑定字段                  │
│  - [RelayCommand] 暴露 XAML 可调用命令                │
│  - 持有 Service 引用，但不持有 UI 引用                │
└────────────────────────────────────────────────────────┘
              ↑ async + DbContextFactory
              │
┌────────────────────────────────────────────────────────┐
│  Service / Model 层（不变）                            │
└────────────────────────────────────────────────────────┘
```

### 3.2 关键原则

1. **VM 永不引用 UI 元素**。VM 是纯数据 + 命令。
2. **View 永不直接读 Model**。View 只跟自己的 VM 对话。
3. **ObservableCollection<VM>** 用于所有列表，WPF 自动 diff。
4. **VirtualizingStackPanel + Recycling**：所有 > 20 项列表强制开启虚拟化（决策 5 已反转，见 §12）。
5. **资源全局化**：颜色 / 字号 / 间距 / 按钮样式 全部 ResourceDictionary。
6. **Container 状态隔离**：所有视觉状态（IsSelected/IsHovering/IsBoundaryRowVisible）必须存在 VM 里，**不能存在 ItemContainer 自身**（虚拟化复用会污染）。
7. **DataTemplate 内 ContextMenu** 通过 `Tag={Binding}` 上传 DataContext，菜单项用 `{Binding PlacementTarget.Tag.XxxCommand, RelativeSource={RelativeSource AncestorType=ContextMenu}}` 引用。
8. **跨线程 ObservableCollection** 必须用 `BindingOperations.EnableCollectionSynchronization` 注册一把锁，或全部走 Dispatcher.Invoke。

---

## 4. 组件分解

### 4.1 新增 ViewModel

| VM | 职责 | 主要字段 |
|---|---|---|
| `SegmentCardViewModel` | 单张分镜卡片状态 | StartTime, EndTime, IsSelected, IsHovering, IsCheckedInMultiSelect, TranscriptText, QualityScore, SemanticTags, PositionType, ThumbnailSource, IsBoundaryRowVisible, AdjustStartCommand, AdjustEndCommand, CopyTranscriptCommand, DeleteCommand |
| `VideoGroupViewModel` | 按视频分组的容器 | Video, Header, ObservableCollection<SegmentCardViewModel> |
| `SchemeRowViewModel` | 方案变体行 | Name, EstimatedDuration, IsSelected, CopyNameCommand, DeleteCommand |
| `StrategyGroupViewModel` | 策略分组 | Name, Style, ObservableCollection<SchemeRowViewModel>, IsExpanded |
| `VideoCardViewModel` | Overview / Import 视频卡片（已部分存在于 ImportView.VideoRow，提取共享） | Video, IsBusy, ProgressOverall, StageLabel, Sentences, ... |

### 4.2 改造现有 ViewModel

| VM | 新增 | 删除 |
|---|---|---|
| `SegmentLibraryViewModel` | `ObservableCollection<VideoGroupViewModel> Groups`、`Save(SegmentCardViewModel)` 异步持久化方法 | `GroupedSegments()` / `FilteredSegments` 等返回 List 的方法（被 ObservableCollection 替代） |
| `SchemeViewModel` | `ObservableCollection<StrategyGroupViewModel> StrategyGroups` | 类似 |
| `ProjectViewModel` | `ObservableCollection<VideoCardViewModel> VideoCards` | — |

### 4.3 重写 View

| View | 当前规模 | 重写后 |
|---|---|---|
| `SegmentLibraryView.xaml` | 124 行（简陋） | ~250 行（完整 DataTemplate） |
| `SegmentLibraryView.xaml.cs` | 882 行（手工 build） | ~120 行（仅快捷键路由 + IProjectView） |
| `SchemesView.xaml` | 81 行 | ~200 行 |
| `SchemesView.xaml.cs` | 835 行 | ~150 行 |
| `ProjectOverviewView` 系列 | 145 + 199 行 | ~200 + 60 行 |

### 4.4 新增资源（App 级）

```
src/MixCut/Resources/
├── Theme/
│   ├── Brushes.xaml         (颜色 token：AccentBlue, Success, Warning, Danger, BgPrimary, ...)
│   ├── Typography.xaml      (字号 token：H1/H2/H3, Body, Caption)
│   ├── Spacing.xaml         (间距 token：Xs/Sm/Md/Lg/Xl)
│   ├── Styles.xaml          (Button/TextBox/Card/Badge 等组件 Style)
│   ├── Light.xaml           (浅色主题资源覆盖)
│   └── Dark.xaml            (暗黑主题资源覆盖)
└── Templates/
    ├── SegmentCardTemplate.xaml
    ├── VideoGroupTemplate.xaml
    └── SchemeRowTemplate.xaml
```

### 4.5 新增 Converter

| Converter | 用途 |
|---|---|
| `QualityScoreToBrushConverter` | score → 4 色 brush |
| `SemanticTypeToBrushConverter` | enum → 11 色 brush |
| `PositionTypeToLabelConverter` | enum → 「开头/中间/结尾」 |
| `TimeSpanToDurationStringConverter` | double → "10.8s" |
| `NullToVisibilityConverter` | null → Collapsed |
| `EmptyCollectionToVisibilityConverter` | Count==0 → Collapsed |
| `InverseBoolToVisibilityConverter` | true → Collapsed |
| `BoolToHoverBackgroundConverter` | IsHovering → BrushKey |

---

## 5. 数据流

### 5.1 用户调整时间（最关键路径）

```
[用户] 点击 SegmentCard 的 "+" 按钮（XAML Button）
   ↓ Command="{Binding AdjustStartTimePlusCommand}" CommandParameter="0.1"
[SegmentCardViewModel.AdjustStartTimePlusCommand]
   ↓ this.StartTime += 0.1
   ↓ [ObservableProperty] 自动触发 PropertyChanged("StartTime")
   ↓ [NotifyPropertyChangedFor(nameof(DurationDisplay))] 触发 DurationDisplay
[WPF Binding Engine]
   ↓ 仅刷新绑定 StartTime / DurationDisplay 的 TextBlock
   ↓ 其他 30 张卡片完全不参与
[后台]
   ↓ CardVM.AdjustStartTimePlusCommand 末尾 await _parent.PersistAsync(this)
   ↓ DbContextFactory.CreateDbContext → 异步保存 → Dispose
```

**关键：用户感知的延迟 ≤ 1 帧（16ms），持久化在后台。**

### 5.2 用户切换选中卡片

```
[用户] MouseLeftButtonDown on Card Border
   ↓ 通过 EventTrigger → CallMethodAction（或简单 Command）
[SegmentLibraryViewModel.SelectCard(cardVM)]
   ↓ 之前选中那张：oldSelected.IsSelected = false
   ↓ 新选中：cardVM.IsSelected = true
[WPF Binding]
   ↓ 只有这两张卡片的 BorderBrush 通过 ColorAnimation 平滑过渡
```

### 5.3 列表结构变化（筛选 / 排序 / 删除）

```
[用户] 点击语义筛选 chip 切换 active
   ↓ ChipVM.IsActive 切换
[SegmentLibraryViewModel.ApplyFilter()]
   ↓ 重新计算 _filteredSegments
   ↓ 比对 Groups 内容差异
   ↓ Groups.Remove(...) / Groups.Add(...) / Group.Segments.Move(...)
[ObservableCollection 触发 CollectionChanged]
   ↓ ItemsControl 仅添加/移除受影响的 container
   ↓ VirtualizingStackPanel 复用可见 container
```

---

## 6. 错误处理

### 6.1 数据约束

- **StartTime ≥ EndTime**：CardVM.StartTime setter 检查，若违反 revert + `ToastService.Show("时间起点不能 ≥ 终点", Warning)`
- **StartTime < 0**：clamp 到 0
- **EndTime > Video.Duration**：clamp 到 Duration
- 视觉反馈：违反约束时 TextBox 边框红色闪烁（DataTrigger + Storyboard）

### 6.2 异步持久化失败 + 并发顺序保证

**并发顺序（关键）**：用户连点 ±0.1s 时同一 segment 会产生多个 save Task，必须保证：
- 同一 segment 的 save 顺序执行（后到的 save 看到的是前一次的最终状态）
- 不同 segment 之间可并行

**实现**：
- `SegmentCardViewModel` 内部维护 `SemaphoreSlim(1)` 串行同 segment 的 save
- 或更优：用 **debounce 300ms**（连点 300ms 内只发最后一次 save），同时降 DB 压力
- 推荐组合：先 debounce 300ms，到期后串行写

**失败处理**：
- VM 状态保持新值（用户能继续操作）
- 失败次数 < 3：静默后台重试（间隔 2s / 5s 指数退避）
- 失败次数 = 3：`ToastService.Show("保存失败，请检查磁盘空间", Error)` + 提供"重试"按钮
- 应用退出前 flush 所有未持久化 VM（DispatcherShutdownStarted 钩子）

### 6.3 Binding 错误

- 启用 `PresentationTraceSources.TraceLevel=High`（仅 Debug build）
- App.xaml.cs 注册 `BindingFailed` listener，写入 logs/binding-errors.log
- 关键 binding 使用 `FallbackValue` 避免空白

### 6.4 视频文件缺失

- VideoCardViewModel 构造时 cache `IsVideoFileAvailable: bool`
- 缺失时：缩略图覆盖灰色 + ⚠ 图标 + 文字"原视频丢失"
- 不触发任何 `File.Exists` 的 UI 线程同步 IO

---

## 7. 测试策略

### 7.1 单元测试（必需）

```csharp
// xunit 项目：MixCut.Tests
[Fact]
public void SegmentCardViewModel_AdjustStartTime_PreservesDurationConstraint() {
    var vm = new SegmentCardViewModel(seg, parent);
    vm.StartTime = 5.0;
    vm.EndTime = 10.0;
    vm.AdjustStartTimeCommand.Execute(6.0);   // 合法
    Assert.Equal(6.0, vm.StartTime);
    vm.AdjustStartTimeCommand.Execute(11.0);  // 违反约束
    Assert.Equal(6.0, vm.StartTime);          // 不变
}

[Fact]
public void SegmentCardViewModel_PropertyChanged_OnlyForChangedField() {
    var vm = new SegmentCardViewModel(...);
    var changes = new List<string>();
    vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
    vm.IsSelected = true;
    Assert.Equal(new[] { "IsSelected" }, changes);   // 没有 spurious propagation
}
```

覆盖所有 VM 的：每个 ObservableProperty / 每个 RelayCommand / 每个数据约束。

### 7.2 Converter 测试

每个 Converter 测正向 + 反向（如可逆）+ 边缘值（null、空集合、超出范围）。

### 7.3 性能基准（必需，验收门槛）

WPF 单元测试框架无法直接测渲染耗时（STA + measure/arrange 异步）。改用**应用内 in-app benchmark**：

**A. 交互延迟测量**（运行在真实 App，Debug build 自动开启）

```csharp
// Infrastructure/PerformanceBenchmark.cs
public static class FrameLatencyProbe {
    public static IDisposable BeginMeasure(string scenario) {
        var sw = Stopwatch.StartNew();
        var frameCount = 0;
        EventHandler onRender = (_, _) => frameCount++;
        CompositionTarget.Rendering += onRender;
        return new Disposer(() => {
            CompositionTarget.Rendering -= onRender;
            sw.Stop();
            Log.Info($"[Perf] {scenario}: {sw.ElapsedMilliseconds}ms / {frameCount} frames");
        });
    }
}
// 使用：
// CardVM.AdjustStartTimeCommand 末尾包一层 FrameLatencyProbe.BeginMeasure("adjust_start_time")
```

跑分步骤：
1. Debug build 启动，加载 100 卡/500 卡 fixture（dev mode 提供 seeder）
2. 连点 ±0.1s 30 次，读 log p99 必须 ≤ 16ms
3. 滚动到底，CompositionTarget.Rendering 平均间隔 ≤ 20ms

**B. 滚动 fps 测量**：

绑定到 `ScrollViewer.ScrollChanged` + `CompositionTarget.Rendering`，记录滚动期间 frame interval 直方图。

**C. 内存基准**：

dotMemory 手测，对比 Stage 0 vs Stage 8 同样操作（切换 100 次项目）的 GC heap 增量。

500 卡 < 16ms 不再是硬指标（用 p99 衡量更现实）；100 卡 p99 ≤ 16ms 是硬指标。

### 7.4 集成测试（手动 + 自动化）

E2E checklist（每次 publish 前跑）：
- [ ] 切换项目 A→B→A，分镜列表数据 / 选中态正确
- [ ] 多选 50 张 → 批量删除 → 列表正确更新
- [ ] hover 卡片 350ms 自动播放，离开停
- [ ] ±0.1s 连点 30 次，无卡顿、无内存增长
- [ ] 切到暗黑模式，所有视图颜色正确
- [ ] 滚动到 500 个分镜底部，无掉帧

---

## 8. 迁移路径（阶段化交付）

### Stage 0：基础设施（不破坏现有，可单独 publish）

- 新建 `Resources/Theme/*.xaml`
- 新建 Converter 类
- `App.xaml` 合并 ResourceDictionary
- 现有视图开始引用 `{StaticResource AccentBlueBrush}` 替代硬编码 `#1D6BE5`

**验证**：所有视图视觉无变化，无新 bug。

### Stage 1：SegmentLibrary 重构（主战场，feature flag 保护）

**Feature flag 策略**：
- 新视图取名 `SegmentLibraryViewV2.xaml`（与现有 V1 并存）
- `AppSettings.UseNewSegmentLibrary: bool`（默认 true）
- `MainWindow.GetView()` 按 flag 选 V1 / V2
- V2 验证稳定（一周无回归）→ 删 V1

**任务清单**：
- 新建 `ViewModels/Cards/SegmentCardViewModel.cs`
- 新建 `ViewModels/Cards/VideoGroupViewModel.cs`
- 改造 `SegmentLibraryViewModel` 暴露 ObservableCollection
- 新建 `SegmentLibraryViewV2.xaml` + 配套 cs（~150 行）
- **逐一映射 V1 23 处 RefreshContent 调用点的副作用到 V2**（详见下表）
- 保留 PreviewKeyDown 多选快捷键

**V1 → V2 副作用映射表**（必须逐项实现）：

| V1 调用点 | 触发动作 | 当前副作用 | V2 实现 |
|---|---|---|---|
| 行 46/85/96/102/108/114 | 多选模式 toggle/全选/反选/清空 | 全卡片重建 | 每张 CardVM.IsCheckedInMultiSelect setter |
| 行 140 | 切项目 LoadProject | 重新查询 + 重建 | Groups.Clear + 重新装载 |
| 行 156/192/208/219/227 | 筛选/排序变化 | 全卡片重建 | Groups Diff 更新 |
| 行 309 | 类型 chip click | 全卡片重建 | 同上 |
| 行 756/793 | ±0.1s 按钮 | 全卡片重建 | CardVM.AdjustXxxCommand |
| 行 807 | TextBox commit | 全卡片重建 | CardVM.StartTime = newVal |
| 行 828/844 | QuickEdit 改类型 | 全卡片重建 | CardVM.ToggleSemanticTypeCommand |
| 行 949 | 删除分镜 | 全卡片重建 | Groups[i].Segments.Remove(cardVM) |
| 行 1079 | 选中卡片 | 全卡片重建 | 切换两张卡的 IsSelected |

**保留**：滚动位置（V2 用 ScrollViewer with virtualization，自动保留）、焦点（保留 Window 焦点逻辑）、IProjectView 联动（实现 LoadProject）。

**验证**（每项必过）：
- ✅ ±0.1s 0 卡顿，p99 ≤ 16ms
- ✅ 选中切换无重建
- ✅ hover 自动播放
- ✅ 台词面板
- ✅ 筛选 + 排序
- ✅ 多选 + 批量导出 + 批量删除
- ✅ 右键菜单（复制台词 / Explorer 显示 / 删除）
- ✅ 边界微调 hover/选中才显示
- ✅ Ctrl+A/D/0/Esc 快捷键
- ✅ 切项目所有状态清空

**回滚**：发现回归立即在 Settings 关闭 `UseNewSegmentLibrary` 切回 V1，零停机。

### Stage 2：SchemesView 重构

- 新建 `StrategyGroupViewModel` / `SchemeRowViewModel`
- 重写 `SchemesView.xaml`
- 删除手工 build

### Stage 3：ProjectOverview / Welcome / Export 重构

低频但同样模式重构，提升一致性。

### Stage 4：InlineVideoPlayer 懒加载

- DataTemplate 默认只渲染缩略图 + 时长 badge
- hover 350ms 后才实例化 MediaElement（通过 DataTrigger 切换 ContentPresenter）
- 离开释放（彻底避免 31 个 MediaElement 同时存在）

### Stage 5：缩略图异步加载

- `ThumbnailCache.GetImageAsync(path) → Task<ImageSource>`
- 卡片绑定到 AsyncImageLoader（自定义 MarkupExtension）
- 滚动时用 LowQuality scaling，静止后 HighQuality

### Stage 6：暗黑模式

- 新建 `Dark.xaml`
- `ThemeManager.SwitchTheme(Light/Dark)`
- Settings 加切换 UI
- 持久化到 AppSettings

### Stage 7：动画系统

- 工作区切换：FadeTransition 320ms
- 卡片 hover：Storyboard 控制 BoxShadow + Border
- 选中切换：ColorAnimation BorderBrush
- Toast：滑入 + 淡出

### Stage 8：性能验收

- 跑 500 卡 benchmark
- profile 内存（dotMemory）
- 跑 E2E checklist
- 修剩余 bug

---

## 9. 风险与应对

| # | 风险 | 概率 | 应对 |
|---|---|---|---|
| 1 | WPF Binding 调试痛苦（错误静默） | 高 | 启用 PresentationTraceSources + App.xaml.cs 注册 BindingFailed listener → logs/binding-errors.log |
| 2 | DataTemplate 内 ContextMenu DataContext 失效 | 高 | Border `Tag="{Binding}"`；菜单项 `Command="{Binding PlacementTarget.Tag.XxxCommand, RelativeSource={RelativeSource AncestorType=ContextMenu}}"` |
| 3 | **虚拟化 + InlineVideoPlayer 冲突**（container 复用让 MediaElement 残留旧 segment） | 高 | (a) DataTemplate 不直接放 MediaElement；(b) hover 才创建，离开立刻 Stop+Source=null；(c) `ContentControl.UnloadedBehavior` 强制释放 |
| 4 | **Container recycle 污染**（IsSelected/IsHovering 状态被复用 container 反向带过来） | 高 | 严格遵守原则 6：所有状态存 VM；container 不持有任何状态；`ItemContainerStyle` 用 binding 而非 Setter |
| 5 | 虚拟化 + 拖拽 / hover 自动播放计时器冲突 | 中 | hover 计时器存 VM，container Unloaded 时 dispose timer + cancel pending tasks |
| 6 | **连点 ±0.1s 持久化乱序**（后到 save 覆盖前面状态） | 中 | per-VM SemaphoreSlim + debounce 300ms（详见 §6.2） |
| 7 | Stage 1 改造大，期间无法 publish 新功能 | 中 | feature flag `UseNewSegmentLibrary`，V1/V2 并存，零停机回滚 |
| 8 | 现有功能回归（hover播放/Toast/F2/虚拟化先前的台词面板优化） | 中 | E2E checklist 每 stage 跑一遍（详见 §7.4） |
| 9 | `ObservableCollection` 跨线程修改 → InvalidOperationException | 中 | `BindingOperations.EnableCollectionSynchronization` 注册锁；所有 collection 修改走 Dispatcher.Invoke |
| 10 | 暗黑模式 SystemColors / 第三方组件颜色泄漏 | 低 | 全部 BasedOn StaticResource，不用 SystemColors；MediaElement 背景显式设 |
| 11 | DispatcherShutdownStarted 前 pending save 丢失 | 中 | App.OnExit 阻塞等待所有 CardVM 的 SemaphoreSlim 释放，最长 5s |
| 12 | 性能基准在不同硬件上波动 | 中 | benchmark 仅作开发期参考；用户机定义验收（mlamp@100.112.4.71 这台 Windows） |

---

## 10. 估算（修订后，包含 review 反馈）

| Stage | 工作量 | 可独立 publish | 关键产物 |
|---|---|---|---|
| 0 基础设施 | 0.5 天 | ✅ 是 | Brushes/Typography/Styles/Converter（现有视图引用） |
| 1 SegmentLibrary | **3 天** ⚠ 修订 | ✅ 是（feature flag） | V2 视图 + 全部 23 个副作用映射 + 持久化并发顺序 + benchmark 探针 |
| 2 SchemesView | 1.5 天 | ✅ 是 | StrategyGroup VM + SchemeRow VM + V2 视图 |
| 3 其他视图 | 0.5 天 | ✅ 是 | Overview/Welcome/Export 数据绑定 |
| 4 InlineVideoPlayer 懒加载 | 0.5 天 | ✅ 是 | hover 才挂 MediaElement |
| 5 缩略图异步 | 0.5 天 | ✅ 是 | AsyncImageLoader |
| 6 暗黑模式 | 0.5 天 | ✅ 是 | Dark.xaml + ThemeManager |
| 7 动画系统 | 0.5 天 | ✅ 是 | Storyboard / ColorAnimation |
| 8 性能验收 | 0.5 天 | — | benchmark + profile + E2E checklist |
| **总计** | **7.5 天** | — | 商业级 toC 桌面应用品质 |

**变更说明**：Stage 1 由 1.5 天调整为 3 天 —— review 指出 880 行重写 + 23 处副作用映射 + feature flag + 并发持久化 + benchmark 探针，1.5 天明显低估。

**每个 Stage 完成都 publish + 用户测试。Stage 1 完成后即可感知到「调整时间不卡」，是关键里程碑。**

---

## 11. 后续 / Out of Scope

- 触摸手势 / 平板适配 → 未来版本
- 多窗口 / 分屏编辑 → 未来版本  
- 自定义主题（用户自配颜色）→ 暗黑模式实现后再考虑
- 国际化（i18n）→ Mac 也无，未来对齐

---

## 12. 决策记录

| # | 决策 | 理由 |
|---|---|---|
| 1 | 用 CommunityToolkit.Mvvm 而非 ReactiveUI | 项目已用 Toolkit；ReactiveUI 学习曲线高 |
| 2 | ObservableCollection 而非 ReactiveList | 标准库，无新依赖 |
| 3 | 不引入 Prism / Caliburn | 项目体量小，足够用 Toolkit + 手工 DI |
| 4 | 不重写 InlineVideoPlayer 的播放核心 | MediaElement 性能够；重构限定 lifecycle 管理 |
| ~~5~~ | ~~分镜卡片不用虚拟化（先）~~ | **反转：必须开启虚拟化**。reviewer 指出与「1000 卡可用」验收冲突。配合原则 6 杜绝 container 状态污染 |
| 5b | **VirtualizingStackPanel + Recycling 强制开启** | 商业级 toC 标准；解决 reviewer 提出的冲突 |
| 6 | 暗黑模式仅做颜色切换，不做布局变化 | YAGNI |
| 7 | V1/V2 双视图并存 + feature flag 切换 | 大改不能直接替换，需零停机回滚通道 |
| 8 | 持久化用 debounce 300ms + per-VM SemaphoreSlim | 保证连点场景的顺序 + 降 DB 写入压力 |
| 9 | 性能基准用 in-app probe 而非单元测试 | WPF 渲染异步 STA，单元测试不可达；CompositionTarget.Rendering 是唯一可靠源 |

---

## 终止状态

**这是 design 阶段产物。**  
确认无误后，下一步执行 `writing-plans` skill 生成具体实施计划，再按 stage 顺序实施。
