# MixCut Windows - AI 广告视频混剪工具

> Windows 桌面版 | C# + WPF + .NET 8 | 本地 AI 驱动 | 对齐 macOS 原版

MixCut 面向广告投放团队，**导入广告素材视频 → AI 自动按语义切分分镜 → 智能排列组合生成多条差异化广告视频 → 一键批量导出**。视频内容、API Key 全部本地处理，AI 调用只发送结构化文本不发视频。

本项目是 [macOS 原生版 MixCut](https://github.com/RoshanGH/mixed_cut) 的 Windows 移植版，功能完整对齐。

## 下载

| 平台 | 链接 |
|------|------|
| Windows | [Releases](https://github.com/RoshanGH/mixcut-windows/releases) |
| macOS | [Releases](https://github.com/RoshanGH/mixed_cut/releases) |

最新版本：**v0.3.0**（Windows 首发）

## 功能亮点

### AI 智能流水线
- **AI 语义切分** — 自动识别 11 种语义类型（噱头引入 / 痛点 / 产品方案 / 效果展示 / 信任背书 / 价格对比 / 活动福利 / 行动号召 / 产品定位 / 产品使用教育 / 过渡）
- **本地语音识别** — 内置 whisper.cpp（`ggml-large-v3-turbo` 模型，首次启动自动下载），完全离线
- **AI 混剪方案** — 两步生成：① AI 出策略（风格 / 受众 / 叙事结构）② AI 按策略排列分镜组合
- **多 AI 提供商** — 千问 / MiniMax / DeepSeek / Claude 原生 / 国内转发网关（Claude/Gemini/OpenAI 三平台）/ 自定义 OpenAI 兼容

### 视频处理
- **硬件加速** — 启动时探测可用编码器，优先用 NVIDIA NVENC / Intel QSV / AMD AMF，CPU 兜底
- **分镜批量导出** — 多选分镜 → 单独 MP4，文件命名 `{编号}_{原视频名}.mp4`
- **第一帧无黑屏** — 用 trim filter + setpts 精确切片
- **视频全局共享** — 同一视频（SHA-256 哈希）跨项目共享

### 体验
- **MVVM 数据驱动 + VirtualizingWrapPanel** — 1000+ 分镜也能瞬时加载
- **分镜按视频分组渲染** — 每个视频独立标题栏 + 该组分镜，对齐 Mac 版交互
- **缩略图全局懒加载缓存** — 滚动不卡顿，进入分镜库 <100ms
- **键盘快捷键** — `Ctrl+1~5` 切换工作区 / `F2` 重命名 / 多选模式 `Ctrl+A`/`Ctrl+D`/`Ctrl+0`/`Esc`
- **应用启动恢复上次项目** + **双击项目名直接重命名**
- **Toast + Skeleton + InlineBanner** — 完整的反馈/加载/错误展示体系

### 容错与稳定性
- **崩溃捕获** — 三层 unhandled exception hook（Dispatcher / AppDomain / TaskScheduler），不让窗口神秘消失
- **进程不孤儿** — 所有 whisper/ffmpeg 子进程挂 Job Object，主进程退出一起死
- **状态自愈** — 应用强退后下次启动自动重置卡在「分析中」的视频状态
- **断点续传** — Whisper 模型下载支持 HTTP Range，断网续传
- **AI JSON 4 层防御** — 预清洗 / 截断修复 / 错误位置救援 / 失败 JSON 落盘
- **缩略图自动修复** — 启动时检测缺失 thumbnail 自动后台补生成

## 系统要求

- Windows 10 22H2 / Windows 11 x64
- .NET 8 Desktop Runtime（应用首次启动会提示下载）
- 首次启动下载 Whisper 模型 ~1.5 GB（仅一次）
- 可选：NVIDIA 显卡（NVENC 加速）/ Intel 核显（QSV 加速）/ AMD 显卡（AMF 加速）

## 用户使用（普通用户）

1. 下载 [Releases](https://github.com/RoshanGH/mixcut-windows/releases) 的 ZIP 包，解压
2. 双击 `MixCut.exe` 启动
3. 首次启动会下载 Whisper 模型并引导配置 AI Key

### 配置 AI Key

在 **设置 → AI 配置** 中填入 API Key：

| 提供商 | 说明 |
|--------|------|
| **千问 (Qwen)** | 阿里通义千问 |
| **MiniMax** | MiniMax M2.7 / abab 系列 |
| **DeepSeek** | DeepSeek-V4 系列 |
| **Claude** | Anthropic 官方 API |
| **国内转发网关** | 转发到 Claude / Gemini / OpenAI 三平台之一 |
| **自定义** | 任意 OpenAI 兼容 API（自填地址 + 模型名） |

## 开发者构建

```powershell
# 1. 克隆
git clone https://github.com/RoshanGH/mixcut-windows.git
cd mixcut-windows

# 2. 拷贝 Windows 版 FFmpeg / ffprobe / whisper-cli 二进制到 src/MixCut/Resources/bin/
#    （ffmpeg.exe、ffprobe.exe、whisper-cli.exe + 所需 DLL）

# 3. 还原 + 编译
dotnet restore MixCut.sln
dotnet build src/MixCut/MixCut.csproj -c Release

# 4. 跑起来
dotnet run --project src/MixCut/MixCut.csproj

# 或 publish 出独立 EXE
dotnet publish src/MixCut/MixCut.csproj -c Release -r win-x64 --self-contained false -o publish
```

详细技术架构、跨机器开发流程、商业 toC 标准、自我验证铁律见 [CLAUDE.md](./CLAUDE.md)。

## 故障排查

### 首次启动相关

| 症状 | 原因 | 解决步骤 |
|---|---|---|
| 双击安装包出现「Windows 已保护你的电脑」蓝色弹窗 | SmartScreen 拦截未签名应用 | 点「**更多信息**」→「**仍要运行**」。以后不会再弹 |
| 双击 MixCut.exe 没反应 | .NET 8 Desktop Runtime 缺失 | v0.4.0+ 安装包自带 Runtime，不会有这问题。若是绿色版 zip：[下载 .NET 8 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/8.0) |
| 启动一闪而过 | App 启动期崩溃 | 查日志 `%APPDATA%\MixCut\logs\mixcut-YYYYMMDD.log`，搜 `[FTL]` |
| 杀软（360/腾讯管家/Defender）拦截 `whisper-cli.exe` 或 `ffmpeg.exe` | 未签名 EXE 被误报 | 把 MixCut 安装目录加入杀软白名单 |

### 功能错误

| 症状（日志或界面文案） | 错误码 | 含义 | 解决步骤 |
|---|---|---|---|
| 语音识别失败：进程异常退出 ExitCode=-1073741515 | 0xC0000135 STATUS_DLL_NOT_FOUND | VC++ Runtime DLL 缺失 | 升级到 v0.3.1+（含 6 个 VC Runtime DLL）。v0.4.0 起还多带了 vcomp140（OpenMP runtime）|
| 语音识别失败：进程异常退出 ExitCode=-1073741795 | 0xC000001D STATUS_ILLEGAL_INSTRUCTION | CPU 不支持 AVX2 | 老 CPU（Intel < Haswell 2013 / AMD < Excavator 2015）跑不了内置 whisper-cli。其它功能仍可用 |
| 启动弹窗「MixCut 安装包不完整」 | 内置二进制被杀软隔离 | bin\ 目录下文件被删 | 把整个安装目录加杀软白名单，重装 |
| 启动弹窗「MixCut 数据目录无法写入」 | %APPDATA% 不可写 | 漫游目录权限异常 / GPO 限制 | v0.4.0 已三级兜底（自动回退 LocalAppData 或 EXE 同级 data/）。仍不行联系管理员 |
| 启动弹窗「CPU 不支持 AVX2」 | n/a | 见上 | 见上 |
| 导入视频卡片首帧黑屏 | n/a | thumbnail 没生成 | 已修复（v0.3.0+）。重启应用会自动后台补齐 `[ThumbRepair]` |

### 一键收集诊断信息

把这段 PowerShell 复制到任意 PowerShell 窗口跑（无需管理员），会输出最新一份日志里的关键诊断：

```powershell
$log = Get-ChildItem $env:APPDATA\MixCut\logs -Filter "mixcut-*.log" |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
Get-Content $log.FullName |
    Select-String -Pattern "EnvDiag|VcRuntimeDiag|CpuDiag|BinariesDiag|AppDataDiag|HwProbe|\[WRN\]|\[ERR\]|\[FTL\]"
```

### 启动期诊断 tag 一览（按出现顺序）

- `[AppDataDiag] selected=tier1-Roaming root=...` — 数据目录三级兜底选了哪一层
- `[EnvDiag] os=... arch=... cpu=... vcrt=... bin=... appdata=... installdir=... path=... pass=...` — 启动一行汇总
- `[VcRuntimeDiag] all 6 VC Runtime DLLs present` — VC++ Runtime 完整
- `[HwProbe] encode=h264_qsv decode=qsv whisper=cpu` — 硬件加速选择

如果 `[EnvDiag] pass=False`，对照表格找出问题 = 1 分钟自助解决。

## 致谢

- [whisper.cpp](https://github.com/ggerganov/whisper.cpp) — 本地语音识别
- [FFmpeg](https://ffmpeg.org/) — 视频处理底座
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) — MVVM 框架
- [VirtualizingWrapPanel](https://github.com/sbaeumlisberger/VirtualizingWrapPanel) — WPF 真虚拟化
- [Serilog](https://serilog.net/) — 结构化日志

## License

待定（暂时保留所有权利，后续会补 LICENSE 文件）。
