# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目概述

MixCut Windows 版 —— macOS 原生应用 [MixCut](https://github.com/RoshanGH/mixed_cut) 的 Windows 移植版，
功能完整对齐。面向广告投放团队的 AI 视频混剪工具：导入广告素材，AI 按语义切分镜头并标注类型，
再智能排列组合生成多条差异化混剪广告。

技术栈：**C# + WPF + .NET 8**（`net8.0-windows`），目标系统 Windows 10 及以上（x64）。

---

## 🥇 最高原则：商业化 ToC 软件标准（一切环节的总闸 ⚠️⚠️⚠️）

**MixCut Windows 不是工程 demo、不是内部工具、不是「能跑就行」的 MVP。它是一款最终要卖给真实付费用户的 ToC 桌面软件，每一个环节都必须按商业化产品的标准来要求。**

> 「无论是哪一个功能，无论写哪一个文档，最终目标是一定要以一个商业化 ToC 软件的要求来要求每一个环节。」—— 项目用户原话（2026-05-29）

### 适用范围（每一个环节都要套用这把尺）

| 环节 | 商业化 ToC 标准的具体含义 |
|---|---|
| **新功能** | 必须达到本文件 §「商用 C 端软件丝滑标准」全部 10 条；否则不算完成 |
| **Bug 修复** | 修一个不能改坏三个；必须自验证再让用户测；故障描述要翻译成人话 |
| **UI / 交互** | 像剪映 / Final Cut Pro 一样丝滑，没有静默成功 / 静默失败 / 卡顿 / 神秘消失的窗口 |
| **错误提示** | 不许把 stack trace / JsonException / ExitCode=-XXX 直接丢用户看；必须翻译为人能看懂的描述 + 重试入口 |
| **进度反馈** | 任何 >1s 的操作必须有可视进度；多阶段任务显示「N/M 当前阶段」；细到单个视频/单个文件 |
| **依赖下载** | 国内国外都能用 —— 没国内可访问源就不做依赖下载化（用打包代替）|
| **安装包** | 装上即跑，不挑机器；不能依赖目标机器装过 VS / VC++ Redist / .NET SDK |
| **文档（README / Release notes / Issues）** | 用户读得懂；不堆英文报错；Gitee / GitHub 各自当独立发布渠道，不互相引导 |
| **发版 notes** | 用户视角讲「新增了什么 / 修了什么 / 怎么下载」；不是 commit log 复制粘贴 |
| **代码注释 / 错误码** | 给未来维护者读 —— 解释为什么这么做，而不是这是什么 |
| **测试与回归** | 改完自验证再让用户测（详见 §「自我验证铁律」）；发版前在干净环境跑完整 e2e |
| **issue / PR 描述** | 让 Windows 端 / Mac 端 / 国内代理跑步的工程师都能照着复现 + 实施 |

### 判定原则（每次完成任务前先自问）

1. **这个完成度，敢卖钱吗？** 不敢 → 没完成。
2. **真实用户首次打开就能用吗？** 需要他改注册表 / 装 VC Redist / 加白名单 → 没完成。
3. **国内国外用户都能正常下载与更新吗？** 一边断了 → 没完成。
4. **如果是我自己付了 199 元下载的，我会满意这一段体验吗？** 不会 → 没完成。
5. **如果出错了，用户看屏幕能不能知道发生了什么 + 下一步怎么办？** 不能 → 没完成。

### 红线（任何一条踩中即视为不达标）

- ❌ 发版前没在干净 Win 10/11 环境跑完整 e2e（导入 → ASR → 切分 → 生成方案 → 导出 → 播放）
- ❌ 任何用户面板上能看到的英文 stack trace / 原生报错码
- ❌ 「能跑通」就汇报完成（必须达到 §「商用 C 端软件丝滑标准」基线）
- ❌ 国内 Gitee 渠道引导用户去 GitHub 下载
- ❌ Release notes / issue 描述写得像 commit log，不是给最终用户看的
- ❌ 任何「让用户当第一测试者」的偷懒做法

**本节是整个 CLAUDE.md 的总闸 —— 当下游章节的规则与本节冲突时，本节优先。当下游规则需要解释「为什么这么严」，回到本节查就行。**

---

## 🪟 目标平台铁律：Windows 10 + Windows 11 双系统兼容（必须 ⚠️⚠️⚠️）

**本项目目标用户机器是 Windows 10 和 Windows 11（x64）。任何改动、任何依赖、任何发版都必须保证：用户下载安装包 → 双击 setup.exe → 装完即用，Win 10 / Win 11 双平台都不允许出现「装完不能跑」「需要装别的东西」「需要改注册表」「需要装防火墙白名单」等情况。**

> 「这个项目要兼容 Win 10 系统和 Win 11 系统」—— 项目用户原话（2026-05-29）

### 支持范围

| 系统 | 最低版本 | 架构 | 状态 |
|---|---|---|---|
| **Windows 10** | 1809（17763）及以上 | x64 | ✅ 必须支持 |
| **Windows 11** | 全部版本 | x64 | ✅ 必须支持 |
| Windows 10 1809 之前 | n/a | n/a | ❌ 不支持（.NET 8 硬性下限） |
| Windows 7 / 8 / 8.1 | n/a | n/a | ❌ 不支持 |
| Windows ARM64 | n/a | n/a | ❌ 暂不支持 |
| Windows 10/11 N 版（不含 Media Pack） | n/a | n/a | ⚠️ 自带 LibVLC 后已不依赖 Windows Media Foundation，理论上能跑，发版前需在 N 版上跑一遍 e2e |

> .NET 8 官方最低支持 Win 10 1809。低于此版本系统市占率 < 1%，不在支持范围。

### 装上即跑（铁律）

用户首次安装后，**必须**不依赖以下任何外部条件就能跑全部核心功能：

| 项 | 必须自带 | 不依赖用户机器有 |
|---|---|---|
| .NET 8 Runtime | ✅ 已 self-contained 打包 | ❌ 不要求用户装 .NET Desktop Runtime |
| VC++ Redistributable (2015-2022) | ✅ publish/bin/ 已含 6 个 VC Runtime DLL | ❌ 不要求用户装 VC++ Redist |
| OpenMP runtime (vcomp140) | ✅ publish/bin/ 已含 | ❌ 不要求用户装 Office |
| FFmpeg / ffprobe / whisper-cli | ✅ publish/bin/ 已含 | ❌ 不要求用户装 FFmpeg |
| LibVLC + 365 个解码插件（v0.6.0+） | ✅ publish/libvlc/win-x64/ 已含 | ❌ 不要求用户装 VLC Player |
| Whisper 语音模型（按需下载） | ⚠️ 首次用 ASR 时下载 | ✅ 应用内有下载进度 UI + 国内镜像源 |

发版前自检 grep 关键字：`[VcRuntimeDiag] all 6 VC Runtime DLLs present`、`[VlcDiag] libvlc=... ok`、`[EnvDiag] pass=True`。

### 发版前 Win 10/11 双平台兼容性 checklist

**每次改动引入新 native 依赖（LibVLC、ffmpeg 升级、whisper 升级等）必须跑完**：

- [ ] **静态分析**：对每个**新引入的 native dll** 跑 PE import 表扫描（`Get-PeImports`），列所有依赖 dll → 对比 publish/bin/ 和 publish/libvlc/ 是否完备 → 缺啥补啥（参考 v0.4.1 vcomp140 沉淀方法）
- [ ] **构建机自验**：远端 publish + 启动 + grep 启动期诊断日志 `[VcRuntimeDiag]` `[VlcDiag]` `[EnvDiag]` 全绿
- [ ] **干净 Win 10 e2e**（**有干净机时必跑**）：装安装包 → 启动 → 完整跑导入 → ASR → 切分 → 方案生成 → 导出 → 用系统播放器播一遍
- [ ] **干净 Win 11 e2e**（**有干净机时必跑**）：同上
- [ ] **N 版 Win 10/11 e2e**（**有 N 版机时必跑**）：N 版不含 Windows Media Foundation，确认 LibVLC 走自己的解码栈不撞 mfplat.dll
- [ ] **无干净机时**：dumpbin 静态分析 + 构建机用「新用户账号 + 不继承 VC++ Redist」模拟干净环境跑

### 已知容易撞双平台兼容性的雷区（动相关代码时必查）

| 雷区 | 表现 | 防范 |
|---|---|---|
| **VC Runtime 缺失** | 干净机启动崩，`ExitCode=-1073741515` (`STATUS_DLL_NOT_FOUND`) | v0.3.0 沉淀：必带 6 个 VC Runtime DLL |
| **vcomp140 缺失** | ggml-cpu / 部分 OpenMP 加速代码崩 | v0.4.0 沉淀：必带 vcomp140 + concrt140 |
| **ffmpeg codec-private 选项** | N 卡 / I 卡 / A 卡 不同 GPU 路径行为不一致 | v0.4.0 沉淀：构建机只能测一种 GPU，其它 GPU 路径需用户实测 |
| **WPF MediaElement seek 闪帧** | hover 播放分镜先闪视频第 0 秒 | v0.6.0 沉淀：换 LibVLCSharp，TimeChanged 精确感知 seek 完成 |
| **LibVLC native dll 路径** | hover 即崩 `DllNotFoundException` libvlc | v0.6.0：启动期 `Core.Initialize(libvlcDir)` explicit 路径 + 启动检查弹窗 |
| **LibVLC 365 plugins 缺失** | VLC 报「无解码器」/ 部分视频格式无法播 | v0.6.0：Inno Setup 必须把 publish/libvlc/ 整目录打进安装包 |
| **Windows SmartScreen 拦截** | 首次启动「Windows 已保护你的电脑」拦 | 长期：买 EV 代码签名证书；短期：用户点「仍要运行」 |
| **antivirus 误报** | VLC 被识为 P2P 工具 / whisper-cli 被识为可疑 | 短期容忍；发版 notes 提示用户加白名单 |
| **Win 10 1809 之前** | .NET 8 安装失败 | 不在支持范围，安装包不主动检查（用户极少） |

### 反模式（绝对禁止）

- ❌ 引入新 native 依赖（如换 ffmpeg / 升级 whisper / 加新 codec）后不做 dumpbin 静态分析就发版
- ❌ 假设「构建机能跑 = 用户机能跑」—— 构建机已有 VS / VC++ Redist / .NET SDK，远比干净用户机器宽松
- ❌ 发版前没跑过干净 Win 10 e2e（**有干净机时**）
- ❌ Release notes 写「请先装 VC++ Redist」/「请先装 .NET Runtime」—— 这违反「装上即跑」原则
- ❌ 提示用户「请把 X 加入防火墙白名单」/「请关闭杀软」—— 短期容忍提示但长期必须自己签证书

---

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

#### Issue 只提 GitHub（不提 Gitee）⚠️

**Release 必须双发，但 Issue 仅在 GitHub 提**。这是项目用户明确定的规则（2026-05-29 沉淀）。

工作流：
- 用 `gh issue create --repo RoshanGH/mixcut-windows ...` 提交
- 不需要 `GITEE_TOKEN`，不需要双平台同步
- 不必询问用户「要不要 Gitee 也提一份」

为什么不双提：
- Issue 是工程内部协作品（让工程师照着复现 + 实施），不是发给终端用户看的发布物
- Gitee issue 没有人维护 / 不看，双提会造成两边状态不一致
- 终端用户该看的是 Release notes（双平台同步），不是 issue tracker

边界（不要搞混）：
- ✅ **Release / Release notes**：仍然必须 GitHub + Gitee 双发（§「双平台发版铁律」原文不变）
- ❌ **Issue**：只 GitHub
- 即「**发版给用户看的双发，issue 给自己看的单发**」

反模式：
- ❌ 提完 GitHub issue 还问用户「要不要给我 GITEE_TOKEN 同步到 Gitee」 —— 不要问，直接结束
- ❌ 用 Gitee issue 跟踪 Windows 端工作进度（Gitee 不看）

#### 发版前**干净环境**自测铁律（v0.3.0 事故沉淀 ⚠️）

构建机（`mlamp@100.112.4.71`）装了 VS / .NET SDK / VC++ Redist，**用它自测过 ≠ 用户机能跑**。
v0.3.0 因此踩了 `whisper-cli.exe` 缺 VCRUNTIME140.dll 的坑，用户机器直接报 `ExitCode=-1073741515` (`STATUS_DLL_NOT_FOUND` / `0xC0000135`)。

发版前必查清单：
- 所有内置二进制（whisper-cli / ffmpeg / ffprobe）依赖的 native DLL 是否都在 `publish/bin/` 里
- 6 个 VC Runtime DLL（vcruntime140 / vcruntime140_1 / msvcp140 / msvcp140_1 / msvcp140_2 / concrt140）必须随包分发
- 启动期 `[VcRuntimeDiag]` 日志应输出 `all 6 VC Runtime DLLs present`，缺任意一个立即 WRN
- 如果有干净 Win10/11 测试机（无 VS / 无 VC++ Redist），发版前必跑完整流程：导入 → ASR → AI 切分 → 生成方案 → 导出
- 没有干净测试机时，至少 `dumpbin /imports whisper-cli.exe` 看 import 列表，确认所有 DLL 都已打包

诊断 grep 关键字：`[VcRuntimeDiag]` —— 启动期；`ExitCode=-1073741515` —— 用户机器跑外部进程时撞 DLL 缺失的标志。

#### 外部进程 codec-specific 选项必须真实路径自验证（v0.4.0 事故沉淀 ⚠️）

`HardwareEncoderProbe` 启动期跑的 NVENC/QSV smoke test **只验证「能编 1 帧」**，不能保证我们实际导出命令里的 codec-private 选项被新版 ffmpeg 接受。
v0.4.0 因此踩了 `-allow_sw 0` 的坑 —— gyan.dev ffmpeg 8.1.1 起把这个曾经 NVENC 私有的选项移除，全局解析器拒识，导出全部失败 `exit -1414549496` (`0xABABABAB` "Unrecognized option, Error splitting the argument list: Option not found")。
**关键**：构建机选 QSV 路径，**根本走不到 NVENC 死代码**，所以 smoke test + 启动检查全绿，但 N 卡用户机器一导出就崩。

发版前必跑：
- **真实导出测试**（不是 smoke test）：导入视频 → 生成方案 → **导出 MP4 → 用播放器播一遍**
- 构建机如果只能选某个 codec（如只有 QSV），用 `ffmpeg -h encoder=<codec>` 检查所有 codec-private 选项是否仍有效
- 看实际 `FFmpegRunner` 拼的命令，**每个 codec 私有参数都要在 `ffmpeg -h encoder=<那个 codec>` 输出里能找到**

诊断 grep 关键字：`exit -1414549496` / `Unrecognized option` —— ffmpeg 选项解析失败。

### 10. 不允许的反模式

- ❌ 把 `dotnet publish` 当作发版门槛挂在用户确认上
- ❌ `Process.Start()` 后不挂 `ChildProcessTracker`（必出孤儿）
- ❌ 写死的超时 / 写死的并发数（要么按 CPU 算，要么按数据规模算）
- ❌ 把 PB 级错误日志原文丢给用户看（必须翻译成人话）
- ❌ 任何视图功能比 macOS 版**少**（除非已开 issue 标记 Windows 版主动废弃）
- ❌ 用户报告问题后只读代码不读运行日志

---

## 会话踩坑沉淀（v0.3.0 → v0.5.0）⚠️⚠️⚠️

本节是 v0.3.0 首发 → v0.5.0 期间累积的全部教训。下次开新会话上下文清零后，**先 grep 这一节**避免重蹈覆辙。

### A. 用户定的不可妥协原则

| # | 原则 | 用户原话 / 背景 |
|---|---|---|
| 1 | **「装完即跑」** —— 不管目标机器原本什么样，安装包装上就能用全部核心功能 | 「不管它本身是什么样子，它只要用我的安装包安装了这个东西，要保证这个东西能正常的运行」|
| 2 | **真正自验证再让用户测** —— 用户拒绝当第一测试者 | 「首帧黑屏 这个问题你每次改完自己能不能 验证 你已经让我验证 很多了」|
| 3 | **构建机不是用户机器** —— 远程构建机能跑 ≠ 用户机器能跑 | 「你自己脸上 windows 服务器 测试不行吗」（提醒可用构建机做静态分析 + 实跑） |
| 4 | **依赖下载源必须国内可访问** —— 没 GitHub raw / HuggingFace / gyan.dev 直链 | 「我说的是这个软件安装好了以后，我去下载它的依赖，这些依赖要用国内的链接去下载」|
| 5 | **没国内源就不做依赖下载化** —— 不要为了瘦身把功能搞复杂 | 「没有的话那就不做这个功能了，那就依然用现在的打包方式」|
| 6 | **Gitee 当独立发布渠道** —— 不引导用户去 GitHub | 「你就当 两个是独立 发布 该怎么写怎么写」|

### B. 具体坑 + 错误码速查表（按版本排序）

| 版本 | 症状 | 错误码 | 根因 | 修复 |
|---|---|---|---|---|
| v0.3.0 | whisper-cli 在干净 Win 上崩 | `ExitCode=-1073741515` `0xC0000135 STATUS_DLL_NOT_FOUND` | 没打包 VC++ Runtime DLL；构建机装了 VC++ Redist 自测看不出 | v0.3.1 打包 6 个 VC Runtime DLL |
| v0.4.0 | ggml-cpu 启动期可能崩 | `0xC0000135` | `ggml-cpu.dll` 依赖 **vcomp140.dll** (OpenMP runtime)，干净 Win 没有 | v0.4.0 起补 vcomp140 + concrt140 |
| v0.4.0 | N 卡机器导出 0/30 全失败 | `exit -1414549496` `0xABABABAB Unrecognized option 'allow_sw'` | `FFmpegRunner` 加 `-allow_sw 0`，gyan.dev ffmpeg 8.1.1 移除该选项；**构建机选 QSV 路径根本走不到 NVENC 死代码**，潜伏 v0.3.x 全期 | v0.4.1 删 NVENC `-allow_sw 0`|
| v0.4.x | 分析完分镜库没分镜，重启才有 | n/a | `MainWindow._viewLastLoadedProjectId` 缓存「同 project 不重 LoadProject」，性能优化把「数据变更要刷新」case 搞坏 | v0.5.0 `ImportViewModel.SegmentsChanged` 事件 + `MainWindow.OnSegmentsChanged` 失效缓存 |

### C. 容易被构建机隐藏的隐患（关键盲点）

构建机 `mlamp@100.112.4.71` 是 **Win11 Pro Build 26200**，装了 .NET SDK + VC++ Redist + 集成显卡（Intel 核显）。**这些是构建机有但干净用户机可能没有的东西**：

| 构建机有 | 用户机可能没有 | 不在构建机自测能踩的坑 |
|---|---|---|
| Visual C++ Redistributable 14.x | 没装过 VS / VC++ Redist 的全新 Win | VC Runtime DLL 全部缺失（v0.3.0 坑）|
| Office / Excel | 不装 Office 的纯净系统 | `vcomp140.dll` 等 OpenMP runtime 缺失（v0.4.0 坑）|
| **Intel 核显（QSV）** | NVIDIA 独显（NVENC）/ AMD 独显（AMF）/ 无 GPU | **NVENC / AMF codec-specific option 死代码**（v0.4.0 -allow_sw 坑）|
| .NET 8 Runtime（手动装的）| 大多数普通用户没装 | 启动直接弹「需安装 .NET Desktop Runtime」（v0.4.0 起 self-contained 已解决）|

**死规矩**：改任何**有 GPU 厂商分支**的代码（HwProbe / FFmpegRunner 编码路径 / 解码 hwaccel），必须告诉用户「构建机只能测 QSV 路径，N 卡 / A 卡路径需要你在对应硬件机器上跑一遍真实导出」。

### D. 容易被构建机抓到的工具（自验证武器库）

1. **PE 文件 import 表静态分析**（v0.4.1 用这方法发现 vcomp140 漏网）
   - 用 PowerShell 读 PE 文件二进制，正则 `[\w\-\.]+\.dll` 抓所有 import
   - 流程：`Get-PeImports whisper-cli.exe` → 看依赖什么 → 对比 publish/bin 看缺啥
   - **比"启动跑通"更可靠**：能发现潜伏的 native DLL 依赖

2. **ffmpeg encoder 私有选项查询**（v0.4.1 验证 `-allow_sw` 是否还有效）
   - `ffmpeg -h encoder=h264_nvenc | grep <option_name>`
   - 改 codec 选项前先 query，不靠"以前能用"假设

3. **实跑测试用例**（不只看启动 EnvDiag）
   - 任何 GPU/编码相关改动 → 跑 5-30 秒真实导出（`ffmpeg -f lavfi -i testsrc=...`）
   - 任何 ASR 改动 → 跑一个 5 秒 silence.wav 看 whisper 加载流程
   - 任何 import 流程改动 → 端到端跑导入 + 分析

### E. Gitee 独立发布原则（v0.5.0 沉淀）

Gitee Release 单文件 **100MB 上限**。我们安装包 101 MB / zip 137 MB **超线**。
**不能在 Gitee Release notes 引导用户去 GitHub**（违反「独立发布」原则）。

唯一可行方案：**Inno Setup DiskSpanning 分卷**：
```
DiskSpanning=yes
DiskSliceSize=94371840   ; 90 MB 留余量
SlicesPerDisk=1
```
输出：`Setup.exe`（stub ~2 MB）+ `Setup-1.bin` (~88 MB) + `Setup-2.bin` (~11 MB)
用户下 3 个文件放同目录双击 setup.exe 即装。

**`scripts/sync.sh` 必须含 `installer/` 和 `scripts/`**（否则 .iss 不会被同步到构建机，iscc 编译报「找不到文件」）。

### F. 「数据变更 → UI 刷新」通道清单（不要再破坏）

`MainWindow.UpdateContent` 的 nav 切换性能优化使用 `_viewLastLoadedProjectId` 缓存。**任何会修改 segments / schemes / videos / project status 的 DB 写入必须广播事件失效对应缓存**：

| DB 写入来源 | 触发事件 | 应失效缓存 |
|---|---|---|
| `ImportViewModel` 视频导入 | `VideoListChanged` | 走 `RefreshAfterProjectChange`（已有） |
| `ImportViewModel` 视频分析完成 | `SegmentsChanged` (v0.5.0 新增) | `SegmentLibrary` / `Schemes` / `Overview` |
| ⚠️ 未来：`SchemeViewModel` 生成新方案 | 应加 `SchemesChanged` | `Schemes` / `Overview` / `Export` |
| ⚠️ 未来：`ExportService` 完成导出 | 应加 `ExportFinished` | `Overview`（状态更新） |
| ⚠️ 未来：删除单个 segment | 应加 `SegmentDeleted` | `SegmentLibrary` 自身刷新（同 view 内）|

### G. GPU 感知设计原则（v0.5.0 沉淀）

并发数 magic number **不要散落在多处**（之前 `ImportViewModel` / `ExportView` / `SettingsWindow` 三个地方各算各的）。统一走 `Infrastructure/ConcurrencyPolicy`：
- `MaxAnalyzeConcurrency(videosCount)` —— 分析（whisper + ffmpeg 场景检测）
- `MaxExportConcurrency(tasksCount)` —— 导出（ffmpeg 编码）
- `ExplainExportFormula()` / `ExplainAnalyzeFormula()` —— Settings UI 透明拆解显示

**有 GPU 编码 +3 路（封顶 11）**，**有 GPU 解码 +1 路（封顶 4）**，**无 GPU 维持原 CPU 公式不变**。

Settings → 系统信息必须显示：
- `HardwareEncoderProbe.HardwareDescription` —— 编码加速友好名
- `HardwareEncoderProbe.DecodeHwaccelDescription` —— 解码加速友好名
- `HardwareEncoderProbe.WhisperBackendDescription` —— Whisper 后端
- `ConcurrencyPolicy.Explain*Formula()` —— 让用户看到「10 路（CPU 7 + GPU 加成 +3，上限 11）」

### H. 当前在用的 Gitee Token 处理

私有令牌**用完即焚**：发版完成后立刻让用户去 https://gitee.com/profile/personal_access_tokens 删除。本会话 token `0872e38d1d661a071dca68076e49d085` 跨多次 release 复用过；下次发版前要让用户重新生成。
**永远不要把 token 写进代码 / commit / 长期日志**。
