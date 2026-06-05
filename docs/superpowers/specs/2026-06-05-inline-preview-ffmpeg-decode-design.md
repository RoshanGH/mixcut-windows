# 设计文档：hover 预览统一到自带 FFmpeg 解码（消灭 0xC00D109B）

- 日期：2026-06-05
- 状态：设计待评审
- 出发点：CLAUDE.md §「兼容性总纲：核心能力自带·自控·与系统解耦」
- 关联记忆：`compatibility_first_principle`

---

## 1. 背景与问题

用户反馈：部分视频 hover 预览弹「视频播放失败：`0xC00D109B` 该格式可能需要系统媒体编解码支持」。

**根因**：`src/MixCut/Views/Components/InlineVideoPlayer.xaml.cs` 用 WPF `MediaElement`，底层走 **Windows Media Foundation（系统编解码器）**。而 Win10/11 默认**不带 HEVC/H.265 解码器**（需用户去微软商店装付费「HEVC 视频扩展」），N 版 Windows 连 H.264 都可能缺。iPhone「高效」格式、众多安卓机拍摄的素材都是 HEVC → 一播就报错。

**这是全工程唯一违反 §兼容性总纲推论 2「与系统解耦」的一环**：导出（`FFmpegRunner`）、缩略图都走自带 FFmpeg，HEVC 素材照常处理；唯独 hover 预览掉队，仍赌用户机器的系统编解码器。

**历史**：v0.6.0 曾用 LibVLCSharp 替换（方向对），但实现写错——每次 hover 重建整个 VLC 生命周期，UI 线程同步阻塞卡死 0.5-2s；v0.6.1 退回 MediaElement「接受不能预览」。教训：实现 bug（per-hover 重建卡死）不该用「放弃正确架构」来修。

## 2. 目标与非目标

### 目标
- hover 预览**不再依赖任何系统编解码器**，HEVC/VP9/AV1 等全格式可播，N 版 Windows 亦可播。
- 流畅度达 §商用丝滑标准（连续 hover 多张卡片不卡死、不闪帧）。
- 「装上即跑」不破坏；安装包体积可控；闭源商用授权合规。
- `InlineVideoPlayer` 现有全部能力零回归（见 §6）。

### 非目标
- 不改导出/缩略图链路（它们已合规）。
- 不引入主预览大窗（本次只统一 inline 卡片预览）。
- 不做依赖在线下载（无国内源就打包，遵循既有原则）。

## 3. 技术选型（2026-06-05 spike 后定稿 ⚠️ 已从 FFME 改为进程外渲染器）

**采用「进程外 ffmpeg.exe 裸帧渲染器」：复用现有内置 ffmpeg.exe 解码，裸帧管道 → WriteableBitmap 渲染，音频走 WASAPI；不引入任何进程内 FFmpeg 库。**

### 为何不是最初设计的 FFME（spike 实证三杀）
2026-06-05 在构建机做 Task 0 spike，FFME 被以下硬事实否决：
1. **授权红线**：FFME 进程内链接 FFmpeg，必须用 **LGPL** 构建；而 GPL full 构建（115MB）进程内链接会传染 → 强制 MixCut 整体开源，**付费闭源产品不可接受**。
2. **取库无门**：FFME 4.4.350 死绑 ffmpeg **4.4** ABI（`avcodec-58`），BtbN 只提供 7.1/8.1/master 的 LGPL 共享库，**无现成 LGPL 4.4**；自编 ffmpeg 4.4 LGPL 工具链又重又脆。
3. **体积反噬**：任何进程内方案（FFME/Flyleaf/libmpv）都要再塞一整套 ffmpeg 媒体栈（LGPL 7.1=59MB / 8.1=84MB 压缩包，解包 +40~60MB），把安装包顶到 ~100MB，**重撞 Gitee 100MB 单文件线，吐回 v0.6.1 瘦包成果**。

### 进程外方案为何成立
| 维度 | 进程外 ffmpeg.exe 渲染器 |
|---|---|
| 授权（付费产品） | ✅ 进程外调用 GPL **不传染**（mere aggregation，与现有导出 `FFmpegRunner` 同模型，业界通行） |
| 取库 | ✅ 复用已打包的 ffmpeg.exe，无需取任何新库 |
| 体积 | ✅ **零新增**，守住 v0.6.1「装上即跑/小包/Gitee 100MB」 |
| 兼容性（总纲核心） | ✅ ffmpeg 解的全能播，彻底不碰系统编解码器，N 版亦可 |
| 一致性 | ✅✅ 与导出**同一个 exe**，真·所见即所得（总纲推论 3） |
| 风险 | ⚠️ 自研播放器内核，流畅度/音画同步需实测验证（见 §8/§9） |

