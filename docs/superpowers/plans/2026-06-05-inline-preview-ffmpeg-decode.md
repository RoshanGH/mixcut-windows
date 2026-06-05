# hover 预览统一到自带 FFmpeg 解码 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把分镜卡片 hover 预览的播放内核从 WPF MediaElement（依赖系统编解码器）换成 FFME（自带 FFmpeg 解码），消灭 `0xC00D109B`，实现全格式 + N 版 Windows 通用兼容。

**Architecture:** 引入 `Unosquare.FFME` WPF 控件 + LGPL FFmpeg 共享库（放 `Resources/bin/`）。`InlineVideoPlayer` 对外 API（`SetVideo`/`SetSegment`/`AutoPlayOnHover`）不变，5 处调用方零改动，只换内部内核。启动期一次性 `Library.FFmpegDirectory` 指向 `bin/`。每个播放器持有单一 FFME 实例，hover 只 Open/Close，绝不 per-hover 重建（规避 v0.6.0 卡死）。

**Tech Stack:** C# / WPF / .NET 8 / Unosquare.FFME / FFmpeg(LGPL shared) / Serilog

**前置约束（铁律）：**
- 构建机 `mlamp@100.112.4.71` 离线时**不进入 Task 1+**；Task 0 是所有编码的硬门槛。
- 参照 CLAUDE.md §兼容性总纲、§不破坏已有功能 SOP、§自我验证铁律。

---

## 文件结构

| 文件 | 责任 | 动作 |
|---|---|---|
| `src/MixCut/MixCut.csproj` | 加 FFME PackageReference + LGPL 共享库打包规则 | Modify |
| `src/MixCut/Resources/bin/*.dll`（构建机） | LGPL FFmpeg 共享库 | Create（仅构建机） |
| `src/MixCut/App.xaml.cs` | 启动期 `Library.FFmpegDirectory` 初始化 + `[FfmeDiag]` 日志 | Modify |
| `src/MixCut/Infrastructure/BundledBinaries.cs` | 新增 FFmpeg 共享库存在性探测 | Modify |
| `src/MixCut/Views/Components/InlineVideoPlayer.xaml` | `MediaElement` → `ffme:MediaElement` | Modify |
| `src/MixCut/Views/Components/InlineVideoPlayer.xaml.cs` | 事件/属性 API 适配 + 降级逻辑 | Modify |
| `src/MixCut.Tests/BundledBinariesTests.cs` | 共享库探测单测 | Create |

---

## Task 0：构建机验证 spike（编码硬门槛，构建机必须在线）

**目的：** 钉死三个不确定项，spike 不过则停工并向用户报告，不强行往下做。

**Files:** 无（纯验证，spike 代码用完即弃，不进 main）

- [ ] **Step 1: 确认构建机在线**

Run（Mac）：`ssh -i ~/.ssh/mixcut_win -o IdentitiesOnly=yes -o ConnectTimeout=12 mlamp@100.112.4.71 'whoami'`
Expected: 输出用户名。超时则停工，告知用户「构建机离线，无法验证，暂不动手」。

- [ ] **Step 2: 验证 FFME 能在 .NET 8 + 当前 WPF restore + 编译**

在构建机建一个最小 WPF .NET 8 临时工程，加 `<PackageReference Include="FFME.Windows" Version="*" />`（注意实际包名，FFME 的包名历史上为 `FFME.Windows`），放一个 `<ffme:MediaElement/>`，`dotnet build`。
Expected: restore + build 成功。失败 → 记录错误，触发 §9 回退评估。

- [ ] **Step 3: 取得 LGPL FFmpeg 共享库并实测体积**

获取与 FFME 兼容的 **LGPL 构建** FFmpeg 共享库（`avcodec/avformat/avutil/swscale/swresample/avfilter/avdevice` 等 dll）。
Run：`Get-ChildItem <libdir> -Filter *.dll | Measure-Object -Property Length -Sum`
记录：版本、总体积、来源 URL。若总体积 + 现有安装包 > Gitee 90MB 单卷 → 标记需 DiskSpanning。

- [ ] **Step 4: dumpbin 静态分析依赖完备性**

对每个 FFmpeg 共享 dll 跑 PE import 扫描（`Get-PeImports`，CLAUDE.md §D 武器库），列出依赖 dll，对照 `bin/` 是否完备（VC Runtime 等）。
Expected: 无缺失 native 依赖；缺啥记录待 Task 2 补。

