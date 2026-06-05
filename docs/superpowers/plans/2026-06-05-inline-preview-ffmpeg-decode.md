# hover 预览统一到自带 FFmpeg 解码 实施计划（进程外渲染器版）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把分镜卡片 hover 预览的播放内核从 WPF MediaElement（依赖系统编解码器）换成「进程外 ffmpeg.exe 裸帧渲染器」，消灭 `0xC00D109B`，实现全格式 + N 版 Windows 通用兼容，零新增安装包体积。

**Architecture:** 新建 `FfmpegFramePlayer`（WPF 控件），内部用已打包的 `ffmpeg.exe` 解码：视频走 `-f rawvideo -pix_fmt bgra` 管道 → 后台线程读帧 → `WriteableBitmap`；音频走 `-f s16le` 管道 → WASAPI/NAudio，作主时钟。`InlineVideoPlayer` 对外 API 不变，5 处调用方零改动。所有子进程挂 `ChildProcessTracker`。

**Tech Stack:** C# / WPF / .NET 8 / 内置 ffmpeg.exe(进程外) / WriteableBitmap / NAudio(或 WASAPI) / Serilog

**前置约束（铁律）：** 构建机 `mlamp@100.112.4.71` 离线时不进入需要实跑验证的 Step；参照 CLAUDE.md §兼容性总纲、§不破坏已有功能 SOP、§自我验证铁律。每个 Task 在独立 commit。

**spike 结论（2026-06-05，已完成）：** FFME×.NET8 兼容但因 GPL 传染/LGPL4.4 无构建/+40~60MB 三杀被否；改进程外。详见 spec §3。

---

## 文件结构

| 文件 | 责任 | 动作 |
|---|---|---|
| `src/MixCut/Services/VideoProcessing/FramePipeArgs.cs` | 纯函数：拼 ffmpeg 视频/音频管道参数、算帧字节数/PTS | Create |
| `src/MixCut.Tests/FramePipeArgsTests.cs` | 上述纯函数单测 | Create |
| `src/MixCut/Views/Components/FfmpegFramePlayer.cs` | WPF 控件：子进程管道→WriteableBitmap 渲染 + 音频时钟 | Create |
| `src/MixCut/Views/Components/InlineVideoPlayer.xaml` | `MediaElement` → `FfmpegFramePlayer` | Modify |
| `src/MixCut/Views/Components/InlineVideoPlayer.xaml.cs` | 改调用新控件 API + 降级 | Modify |
| `src/MixCut/App.xaml.cs` | 启动期 `[FfmeDiag] ffmpeg.exe exists` 诊断 | Modify |

---

## Task 1：管道参数纯函数 + 单测（先把可测的测了）

**Files:**
- Create: `src/MixCut/Services/VideoProcessing/FramePipeArgs.cs`
- Create: `src/MixCut.Tests/FramePipeArgsTests.cs`

- [ ] **Step 1: 写失败测试**

`src/MixCut.Tests/FramePipeArgsTests.cs`：

```csharp
using MixCut.Services.VideoProcessing;
using Xunit;

public class FramePipeArgsTests
{
    [Fact]
    public void VideoArgs_含裸帧bgra与裁剪缩放与起止()
    {
        var args = FramePipeArgs.Video("C:\\v.mp4", start: 1.5, dur: 3.0, w: 320, h: 180, fps: 30);
        var s = string.Join(" ", args);
        Assert.Contains("-ss 1.5", s);
        Assert.Contains("-t 3", s);
        Assert.Contains("-an", s);
        Assert.Contains("-f rawvideo", s);
        Assert.Contains("-pix_fmt bgra", s);
        Assert.Contains("scale=320:180", s);
        Assert.Contains("fps=30", s);
        Assert.Equal("pipe:1", args[^1]);
    }

    [Fact]
    public void AudioArgs_含s16le立体声采样率()
    {
        var s = string.Join(" ", FramePipeArgs.Audio("C:\\v.mp4", start: 0, dur: 5));
        Assert.Contains("-vn", s);
        Assert.Contains("-f s16le", s);
        Assert.Contains("-ar 44100", s);
        Assert.Contains("-ac 2", s);
    }

    [Fact]
    public void FrameBytes_等于宽高乘4()
    {
        Assert.Equal(320 * 180 * 4, FramePipeArgs.FrameBytes(320, 180));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run（构建机）：`dotnet test --filter "FullyQualifiedName~FramePipeArgsTests"`
Expected: FAIL（`FramePipeArgs` 未定义）。

- [ ] **Step 3: 实现纯函数**

`src/MixCut/Services/VideoProcessing/FramePipeArgs.cs`：

```csharp
using System.Globalization;

namespace MixCut.Services.VideoProcessing;

/// <summary>hover 预览裸帧管道的 ffmpeg 参数拼接（纯函数，便于单测）。</summary>
public static class FramePipeArgs
{
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>视频裸帧管道：bgra 像素流，已裁剪缩放到卡片尺寸 + 固定 fps。</summary>
    public static string[] Video(string path, double start, double dur, int w, int h, int fps) => new[]
    {
        "-ss", F(start), "-i", path, "-t", F(dur), "-an",
        "-vf", $"scale={w}:{h}:force_original_aspect_ratio=decrease,fps={fps}",
        "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1",
    };