### 风险缓释（针对「自研内核可能不丝滑」）
- hover 预览是**小尺寸卡片 + 短片段（3~10s）**：裸帧管道带宽极小（卡片尺寸约 320×180×4B×30fps ≈ 7MB/s），短片段音画漂移可忽略。
- 音画同步用「音频为主时钟」标准法（音频连续播，视频帧按 PTS=N/fps 对齐音频播放位置）；短片段即便简单墙钟驱动漂移也不可感。
- **实测门槛**（§8）：构建机真机验证「连续 hover 多卡不卡、HEVC 能播、音画不可感漂移」；**做不丝滑则回退到进程内成熟库（Flyleaf + LGPL 8.1）并接受 +40~60MB 体积**。

### 已否决的备选
- **FFME / 任何进程内 FFmpeg 库**：授权 + 取库 + 体积三杀（见上）；Flyleaf+LGPL8.1 仅作进程外做不丝滑时的体积妥协后备。
- **H.264 代理 + 仍用 MediaElement**：仍依赖系统编解码器（只赌 H.264），N 版失败 → 违反总纲，半成品，否。
- **重引 LibVLC**：+150MB 破 Gitee 100MB 限制 + v0.6.0 卡死阴影 → 否。

## 4. 架构设计

### 4.1 影响面（`InlineVideoPlayer` 被 5 处视图使用）
`ImportView`、`SegmentLibraryView`、`SegmentLibraryViewV2`、`ProjectOverviewView`、`SchemesView`。本次只换 `InlineVideoPlayer` 内部播放内核，**对外 API（`SetVideo`/`SetSegment`/`AutoPlayOnHover`）保持不变**，5 处调用方零改动。

### 4.2 新组件：`FfmpegFramePlayer`（替换 XAML 里的 `MediaElement`）
新建一个 WPF `UserControl`（或承载 `Image` 的轻控件）`FfmpegFramePlayer`，对外暴露与原 `MediaElement` 用到的能力等价的 API（`Open/Play/Pause/Stop/Position/Duration` + `Opened/Ended/Failed` 事件），让 `InlineVideoPlayer` 几乎照原逻辑调用。内部：
- **视频**：`ffmpeg.exe -ss <start> -i <file> -t <dur> -an -f rawvideo -pix_fmt bgra -vf "scale=<W>:<H>:force_original_aspect_ratio=decrease,fps=<fps>" pipe:1` → 后台线程按帧读取（每帧 `W*H*4` 字节）→ `WriteableBitmap` 写像素 → `Image.Source` 显示。
- **音频**（第二条管道）：`ffmpeg.exe -ss <start> -i <file> -t <dur> -vn -f s16le -ar 44100 -ac 2 pipe:1` → WASAPI/`NAudio` 输出，作为主时钟。
- 所有子进程必须 `ChildProcessTracker.AddProcess()`（项目铁律，防孤儿）。

### 4.3 解码栈定位（启动期一次）
- 直接用 `BundledBinaries.Ffmpeg`（现有 `<AppDir>\bin\ffmpeg.exe`），无新增库、无 `Library.FFmpegDirectory` 这类初始化。
- 启动期打 `[FfmeDiag] ffmpeg.exe=<path> exists=<bool>`，便于自验证 grep。

### 4.4 生命周期与同步（**绝不重蹈 v0.6.0 卡死，且要丝滑**）
- 每个 `InlineVideoPlayer` 一个 `FfmpegFramePlayer`；hover 开播=启子进程+起读帧线程，停=`Stop()` kill 子进程+停线程。**所有进程/管道 IO 在后台线程，UI 线程只接收 `WriteableBitmap` 帧（`Dispatcher.Invoke` 短操作）**，绝不在 UI 线程同步阻塞。
- **音画同步**：音频为主时钟；视频帧 PTS=帧序号/fps，当音频播放位置 ≥ 帧 PTS 时呈现该帧；短片段（3~10s）即便简单墙钟驱动漂移亦不可感。
- 全局单播协调（`PlaybackStarted` 事件）逻辑保留。
- 连续 hover 多卡：开播即先 kill 其它实例的子进程（呼应全局单播），避免 N 个 ffmpeg 抢 CPU。