- [ ] **Step 5: 真机播 HEVC 冒烟**

用 spike 工程，`Library.FFmpegDirectory=<libdir>`，`Open` 一个真实 HEVC/iPhone「高效」格式 mp4。
Expected: 能解码出画面、不报 `0xC00D109B`。**这是整个方案成立的核心证据。** 失败 → 触发 §9 回退到「自建 ffmpeg.exe 渲染器」。

- [ ] **Step 6: 把钉死的事实回填 spec**

把 FFME 实际包名/版本、API 签名（`Open`/`Close`/`Position`/`MediaOpened` 事件参数类型）、LGPL 库来源/版本/体积写回 `docs/superpowers/specs/2026-06-05-inline-preview-ffmpeg-decode-design.md` §3/§5/§8，commit。
后续 Task 1-5 的 FFME API 调用以本步钉死的签名为准。

---

## Task 1：引入 FFME 包 + LGPL 共享库打包

**Files:**
- Modify: `src/MixCut/MixCut.csproj`
- Create（构建机）: `src/MixCut/Resources/bin/avcodec*.dll` 等（Task 0 取得的库）

- [ ] **Step 1: 加 FFME PackageReference**

按 Task 0 Step 2 钉死的包名/版本，在 `MixCut.csproj` 的 `<ItemGroup>` 加：

```xml
<PackageReference Include="FFME.Windows" Version="<Task0确认版本>" />
```

- [ ] **Step 2: 确认共享库随 Content 打包**

`MixCut.csproj` 已有规则（37-41 行）会把 `Resources\bin\**\*` 拷到输出 `bin\`。把 Task 0 的 LGPL 共享库放进构建机 `Resources/bin/`，无需改 csproj。确认 publish 后 `publish/bin/` 含这些 dll。

- [ ] **Step 3: 编译验证（Mac 语法 + 构建机 publish）**

Run（Mac）：`dotnet build src/MixCut/MixCut.csproj -c Release -p:EnableWindowsTargeting=true`
Expected: 0 错误（语法/类型过）。
Run（构建机）：远端 `dotnet publish`。
Expected: 成功，`publish/bin/` 含共享库。

- [ ] **Step 4: Commit**

```bash
git add src/MixCut/MixCut.csproj
git commit -m "feat(player): 引入 FFME 包 + LGPL FFmpeg 共享库打包"
```

---

## Task 2：BundledBinaries 共享库探测 + 单测

**Files:**
- Modify: `src/MixCut/Infrastructure/BundledBinaries.cs`
- Create: `src/MixCut.Tests/BundledBinariesTests.cs`

- [ ] **Step 1: 写失败测试**

新建 `src/MixCut.Tests/BundledBinariesTests.cs`：

```csharp
using MixCut.Infrastructure;
using Xunit;

public class BundledBinariesTests
{
    [Fact]
    public void RequiredFfmpegLibs_列表非空且含核心解码库()
    {
        Assert.Contains("avcodec", string.Join(",", BundledBinaries.RequiredFfmpegLibPrefixes));
        Assert.Contains("avformat", string.Join(",", BundledBinaries.RequiredFfmpegLibPrefixes));
        Assert.Contains("avutil", string.Join(",", BundledBinaries.RequiredFfmpegLibPrefixes));
    }

