# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目概述

MixCut Windows 版 —— macOS 原生应用 [MixCut](https://github.com/RoshanGH/mixed_cut) 的 Windows 移植版，
功能完整对齐。面向广告投放团队的 AI 视频混剪工具：导入广告素材，AI 按语义切分镜头并标注类型，
再智能排列组合生成多条差异化混剪广告。

技术栈：**C# + WPF + .NET 8**（`net8.0-windows`），目标系统 Windows 10 及以上（x64）。

## 跨机器开发工作流（关键）

代码在 **Mac** 上编写，构建/运行/测试在一台 **Windows 电脑**上进行，两机通过 Tailscale 组网。

- **Windows 构建机**：`mlamp@100.112.4.71`（Tailscale，主机名 `laptop-rbiugf7r`）
- **SSH 密钥**：`~/.ssh/mixcut_win`（免密）
- **Windows 项目目录**：`C:\Users\mlamp\MixCutWindows`
- **.NET SDK**：`C:\Users\mlamp\dotnet\dotnet.exe`（8.0.x，未加入系统 PATH，用全路径调用）

### 同步代码到 Windows

```bash
scripts/sync.sh
```

### 远程构建（在 Windows 上执行）

```bash
ssh -i ~/.ssh/mixcut_win -o IdentitiesOnly=yes mlamp@100.112.4.71 \
  'C:\Users\mlamp\dotnet\dotnet.exe build C:\Users\mlamp\MixCutWindows\MixCut.sln'
```

> SSH 默认 shell 为 PowerShell。复杂命令用 `powershell -NoProfile -EncodedCommand <base64>`
> （UTF-16LE 编码）避免多层引号转义问题。stderr 会被包成 CLIXML，属正常噪音。

### 本机 Mac 上 syntax-check 构建（SSH 不通时备用）

Mac 上跑了 `brew install dotnet`，可以做语法/类型检查（无法生成可运行 EXE，因为是 WPF Windows 专属）：

```bash
dotnet build src/MixCut/MixCut.csproj -c Release -nologo -v quiet -p:EnableWindowsTargeting=true
```

注意 `-p:EnableWindowsTargeting=true` 必填，否则报 NETSDK1100。这个只能验证编译过，跑不起来 —— 实际 publish + 测试还是要走 Windows 机器。

### 读取运行日志

应用日志写入 Windows 端 `%APPDATA%\MixCut\logs\mixcut-<date>.log`（Serilog 按天滚动）。
排查问题时 SSH 进去读该文件。

## 架构（MVVM + Service Layer，对齐 macOS 版）

```
src/MixCut/
├── App.xaml(.cs)   入口 + 泛型主机（DI / 日志 / 生命周期）
├── Models/         EF Core 实体（对应 SwiftData @Model）
├── ViewModels/     CommunityToolkit.Mvvm ObservableObject
├── Views/          WPF 窗口与用户控件（XAML）
├── Services/       业务逻辑，async/await，无 UI 依赖
├── Utilities/      AppPaths 等工具类
└── Resources/      AI Prompt 模板 + 内置二进制（FFmpeg/Whisper）
```

macOS → Windows 技术映射：SwiftUI→WPF、SwiftData→EF Core 8 + SQLite、
`@Observable`→CommunityToolkit.Mvvm、AVFoundation 播放→LibVLCSharp、
`Process` 调 FFmpeg/Whisper→`System.Diagnostics.Process`。

## 数据目录（Windows）

集中由 `Utilities/AppPaths.cs` 管理，根目录 `%APPDATA%\MixCut\`：
`logs\` 日志、`Videos\{hash}\` 按哈希全局共享的视频、`mixcut.db` SQLite 数据库。

## 移植约定（macOS Swift → Windows C#）

- Swift `actor`/`class` → C# 普通 `class`，`async/await`，方法返回 `Task`，可取消的接 `CancellationToken`
- 日志：构造注入 `ILogger<T>`；服务在 `App.xaml.cs` 的 `ConfigureServices` 登记
- 服务层均为单例；数据访问用 `IDbContextFactory<MixCutDbContext>` 按操作建短上下文
- ViewModel：`CommunityToolkit.Mvvm.ObservableObject` + `[ObservableProperty]`/`[RelayCommand]`，
  集合用 `ObservableCollection<T>`；改动数据后保存并**重新查询集合**刷新 UI
  （对齐 macOS 版 `fetchProjects()`/`applyFilter()` 模式）
- EF 实体保持纯 POCO（不加 INPC）；列表项变更靠重新查询体现
- Prompt 模板、边界优化算法、AI 提示词与 macOS 版逐行对齐，不改逻辑

## 开发阶段

- Phase 0 骨架 ✓ / Phase 1 数据层 ✓ / Phase 2-3 基础设施+服务层 ✓
- 进行中：Phase 4 ViewModel → Phase 5 视图(XAML) → Phase 6 联调 → Phase 7 打包
- Phase 7 需获取 Windows 版 FFmpeg/ffprobe/whisper-cli 二进制放入 `src/MixCut/Resources/bin/`
  （csproj 复制到输出 `bin/`，由 `Infrastructure/BundledBinaries.cs` 定位）

---

## 不要在修改过程中破坏已有功能（最重要 ⚠️⚠️⚠️）

修改任何代码前，**必须先识别该文件/模块已有的功能点**；改完后这些功能必须依然完整可用。**严禁为了修一个问题而误伤相邻功能**。对齐 macOS commit `9a43cd0` + `6530fd6` 的教训。

### 强制 SOP（每次代码改动都必走）
1. **改动前**：通读要改的文件 + 周边文件（相同视图树），用一句话列出"这里能做的事"（点击 / 双击 / 编辑 / 拖拽 / 右键菜单 / 键盘快捷键 / 项目切换联动 等等）。
2. **改动中**：每次修改只针对当前任务，**不要顺手"重构"周边代码**。
3. **改动后**：人工跑一遍受影响的视图（或如不能跑就读 XAML 走一遍渲染逻辑），**逐项验证**第 1 步列出的能力是否都还在。
4. **疑似删改**：不确定某段代码的作用时，先查 `git blame` / `git log`，不要随意删改。

### 已知容易被误伤的功能清单（动相关文件时必须回归验证）
- **素材导入页**：拖拽导入、`+ 选择文件` 按钮、卡片右键菜单（复制文件名 / 在资源管理器中显示 / 删除）、台词面板滚动、卡片内联视频播放（hover 切换播放器/缩略图）
- **分镜素材库**：多选模式（全选/反选/清空）、批量导出（编号 + 命名规则）、批量删除、卡片右键菜单、起点/终点 ±0.5s 微调
- **分镜库 / 项目概览 / 素材导入**：**切换项目时**所有数据必须重新加载（`IProjectView.LoadProject(project)` 是入口，不要绕过）
- **导出**：默认 H.264 硬件加速、分镜导出第一帧不黑屏（`trim filter + setpts`）、批量导出记忆目录
- **应用启动恢复上次项目**：`AppSettings.LastSelectedProjectId`，初始化 MainWindow 时恢复
- **缩略图全局缓存**：`ThumbnailCache.Shared`，不要重新引入 `new BitmapImage()` 直接同步加载
- **Toast / InlineBanner / SkeletonView**：全局共享组件，新视图需要错误/loading 提示时复用
- **侧边栏依赖警告**：API Key / Whisper 模型未配置时的橙色 ⚠ 徽章
- **应用启动崩溃捕获**：`App.xaml.cs` 三个 unhandled exception hook（DispatcherUnhandledException / AppDomain.UnhandledException / TaskScheduler.UnobservedTaskException）
- **ChildProcessTracker**：所有 `Process.Start()` 都必须 `AddProcess()`，否则 MixCut 退出后子进程变孤儿

### 当出现 bug 时不要"修一个改坏三个"
本项目（含 Mac 原版）已多次出现"修一个 bug 把别的功能改坏"。**当改动跨越多个文件 / 改了核心视图时，必须用第 3 步的人工验证兜底**，不要假设 WPF 数据绑定会保护你。

### 切换项目联动（铁律 ⚠️）

所有依赖项目数据的视图必须实现 `IProjectView.LoadProject(Project)` 接口，且：
- **每次 `LoadProject` 调用都重新查询数据**（不要假设上次的 `_segments`/`_strategies` 还有效）
- **重置该视图所有可变状态**（多选、筛选、滚动位置等）—— 例如 `SegmentLibraryView.LoadProject` 必须 `SetSelectionMode(false) + ClearSelection()`
- `MainWindow.UpdateContent` 是唯一调用 `LoadProject` 的入口；不要在视图内部自行监听 `SelectedProject` 变化

不联动的常见症状：从项目 A 切到 B 看到的还是 A 的数据；从 B 切回 A 时多选状态泄露过来；统计数字不刷新。

---

## 自我验证铁律（最重要 ⚠️⚠️⚠️）

**改完代码必须自己先跑通验证，再让用户测试。** 严禁把用户当第一测试者。
本项目反复出现「改完没自验 → 用户打开发现还是坏 → 我再改 → 越改越多 bug」的循环，必须靠流程兜住。

### 标准自验证流水线（已铺好，无脑跑）

1. `dotnet build src/MixCut/MixCut.csproj -c Release -p:EnableWindowsTargeting=true` 过 → `scripts/sync.sh` → SSH 远端 `dotnet publish`
2. SSH 启动 EXE：`Stop-Process MixCut → Start-Process publish\MixCut.exe → Start-Sleep 12`
3. 应用按 `AppSettings.LastNavItem` 自动恢复到上次的视图 → 触发该视图的诊断日志
4. SSH 读日志关键字：`Get-Content <最新log> | Select-String -Pattern "<诊断 tag>"`
5. **看到具体计数/状态全对，且无 WRN/ERR 关联到刚改的功能，才算修好**

### 强制规则

- 编译过 ≠ 跑得起来 ≠ 功能对，**必须看运行日志中的实证**（数字、状态、时间），不能凭直觉
- 改任何关键路径要顺手打一行 `[FooDiag]` / `[FooRepair]` 日志，便于自验证 grep
- 现有诊断 tag：`[ThumbDiag]`（缩略图汇总，应 `missing=0`）、`[ThumbRepair]`（缩略图补齐，应无 WRN）、`[HwProbe]`（硬件加速探测）
- 日志里有 WRN/ERR 跟刚改的功能相关 → **继续排查**，不许"留 TODO 再说"

### 反模式（绝对禁止）

- ❌ 改完只跑 `dotnet build` 看到 0 错误就汇报"完成"
- ❌ 让用户做第一个测试者，等用户报"还是不行"才回头查日志
- ❌ 凭"应该没问题吧"判断功能是否好，不看日志实证

---

## FFmpeg 硬件加速适用范围（已锁死，不要乱改）

| 场景 | hwaccel | 原因 |
|---|---|---|
| 缩略图提取 `-frames:v 1 -y x.jpg` | ❌ 不要加 | hardware frame 不能直接 encode 成 jpg，加上会 0 帧失败 —— **这是用户报「分镜卡片首帧黑屏」的真凶** |
| 场景检测 `-vf scene,showinfo -f null` | ❌ 不要加 | scene filter 是 software filter，需要 hwdownload 才能配合，得不偿失 |
| 视频片段裁剪（mp4 输出） | ✓ encoder 用 NVENC/QSV/AMF | 真正省 CPU 的场景，已通过 `HardwareEncoderProbe` smoke test |

加 hwaccel / 硬件 encoder 前必须先确认：**hardware frame 能不能被下游 filter/encoder 直接消费**。不能就走 software，**不要顺手"统一加上"**。
全局性的"统一优化"（如硬件加速、并发上限、超时时间）必须列出所有调用点逐一验证（参考"不要破坏已有功能"SOP）。

---

## 商用 C 端软件丝滑标准（所有 UI/UX 改动必读）

MixCut 面向广告投放团队，目标是**像剪映/Final Cut Pro 一样丝滑的桌面工具**，不是技术 demo。
每个新功能或修改 PR 都必须达到以下基线，否则不算完成。

### 1. 进度反馈：永远不要让用户看着不动的界面

- **任何耗时 > 1 秒**的操作都必须显示进度（百分比 / spinner / 阶段名）
- **能算百分比的就算**（whisper `--print-progress`、ffmpeg `time=`、HTTP `Content-Length`）；
  算不出的用无限 `IsIndeterminate` 进度条
- **多阶段任务**显示「当前阶段 / 总阶段」（如「2/4 语音识别中 42%」）
- **细到单个视频/单个文件**的进度，不只是整体批次进度

### 2. 状态可见：UI 永远清楚地告诉用户「在干嘛」

- 处理中 / 完成 / 失败 / 已取消 四种状态都要**显著区分**（颜色 + 图标 + 文字）
- 失败必须给**人能看懂的错误描述**（不是 `JsonException at line 42`）+ 重试按钮
- 长时间等待的环节（如 whisper 跑大模型）显示**预估剩余时间**或至少 spinner，不要静默

### 3. 操作反馈：每个点击都有视觉响应

- 按钮 hover/pressed 状态明显
- 卡片 hover 有 subtle 高亮（border / shadow / 微动）
- 拖拽时整个 drop zone 高亮 + 显示「松手以导入」类提示
- 提交/保存/删除等操作完成后用 **Toast / 微动效**告知结果（不要静默成功）

### 4. 防错与可恢复：用户不会被坑

- **进程孤儿**：所有外部子进程（whisper/ffmpeg）必须挂 Job Object，主进程死时一起死（见 `ChildProcessTracker`）
- **崩溃捕获**：`App.xaml.cs` 三个 unhandled exception hook 必须保留，不让窗口神秘消失
- **async void**：所有 `async void` 事件处理器必须 try/catch 包裹全部 body，异常不许逃逸
- **状态恢复**：应用崩溃/强退后，下次启动自动把卡在「分析中」的视频状态重置（见 `ResetStaleAnalyzingStatus`）
- **断点续传**：大文件下载（whisper 模型）支持 HTTP Range，断网重连续传，不重头来

### 5. 性能：UI 永不卡顿

- 长任务**绝对**不在 UI 线程跑，全部 `async/await`
- 列表刷新避免整个 `ItemsSource` 替换（会重建播放器/破坏选中态）；
  ImportView 例外：处理中只在 Phase 切换时重建，进度通过 INPC 更新
- WPF binding 用 `INotifyPropertyChanged` 单字段更新，不要全量重渲染
- 数据库访问用短生命周期 `DbContext`（per-operation），避免阻塞

### 6. 资源调度：不抢自己

- **whisper-cli 全局串行**（`SemaphoreSlim(1)`），独占 CPU，避免互相拖慢导致全部超时
- 单 whisper 进程吃满 `Environment.ProcessorCount` 线程
- whisper 超时**按视频时长动态计算**（`max(5min, duration × 4)`），不写死
- 失败重试 1 次（whisper 偶发 hang，重试往往能过）

### 7. 视觉品质：每个像素都讲究

- 卡片圆角统一 8-10px，间距 8/12/16/24 几个挡，不乱来
- 颜色用预定义（主色 #1D6BE5，绿 #2E8B57，橙 #C06F00，红 #D33A3A）
- 字体大小 10/11/12/13/14 几个挡，不乱来
- 状态切换有过渡动画（fade / scale），不闪现
- 处理中遮罩用半透明 + 居中 spinner + 文字，不是大色块盖住

### 8. 跨平台对齐（针对 Windows 移植）

- macOS 版（[RoshanGH/mixed_cut](https://github.com/RoshanGH/mixed_cut)）是设计参考
- 视觉差异允许（SF Symbol → emoji 等价物），交互流程必须对齐
- Windows 特有的偏离（如 explorer.exe 取代 Finder）必须有合理理由

### 9. 发布工作流

- **publish** = 跑 `dotnet publish` 把 EXE 更新到 `C:\Users\mlamp\MixCutWindows\publish\`，
  是开发循环的常规操作，每次代码改完自动跑（默认行为）
- **发版** = GitHub Release / Gitee Release / 打 tag / 制作安装包，需用户明确确认
- 两者不要混淆

#### 双平台发版铁律（GitHub + Gitee 必须同步）

仓库：
- GitHub：`RoshanGH/mixcut-windows`（gh CLI 已登录，可直接操作）
- Gitee：`jinxiushanhehao/mixcut-windows`（remote 名 `gitee`，SSH 已配置）

每次发版**必须两个平台都推**，国内用户主要走 Gitee 下载（GitHub 慢/被墙）：

1. 改 `csproj` 版本号（Version / AssemblyVersion / FileVersion 三处对齐）
2. `git tag -a vX.Y.Z -m "..."`
3. `git push origin main && git push origin vX.Y.Z`（推 GitHub）
4. `git push gitee main && git push gitee vX.Y.Z`（推 Gitee，**不要忘**）
5. Windows 端 `dotnet publish` → `Compress-Archive` 打 `MixCut-vX.Y.Z-win-x64.zip` → scp 回 Mac
6. `gh release create vX.Y.Z <zip> --repo RoshanGH/mixcut-windows --title ... --notes ...`
7. **Gitee Release**：用 Open API（需 `GITEE_TOKEN` 环境变量，从 https://gitee.com/profile/personal_access_tokens 创建）
   ```bash
   curl -X POST "https://gitee.com/api/v5/repos/jinxiushanhehao/mixcut-windows/releases" \
     -d "access_token=$GITEE_TOKEN&tag_name=vX.Y.Z&name=...&body=...&target_commitish=main"
   # 拿到 release id 后上传附件：
   curl -X POST "https://gitee.com/api/v5/repos/jinxiushanhehao/mixcut-windows/releases/<id>/attach_files" \
     -F "access_token=$GITEE_TOKEN" -F "file=@<zip>"
   ```
   token 不在时，至少推完 main + tag，剩余 Release/zip 附件让用户网页手动建。

反模式：
- ❌ 只发 GitHub 不发 Gitee（国内用户拿不到）
- ❌ 两边 tag 不一致 / 版本号不一致
- ❌ 只 push main 不 push tag（gh release 会找不到 tag）

### 10. 不允许的反模式

- ❌ 把 `dotnet publish` 当作发版门槛挂在用户确认上
- ❌ `Process.Start()` 后不挂 `ChildProcessTracker`（必出孤儿）
- ❌ 写死的超时 / 写死的并发数（要么按 CPU 算，要么按数据规模算）
- ❌ 把 PB 级错误日志原文丢给用户看（必须翻译成人话）
- ❌ 任何视图功能比 macOS 版**少**（除非已开 issue 标记 Windows 版主动废弃）
- ❌ 用户报告问题后只读代码不读运行日志