### 4.5 降级（总纲推论 4：降级而非报错）
- 子进程非 0 退出 / 读帧异常 → `Failed` 事件 → **不弹系统错误码**；inline 保留缩略图 + 人话提示「该片段无法预览」+「用系统播放器打开」入口。绝不出现 `0xC00D109B` 字样。

## 5. 兼容性与打包

- **零新增 native 依赖**：复用已打包的 `ffmpeg.exe`，不引入任何 FFmpeg 共享库 / 第三方播放库。
- 唯一可能新增的托管依赖：音频输出库（如 `NAudio`，纯托管，体积可忽略）。若用 WASAPI 原生封装则零依赖。
- 安装包体积**不变**（守住 v0.6.1「装上即跑/小包/Gitee 100MB」），无需 DiskSpanning 变更。
- 不依赖用户机器任何系统编解码器 / 商店扩展 / VLC。

## 6. 不破坏已有功能（铁律 SOP — 换内核后逐项回归）

`InlineVideoPlayer` 当前能力清单（换内核后必须全部依旧可用）：
1. hover 350ms 自动播放（`AutoPlayOnHover=true`，分镜库），离开立即停。
2. `SetVideo`：完整视频播放（项目概览卡片）。
3. `SetSegment`：只播 `startTime→endTime` 区间，到 `endTime` 自动停。
4. 播放中调整区间（±0.1s 微调）→ 按新区间 re-seek，不中断。
5. 全局单播：任一卡片开播，其它正在播的自动停（`PlaybackStarted`）。
6. 缩略图 ↔ 播放态切换（`ThumbnailState`/`PlayingState`）。
7. 时长徽标、进度条拖拽 seek、暂停/播放/停止按钮、当前时间文本。
8. `Unloaded` 时停止并解绑事件（防泄漏/防孤儿）。

## 7. 错误处理
- FFME 初始化失败（共享库缺失）→ 启动诊断 WRN + inline 退回「仅缩略图」，不崩。
- 单文件解码失败 → 人话提示 + 系统播放器入口，绝不暴露错误码。
- 所有 `async void` 事件处理器 try/catch 全包裹（项目铁律）。

## 8. 测试与验证门槛（实施前/发版前必须在构建机过 — §自我验证铁律）

**spike 已过的**（2026-06-05）：FFME×.NET8 兼容性（但因授权/体积否决）、ffmpeg 共享库体积实测（115MB GPL，确认进程内不可行）。

**进程外方案的验证门槛**：
1. **裸帧管道冒烟**：构建机用现有 ffmpeg.exe 跑 `-f rawvideo -pix_fmt bgra` 解一个真实 **HEVC/iPhone 高效格式**片段，确认有正确帧字节流出（不再 0xC00D109B）。
2. **真机渲染丝滑**（核心）：`FfmpegFramePlayer` 在运行的 app 里 hover 播 HEVC，画面正常、**连续 hover 多卡不卡死**、音画漂移不可感。
3. **8 项能力零回归**（见 §6）。
4. **干净 Win10/11 e2e**（有干净机时）：装包 → 导入 HEVC → hover 能播 → 全链路导出。
5. **N 版 e2e**（有 N 版机时）：确认走 ffmpeg.exe 解码不撞 mfplat.dll。
6. 启动诊断 `[FfmeDiag] ffmpeg.exe exists=True`，零 WRN/ERR 关联预览。

## 9. 风险与回退
- **核心风险=自研内核不够丝滑**（音画同步/连续 hover 卡顿）。缓释见 §3「风险缓释」+ §4.4。
- **回退**：若构建机实测 §8.2 做不丝滑 → 改用进程内成熟库 **Flyleaf + BtbN LGPL ffmpeg 8.1 共享库**（丝滑由库保障、LGPL 合规），代价是安装包 +40~60MB（撞 Gitee 100MB 则启用既有 DiskSpanning）。此为体积换确定性的妥协后备。
- 当前 MediaElement 实现保留在 git 历史，新内核以独立 commit 引入，验证不过可一键回退。

## 10. 单元测试
- 播放器内核依赖 native 子进程 + UI，难纯单元测，以**构建机真实 e2e** 为主验证手段。
- 可单元测的纯逻辑：ffmpeg 命令行参数拼接（给定 start/dur/W/H/fps → 期望 args）、帧字节数计算（W*H*4）、音画同步时钟换算（PTS↔帧序号）。这些抽成纯函数便于测。