    [Fact]
    public void ProbeFfmpegLibs_缺目录时返回缺失而非抛异常()
    {
        var (allPresent, missing) = BundledBinaries.ProbeFfmpegLibs("/nonexistent_dir_xyz");
        Assert.False(allPresent);
        Assert.NotEmpty(missing);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run（构建机）：`dotnet test --filter "FullyQualifiedName~BundledBinariesTests"`
Expected: FAIL（`RequiredFfmpegLibPrefixes`/`ProbeFfmpegLibs` 未定义）。

- [ ] **Step 3: 实现探测方法**

在 `BundledBinaries.cs` 加（库前缀以 Task 0 实际为准，下为典型集）：

```csharp
/// <summary>FFME 解码所需的 FFmpeg 共享库前缀（实际文件名带版本号，如 avcodec-61.dll，用前缀匹配）。</summary>
public static readonly string[] RequiredFfmpegLibPrefixes = new[]
{
    "avcodec", "avformat", "avutil", "swscale", "swresample",
};

/// <summary>探测 FFmpeg 共享库在指定目录是否齐全（前缀匹配，兼容版本号后缀）。</summary>
public static (bool AllPresent, string[] Missing) ProbeFfmpegLibs(string dir)
{
    if (!Directory.Exists(dir))
    {
        return (false, RequiredFfmpegLibPrefixes);
    }
    var files = Directory.GetFiles(dir, "*.dll").Select(Path.GetFileName).ToArray();
    var missing = RequiredFfmpegLibPrefixes
        .Where(p => !files.Any(f => f!.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        .ToArray();
    return (missing.Length == 0, missing);
}
```

- [ ] **Step 4: 跑测试确认通过**

Run（构建机）：`dotnet test --filter "FullyQualifiedName~BundledBinariesTests"`
Expected: PASS（2 passed）。

- [ ] **Step 5: Commit**

```bash
git add src/MixCut/Infrastructure/BundledBinaries.cs src/MixCut.Tests/BundledBinariesTests.cs
git commit -m "feat(player): BundledBinaries 加 FFmpeg 共享库探测 + 单测"
```

---

## Task 3：启动期 FFmpeg 初始化 + 诊断日志

**Files:**
- Modify: `src/MixCut/App.xaml.cs`

- [ ] **Step 1: 在启动初始化处加 FFME 初始化**

在 `App.xaml.cs` 应用启动（现有 `[VcRuntimeDiag]`/`[EnvDiag]` 打日志的同一阶段）加：

```csharp
// FFME 自带 FFmpeg 解码：指向内置 bin\ 共享库，彻底不依赖系统编解码器（§兼容性总纲）。
try
{
    Unosquare.FFME.Library.FFmpegDirectory = BundledBinaries.BinDirectory;
    var (libsOk, libsMissing) = BundledBinaries.ProbeFfmpegLibs(BundledBinaries.BinDirectory);
    if (libsOk)
        Serilog.Log.Information("[FfmeDiag] ffmpeg libs={Dir} ok", BundledBinaries.BinDirectory);
    else
        Serilog.Log.Warning("[FfmeDiag] ffmpeg libs 缺失: {Missing}", string.Join(",", libsMissing));
}
catch (Exception ex)
{
    Serilog.Log.Warning(ex, "[FfmeDiag] FFME 初始化失败，预览将退回仅缩略图");
}
```

- [ ] **Step 2: 构建机 publish + 启动 + grep 诊断**

Run（构建机）：publish → `Stop-Process MixCut` → `Start-Process publish\MixCut.exe` → `Start-Sleep 12`。
Run：`Get-Content <最新log> | Select-String "[FfmeDiag]"`
Expected: `[FfmeDiag] ffmpeg libs=...\bin ok`，无 WRN。

- [ ] **Step 3: Commit**

```bash
git add src/MixCut/App.xaml.cs
git commit -m "feat(player): 启动期初始化 FFME FFmpeg 目录 + [FfmeDiag] 诊断"
```

---

## Task 4：InlineVideoPlayer 内核替换（核心，零回归）

**Files:**
- Modify: `src/MixCut/Views/Components/InlineVideoPlayer.xaml`
- Modify: `src/MixCut/Views/Components/InlineVideoPlayer.xaml.cs`

> 动手前重读 CLAUDE.md §不破坏已有功能：列出 InlineVideoPlayer 的 8 项能力（spec §6），改完逐项验。FFME API 以 Task 0 Step 6 钉死的签名为准——下方代码为预期形态，签名不符时以实测为准修正。

- [ ] **Step 1: XAML 换控件**

`InlineVideoPlayer.xaml`：根节点加 `xmlns:ffme="clr-namespace:Unosquare.FFME;assembly=ffme.win"`（实际 assembly 名以 Task 0 为准），把 `<MediaElement x:Name="Player" .../>` 换成 `<ffme:MediaElement x:Name="Player" .../>`。保留 `MediaOpened`/`MediaEnded`/`MediaFailed` 事件绑定名（FFME 同名事件，参数类型不同）。

- [ ] **Step 2: 适配 .cs 里的属性/方法**

`InlineVideoPlayer.xaml.cs` 逐处适配（FFME 实测 API 为准）：
- `Player.Source = new Uri(_videoPath)` + `Player.Play()` → FFME 异步 `await Player.Open(new Uri(_videoPath))`（在 `OnPlayClick` 改为 `async void` 且全 body try/catch）。
- `Player.Stop(); Player.Close(); Player.Source = null;` → `await Player.Close();`
- `Player.Position` / `Player.NaturalDuration.HasTimeSpan` → FFME 的 `Player.Position` / `Player.NaturalDuration`（核对类型，FFME 用 `TimeSpan?`/`Duration`）。
- `Player.CanPause` / `Player.Pause()` → FFME `Player.Pause()`（FFME 始终可暂停，按实测调整 `OnPauseClick`）。
- `OnMediaFailed`：去掉系统错误码弹窗，改降级（见 Step 3）。

- [ ] **Step 3: 降级逻辑（总纲推论 4）**

把 `OnMediaFailed` 改为不暴露错误码：

```csharp
private void OnMediaFailed(object? sender, /*FFME MediaFailedEventArgs*/ EventArgs e)
{
    Serilog.Log.Warning("[InlinePlayDiag] 解码失败 path={Path}", _videoPath);
    Stop();
    // 不弹系统错误码；保留缩略图，UI 不崩。极端损坏文件才会到这里。
    DurationBadge.Visibility = Visibility.Collapsed;
}
```

- [ ] **Step 4: 生命周期防卡死自检（读代码走查）**

确认：每个实例只有一个 `Player`；hover 只 `Open/Close`，无 per-hover `new ffme:MediaElement`；`Open/Close` 全异步不阻塞 UI 线程。逐条对照 spec §4.4。

- [ ] **Step 5: Mac 语法编译**

Run：`dotnet build src/MixCut/MixCut.csproj -c Release -p:EnableWindowsTargeting=true`
Expected: 0 错误。

- [ ] **Step 6: Commit**

```bash
git add src/MixCut/Views/Components/InlineVideoPlayer.xaml src/MixCut/Views/Components/InlineVideoPlayer.xaml.cs
git commit -m "feat(player): InlineVideoPlayer 内核 MediaElement→FFME（自带 ffmpeg 解码）"
```

---

## Task 5：构建机真机 e2e 回归（8 项能力 + HEVC 验收）

**Files:** 无（验证）

- [ ] **Step 1: publish + 启动**

Run（构建机）：publish → `Stop-Process MixCut` → 改 `%APPDATA%\MixCut\settings.json` `last_nav_item="2"`（SegmentLibrary）→ `Start-Process publish\MixCut.exe` → `Start-Sleep 12`。

- [ ] **Step 2: HEVC 验收（核心）**

导入一个真实 HEVC/iPhone「高效」素材 → 分析出分镜 → hover 分镜卡片。
Expected: 能播出画面，**不再弹 `0xC00D109B`**。grep 日志无 `[InlinePlayDiag] 解码失败`。

- [ ] **Step 3: 逐项回归 spec §6 的 8 项能力**

人工走查（或读 XAML+日志确认）：hover 自动播/离开停、SetVideo 完整播、SetSegment 区间播+到点停、播放中微调 re-seek、全局单播互斥、缩略图↔播放态切换、进度条拖拽 seek、Unloaded 解绑。
Expected: 8 项全在；连续 hover 多张卡片**不卡死**（对照 v0.6.0 教训）。

- [ ] **Step 4: grep 诊断收尾**

Run：`Get-Content <最新log> | Select-String "[FfmeDiag]|[InlinePlayDiag]|ERR|WRN"`
Expected: `[FfmeDiag] ok`；无与预览相关的 WRN/ERR。

- [ ] **Step 5: （有干净机时）干净 Win10/11 + N 版 e2e**

装安装包到干净 Win10/11（无 HEVC 扩展）→ 导入 HEVC → hover 预览能播 → 全链路导出。N 版机重复。
Expected: 全绿。无干净机时跳过并在发版 notes/记忆里标注「待干净机验」。

---

## Self-Review 记录

- **Spec 覆盖**：§1 根因→Task 0/4；§3 选型→Task 1；§4.3 初始化→Task 3；§4.4 防卡死→Task 4 Step 4；§4.5 降级→Task 4 Step 3；§5 打包→Task 1+Task 0 Step 3/4；§6 八项能力→Task 5 Step 3；§8 验证门槛→Task 0 + Task 5。全覆盖。
- **占位扫描**：FFME 精确 API 故意推迟到 Task 0 Step 6 钉死（机器离线无法预先确认，已显式标注「以实测为准」），非偷懒占位。
- **类型一致**：`RequiredFfmpegLibPrefixes` / `ProbeFfmpegLibs` 在 Task 2 定义、Task 3 使用，签名一致。
- **风险**：FFME/.NET8 或 HEVC 冒烟不过 → §9 回退「自建 ffmpeg.exe 渲染器」，spec 已留回退路径。