    /// <summary>音频管道：s16le 立体声 44.1k，作主时钟。</summary>
    public static string[] Audio(string path, double start, double dur) => new[]
    {
        "-ss", F(start), "-i", path, "-t", F(dur), "-vn",
        "-f", "s16le", "-ar", "44100", "-ac", "2", "pipe:1",
    };

    /// <summary>一帧 bgra 字节数。</summary>
    public static int FrameBytes(int w, int h) => w * h * 4;
}
```

- [ ] **Step 4: 跑测试确认通过**

Run（构建机）：`dotnet test --filter "FullyQualifiedName~FramePipeArgsTests"`
Expected: PASS（3 passed）。

- [ ] **Step 5: Commit**

```bash
git add src/MixCut/Services/VideoProcessing/FramePipeArgs.cs src/MixCut.Tests/FramePipeArgsTests.cs
git commit -m "feat(player): hover 预览裸帧管道参数纯函数 + 单测"
```

---

## Task 2：FfmpegFramePlayer 视频帧泵（先做视频，构建机验 HEVC 渲染）

**Files:**
- Create: `src/MixCut/Views/Components/FfmpegFramePlayer.cs`

- [ ] **Step 1: 实现视频帧泵控件（视频-only，音频留 Task 4）**

`FfmpegFramePlayer.cs`：一个含 `Image` 的 `UserControl`（或继承 `Image`）。要点：
- 字段：`Process? _proc; Thread? _reader; WriteableBitmap? _bmp; volatile bool _stop;` 尺寸 `_w/_h/_fps`。
- `public event EventHandler? Ended; public event EventHandler<string>? Failed;`
- `public void Open(string path, double start, double dur)`：算 `_w/_h`（控件像素，DPI 换算），起子进程：
  ```csharp
  var psi = new ProcessStartInfo(BundledBinaries.Ffmpeg)
  { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
  foreach (var a in FramePipeArgs.Video(path, start, dur, _w, _h, _fps)) psi.ArgumentList.Add(a);
  _proc = Process.Start(psi)!;
  ChildProcessTracker.AddProcess(_proc);   // 铁律：防孤儿
  ```
- 后台读帧线程：循环 `ReadFull(stream, frameBuf, FramePipeArgs.FrameBytes(_w,_h))`，读满一帧 → `Dispatcher.BeginInvoke(() => _bmp.WritePixels(...))`；按 `1000/_fps` ms 墙钟节流（Task 4 改音频主时钟）。读到 EOF → `Ended`。
- `public void Stop()`：`_stop=true`；`_proc` 未退出则 `Kill(entireProcessTree:true)`；join 线程；释放。
- 所有 `try/catch`，异常 → `Failed?.Invoke(this, msg)`，不抛。

（完整代码在实现时落地；关键约束：进程/IO 全后台线程，UI 线程只 `WritePixels`；子进程必挂 `ChildProcessTracker`；`Stop` 必 kill。）

- [ ] **Step 2: Mac 语法编译**

Run：`dotnet build src/MixCut/MixCut.csproj -c Release -p:EnableWindowsTargeting=true`
Expected: 0 错误。

- [ ] **Step 3: 构建机裸帧管道冒烟（不接 UI，先证管道）**

Run（构建机）：用现有 ffmpeg.exe 对真实 HEVC 片段跑
`ffmpeg.exe -ss 0 -i <hevc.mp4> -t 1 -an -vf scale=320:180,fps=30 -f rawvideo -pix_fmt bgra -v error pipe:1 | Measure`，确认有 `320*180*4*N` 量级字节流出、exit 0。
Expected: 字节流非空、无 0xC00D109B。证明现有 ffmpeg 能把 HEVC 解成 bgra 裸帧。

- [ ] **Step 4: Commit**

```bash
git add src/MixCut/Views/Components/FfmpegFramePlayer.cs
git commit -m "feat(player): FfmpegFramePlayer 视频裸帧泵（进程外 ffmpeg 解码→WriteableBitmap）"
```

---

## Task 3：换入 InlineVideoPlayer（视频-only）+ 真机渲染验证

**Files:**
- Modify: `src/MixCut/Views/Components/InlineVideoPlayer.xaml`
- Modify: `src/MixCut/Views/Components/InlineVideoPlayer.xaml.cs`
- Modify: `src/MixCut/App.xaml.cs`

> 动手前重读 CLAUDE.md §不破坏已有功能，对照 spec §6 的 8 项能力。

- [ ] **Step 1: XAML 换控件**

`InlineVideoPlayer.xaml`：把 `<MediaElement x:Name="Player" .../>` 换成 `<components:FfmpegFramePlayer x:Name="Player" .../>`（加 xmlns）。

- [ ] **Step 2: 适配 .cs**

- `OnPlayClick`：`Player.Open(_videoPath, _segmentStart ?? 0, (_segmentEnd ?? total) - (_segmentStart ?? 0))`。
- `Stop()`：`Player.Stop()`。
- 进度/时长：`FfmpegFramePlayer` 暴露 `Position`（由读帧计时累计）供现有 timer 逻辑用；区间到点停沿用现有 `_segmentEnd` timer。
- `OnMediaEnded` ← `Player.Ended`；`OnMediaFailed` ← `Player.Failed`，改降级（不弹错误码，保留缩略图 + 提示）。

- [ ] **Step 3: 启动诊断**

`App.xaml.cs` 启动期加：
```csharp
Serilog.Log.Information("[FfmeDiag] ffmpeg.exe={Path} exists={Ok}",
    BundledBinaries.Ffmpeg, BundledBinaries.FfmpegAvailable);
```

- [ ] **Step 4: Mac 语法编译**

Run：`dotnet build src/MixCut/MixCut.csproj -c Release -p:EnableWindowsTargeting=true`
Expected: 0 错误。

- [ ] **Step 5: 构建机真机：HEVC 能播 + 不卡**

Run（构建机）：publish → 改 `settings.json` `last_nav_item="2"` → 启动 → 导入 HEVC → hover 分镜卡片。
Expected: 画面正常出现，**不再 0xC00D109B**；连续 hover 多卡不卡死；grep `[FfmeDiag] exists=True`、无预览相关 WRN/ERR。

- [ ] **Step 6: Commit**

```bash
git add src/MixCut/Views/Components/InlineVideoPlayer.xaml src/MixCut/Views/Components/InlineVideoPlayer.xaml.cs src/MixCut/App.xaml.cs
git commit -m "feat(player): InlineVideoPlayer 换 FfmpegFramePlayer（视频，消灭 0xC00D109B）"
```

---

## Task 4：音频 + 音画同步

**Files:**
- Modify: `src/MixCut/Views/Components/FfmpegFramePlayer.cs`
- （可能 Add）音频输出依赖（`NAudio` 或自封 WASAPI）

- [ ] **Step 1: 加音频管道 + 输出**

第二个 ffmpeg 子进程跑 `FramePipeArgs.Audio(...)`，PCM s16le → 音频输出设备（NAudio `WaveOutEvent`/`WasapiOut`）。子进程同样挂 `ChildProcessTracker`。

- [ ] **Step 2: 音频主时钟同步视频**

视频帧 PTS=帧序号/fps；呈现条件改为「音频已播位置 ≥ 帧 PTS」（取代 Task 2 的墙钟节流）。音频位置由输出设备已播字节/(44100*2*2) 算秒。

- [ ] **Step 3: Mac 编译 + 构建机实测漂移**

Run：Mac 编译 0 错误；构建机 hover 播一段有明显口型/节拍的素材，确认音画漂移不可感、Stop 时音视频同时停、无残留 ffmpeg 进程（`Get-Process ffmpeg`）。

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat(player): FfmpegFramePlayer 加音频 + 音频主时钟同步"
```

---

## Task 5：全量 e2e 回归 + 丝滑验收

**Files:** 无（验证）

- [ ] **Step 1: 逐项回归 spec §6 的 8 项能力**

hover 自动播/离开停、SetVideo 完整播、SetSegment 区间播+到点停、播放中微调 re-seek、全局单播互斥、缩略图↔播放态切换、进度条拖拽 seek、Unloaded 解绑（+ 无孤儿 ffmpeg）。Expected: 8 项全在。

- [ ] **Step 2: 丝滑压力**

快速扫过 10+ 张分镜卡片，确认不卡死、不堆积 ffmpeg 进程、CPU 不爆。Expected: 流畅；若卡 → 触发 spec §9 回退评估（Flyleaf+LGPL8.1）。

- [ ] **Step 3: （有干净机时）干净 Win10/11 + N 版 e2e**

装包到干净 Win10/11（无 HEVC 扩展）→ 导入 HEVC → hover 能播 → 全链路导出。N 版重复。无干净机时标注「待干净机验」。

- [ ] **Step 4: grep 诊断收尾**

`Get-Content <log> | Select-String "[FfmeDiag]|ERR|WRN"`：`exists=True`、无预览相关 WRN/ERR。

---

## Self-Review 记录

- **Spec 覆盖**：§3 进程外选型→Task1-4；§4.2 FfmpegFramePlayer→Task2；§4.3 诊断→Task3 Step3；§4.4 同步→Task4；§4.5 降级→Task3 Step2；§6 八项能力→Task5 Step1；§8 验证→各 Task 真机 Step + Task5；§10 纯函数单测→Task1。全覆盖。
- **占位扫描**：Task2/4 的控件完整 C# 在实现时落地（含关键约束清单），非偷懒占位——WPF 进程/线程/位图плумbing 代码量大，计划给出骨架+硬约束，细节随实现。
- **类型一致**：`FramePipeArgs.Video/Audio/FrameBytes`（Task1 定义）→ Task2 使用，签名一致；`FfmpegFramePlayer.Open/Stop/Ended/Failed/Position`（Task2 定义）→ Task3 使用，一致。
- **风险**：丝滑度→Task5 Step2 实测兜底，回退 Flyleaf+LGPL8.1（spec §9）。
