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

## 3. 技术选型（已决定，PM 授权直接拍板）

**采用 `Unosquare.FFME`（FFME，FFmpeg MediaElement for WPF）+ LGPL 版 FFmpeg 共享库。**

FFME 是成熟 WPF 播放控件，API 与 `MediaElement` 高度相似（替换成本低），底层挂 FFmpeg 自解码、自渲染，音视频同步/seek 已由库处理（丝滑有保障，规避 v0.6.0 自研内核风险）。

| 维度 | 为何选 FFME |
|---|---|
| 兼容性（总纲核心） | FFmpeg 解的全能播，彻底不碰系统编解码器，N 版亦可 |
| 丝滑 | 成熟库处理 A/V 同步，不自己造播放器内核 |
| 授权（付费产品） | FFmpeg 共享库用 **LGPL 构建**，允许动态链接进闭源商用 app |
| 一致性 | 同 FFmpeg 家族解码，预览与导出画面一致 |

### 已否决的备选
- **H.264 代理 + 仍用 MediaElement**：仍依赖系统编解码器（只赌 H.264），N 版失败 → 违反总纲，半成品，否。
- **自建 ffmpeg.exe 裸帧渲染器**：零体积、授权最干净，但「自研播放器内核」音视频同步风险高，做砸反而违反丝滑标准 → 列为 FFME 走不通时的后备。
- **重引 LibVLC**：+150MB 破 Gitee 100MB 限制 + v0.6.0 卡死阴影 → 否。

## 4. 架构设计

### 4.1 影响面（`InlineVideoPlayer` 被 5 处视图使用）
`ImportView`、`SegmentLibraryView`、`SegmentLibraryViewV2`、`ProjectOverviewView`、`SchemesView`。本次只换 `InlineVideoPlayer` 内部播放内核，**对外 API（`SetVideo`/`SetSegment`/`AutoPlayOnHover`）保持不变**，5 处调用方零改动。

### 4.2 控件替换
- XAML：`<MediaElement x:Name="Player" .../>` → `<ffme:MediaElement x:Name="Player" .../>`（引入 `xmlns:ffme`）。
- 事件映射：`MediaOpened`/`MediaEnded`/`MediaFailed` → FFME 对应事件（签名不同，需逐个适配）。
- 属性映射：`Source`/`Play()`/`Pause()`/`Stop()`/`Close()`/`Position`/`NaturalDuration` → FFME 等价 API（FFME 用 `Open(Uri)`/`Close()`、`Position`、`MediaInfo`/`NaturalDuration`，需核对实际 API）。

### 4.3 FFmpeg 初始化（启动期一次）
- App 启动时 `Library.FFmpegDirectory = BundledBinaries.BinDirectory`（共享库与 ffmpeg.exe 同目录）。
- 打一行启动诊断 `[FfmeDiag] ffmpeg libs=<dir> ok/fail`，便于自验证 grep。

### 4.4 生命周期（**绝不重蹈 v0.6.0 卡死**）
- **每个 `InlineVideoPlayer` 持有一个 FFME 实例，hover 只做 `Open/Play` 与 `Pause/Close`，绝不 per-hover 反复 new/Dispose 整个内核。**
- 所有 `Open/Close` 走 FFME 自身的异步 API，不在 UI 线程同步阻塞。
- 全局单播协调（`PlaybackStarted` 事件）逻辑保留。

### 4.5 降级（总纲推论 4：降级而非报错）
- 万一 FFME 打开失败（极端损坏文件），**不弹系统错误码**；inline 显示人话提示「该片段无法预览」+ 缩略图保留，并提供「用系统播放器打开」入口。绝不出现 `0xC00D109B` 字样。

## 5. 兼容性与打包

- LGPL FFmpeg 共享库（`avcodec/avformat/avutil/swscale/swresample/avdevice/avfilter`）放进 `Resources/bin/`，随 `Content` 拷到输出 `bin/`，Inno Setup 打进安装包。
- **dumpbin / Get-PeImports 静态分析**每个新增 DLL 的依赖，对照 `bin/` 补齐（沿用 v0.4.1 vcomp140 方法），确认不缺 native 依赖。
- 体积：预计 +N MB（实测，见 §8 验证门槛）；如超 Gitee 90MB 单卷，DiskSpanning 分卷已有方案。
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

## 8. 测试与验证门槛（实施前必须在构建机过，否则不动手 — §自我验证铁律）

构建机当前离线，以下为**编码前硬门槛**：
1. **FFME × .NET 8 × 当前 WPF** restore + 编译 + 启动通过。
2. **LGPL FFmpeg 共享库**：确定来源（可信 LGPL 构建）、版本、实测体积；dumpbin 静态分析依赖完备。
3. **真实 HEVC/iPhone 高效格式素材**在构建机能 hover 播放（不再 0xC00D109B）。
4. **干净 Win10/11 e2e**（有干净机时）：装包 → 导入 HEVC → hover 预览能播 → 全链路导出。
5. **N 版 e2e**（有 N 版机时）：确认走自带解码栈不撞 mfplat.dll。
6. 启动诊断 `[FfmeDiag]` 全绿、零 WRN/ERR 关联预览。

## 9. 风险与回退
- **FFME 不兼容 .NET 8 / 体积过大 / 授权不清** → 回退到后备方案「自建 ffmpeg.exe 裸帧渲染器」（零体积、授权最干净），代价是工程量。
- **回退路径**：保留当前 MediaElement 实现于 git 历史，新内核以独立 commit 引入，验证不过可一键回退。

## 10. 单元测试
- FFME 内核细节难单元测（依赖 native + UI），以**构建机真实 e2e** 为主验证手段。
- 可补：`BundledBinaries` 新增「FFmpeg 共享库存在性探测」方法的单测（对齐现有 `ProbeVcRuntime` 模式）。
