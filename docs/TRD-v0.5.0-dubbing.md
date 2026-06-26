# TRD — v0.5.0「分镜级 AI 配音」Windows 端技术落地方案

> 对应 PRD：GitHub issue **#10**（`[PRD] v0.5.0「分镜级 AI 配音」`）。
> 参考实现：macOS `mixed_cut` tag `v0.5.0`（commit `f8993d1`）真源码（已逐文件比对，非猜测）。
> 本文是 **TRD（技术方案）**，回答 PRD 留给 Windows 自定的「用什么引擎、怎么落到现有 C#/WPF 架构、怎么打包、怎么验证」。
> 阅读对象：Windows 端工程师。读完应能据此分期实施，且不重走 macOS 已踩过的坑。

---

## 0. 一句话结论：引擎选型（与 macOS 完全同源）

| 能力 | 引擎 / API | 调用方式 | 国内可访问 | 打包 / 依赖 |
|---|---|---|---|---|
| **台词改写**（LLM） | 现有 `AIProviderManager`（千问/DeepSeek/Claude…） | 复用 `OpenAICompatibleClient.GenerateJsonAsync` | ✅ 现成 | 无新增 |
| **声音克隆（音色注册）** | 阿里百炼 `qwen-voice-enrollment` | **HTTP POST**，参考音频 **base64 内联** | ✅ DashScope 国内直连 | 无新增（复用 qwen key） |
| **克隆音色 TTS 合成** | 阿里百炼 `qwen3-tts-vc-2026-01-22` | **HTTP POST**，返回 OSS 音频 URL | ✅ 同上 | 无新增 |
| **人声 / BGM 分离** | **demucs.cpp**（C++/ggml，whisper.cpp 同款） | 内置二进制 `demucs.exe` | ⚠️ 模型在 HF，**必须换 hf-mirror 镜像** | 需打包 win-x64 二进制 + ~80MB 模型按需下载 |
| **ASR 重识别** | 阿里百炼 `paraformer-realtime-v2` | **WebSocket** 流式（传本地 PCM，不传文件 URL） | ✅ DashScope 国内直连 | 无新增（复用 qwen key） |
| **字幕渲染** | **GDI+/DirectWrite → PNG → ffmpeg overlay** | 自渲染，不依赖 libass | ✅ 系统自带 | 无新增（内置 ffmpeg 不需 libass） |
| **时长对齐 / 切片 / 混音 / 拼接 / 编码** | 内置 ffmpeg | 复用 `FFmpegRunner` | ✅ 现成 | 无新增 |

> **关键利好**：macOS 把克隆 TTS + 音色注册都做成了**普通 HTTP**（只有被 PRD 砍掉的预设音色 CosyVoice 走 WebSocket）。所以 Windows 端**只需实现 1 个 WebSocket 客户端**（ASR paraformer），其余全是 HTTP，C# `HttpClient` 直接搞定。
> **唯一新增 native 依赖 = demucs.cpp**，按 CLAUDE.md §双平台兼容铁律必须做 PE import 静态分析（它用 OpenMP，依赖 `vcomp140.dll`，我们 v0.4.0 起已打包）。

---

## 1. PRD ↔ macOS v0.5.0 代码的两处分歧（已澄清，按 PRD 走）

实施时若直接照搬 macOS 代码会被这两处误导，PRD 是更晚的最终决策：

| 分歧点 | macOS v0.5.0 代码现状 | PRD 最终决策（Windows 照此） |
|---|---|---|
| **音色来源** | 代码同时有 `CosyVoiceCatalog`（预设音色）+ 克隆两条路；`DubbingViewModel` 里 `tts`(CosyVoice 预设) 与 `vcClient`(qwen 克隆) 并存 | **只做克隆**（§决策1）。Windows **不实现** CosyVoice 预设音色、不实现音色选择 UI、不实现试听音色样例。只保留 `qwen-voice-enrollment` + `qwen3-tts-vc` 克隆链路 |
| **定格补帧** | `SegmentDub` / `AlignmentPlan` 有 `freezePadFrames` 字段 | 代码里 `AudioAligner.plan()` **已恒返回 `freezePadFrames=0`**，与 PRD §决策5「绝不定格延长画面」一致。字段保留只为避免迁移，Windows 同样建表保留字段但永远写 0 |

> 直接收益：Windows 端比 macOS 代码**更简单**——砍掉预设音色后，`SpeechRatePlanner`（CosyVoice rate 对齐）也不需要，克隆路只用 `atempo` 对齐。

---

## 2. 复用现有 Windows 代码的映射

现有架构（已盘点，见 `src/MixCut/`）几乎全部可直接复用，配音是「增量挂载」而非重写：

| 现有组件 | 复用方式 |
|---|---|
| `Models/Segment.cs`（已有 `Text` 台词、`StartFrame/EndFrame`、`Fps`） | 直接作为改写输入源；新增导航属性 `SegmentDubs` |
| `Models/Video.cs`（已有 `Transcript`/`AsrWords`/`ContentHash`） | 新增 `ClonedVoiceId` 等字段 |
| `Models/MixScheme.cs` + `SchemeSegment.cs`（已有 `Position` 序列） | `SchemeSegment` 新增 `SelectedSegmentDubId` |
| `Services/AI/OpenAICompatibleClient.cs`（三层 JSON 解析 + 重试） | 台词改写直接复用 `GenerateJsonAsync<RewriteResultDto>` |
| `Services/AI/PromptLoader.cs` + `Resources/Prompts/` | 新增 `script_rewrite_prompt.md` |
| `Services/AI/AIProviderManager.cs`（DashScope/qwen key、DPAPI 加密） | 克隆/TTS/ASR 复用同一把 qwen key（`HasApiKey(Qwen)` / `GetApiKey(Qwen)`） |
| `Services/VideoProcessing/FFmpegRunner.cs`（命令拼装/进度/probe/超时） | 分离取音、切片、atempo、混音、PNG overlay、concat 全走它 |
| `Services/Export/ExportService.cs` | **不改**；新增独立 `DubExportService`（两阶段管线），普通导出不受影响 |
| `Infrastructure/ConcurrencyPolicy.cs`（GPU 感知并发） | 组合导出并发复用 `MaxExportConcurrency`；分离/合成另立轻量串行槽 |
| `Infrastructure/HardwareEncoderProbe.cs`（NVENC/QSV/AMF） | 中间片编码器选型（注意中间片参数必须统一，见 §6） |
| `Infrastructure/BundledBinaries.cs` + `ChildProcessTracker.cs` | 新增定位 `demucs.exe`；demucs 子进程必须 `AddProcess()` 防孤儿 |
| `Services/ASR/ASRService.cs`（hf-mirror 镜像 + 断点续传下载） | demucs 模型下载复用这套国内镜像 + Range 续传模式 |
| `Utilities/AppPaths.cs` | 新增 `StemsDirectory(hash)` / `DubAudioDirectory` / `DemucsModelsDirectory` / `ScrubCache`(已有) |

---

## 3. 数据模型变更（EF Core 迁移）

新增 1 个实体 + 3 处字段扩展。全部 POCO，沿用现有 `MixCutDbContext` + `IDbContextFactory` 短上下文模式。

### 3.1 新增实体 `SegmentDub`（= 分镜 × 改写版 × 音色）
对齐 macOS `SegmentDub.swift`：

```csharp
public class SegmentDub {
    public Guid Id { get; set; }
    public Guid SegmentId { get; set; }            // FK → Segment
    public Segment Segment { get; set; }
    public string VoiceId { get; set; } = "";      // 克隆音色 id
    public string VoiceProvider { get; set; } = "qwen";
    public int TextVariantIndex { get; set; }      // 第几个改写版 0..K-1（→ 字母 A/B/C）
    public string RewrittenText { get; set; } = "";
    public string? AudioFilePath { get; set; }     // 按需生成，初始 null（= 未生成）
    public double AudioDuration { get; set; }       // atempo 对齐后的实际时长（展示用）
    public double AtempoFactor { get; set; } = 1.0;
    public int FreezePadFrames { get; set; }        // 恒 0（见 §1）
    public double TrailingSilence { get; set; }
    // 失效追踪（生成那刻快照；边界/文本变了即"过期"）
    public int GeneratedForStartFrame { get; set; } = -1;
    public int GeneratedForEndFrame { get; set; } = -1;
    public string GeneratedForTextHash { get; set; } = "";
    public string StatusRaw { get; set; } = "pending";  // pending/generated/failed
}
```

`Segment` 加导航属性 + 计算属性 `EffectiveDubVariants`（仅取已生成音频、按 `TextVariantIndex` 去重、优先克隆声）——对齐 macOS `Segment.effectiveDubVariants`。

### 3.2 `Segment` 扩展字段（字幕处理 + 保留原声）
```csharp
public bool IsVoiceLocked { get; set; }          // 🔒 保留原声
public bool HasHardSubtitle { get; set; }        // 是否做字幕区域遮挡
public string MaskStyleRaw { get; set; } = "blur"; // blur / solid
public string MaskRectJson { get; set; }         // 遮挡框归一化坐标 (x,y,w,h ∈ 0..1)
```
> UI 层用 `SubtitleTreatment` 枚举（direct/blur/solid）映射到 `(HasHardSubtitle, MaskStyleRaw)`，对齐 macOS `DubEnums.swift`（半透明 dim 已砍，旧数据归入 blur）。

### 3.3 `Video` 扩展字段
```csharp
public string? ClonedVoiceId { get; set; }       // 注册成功的克隆音色 id（按 ContentHash 复用）
// macOS 还有 selectedVoiceIds / dubSpeechRate —— clone-only 下 selectedVoiceIds 恒 =[ClonedVoiceId]，
// dubSpeechRate 是 CosyVoice 专用，Windows 砍预设音色后不需要。可不建。
```

### 3.4 `SchemeSegment` 扩展字段
```csharp
public Guid? SelectedSegmentDubId { get; set; }  // 该槽选中的配音变体；null = 原声（默认）
```

> 迁移注意（坑 11）：MixCut 用专属 `%APPDATA%\MixCut\mixcut.db`，新增表/列走 EF Core migration，不影响既有数据。

---

## 4. 新增服务层（按引擎，附确切 API 契约）

全部放 `Services/Dubbing/`（新目录）。下列 API 契约均从 macOS 真源码提取，**但 §10 仍要求对真实 API 实跑验证后再大规模实现**。

### 4.1 `VoiceCloneService`（音色注册）— HTTP POST
- Endpoint：`https://dashscope.aliyuncs.com/api/v1/services/audio/tts/customization`
- Header：`Authorization: Bearer <qwen key>`，`Content-Type: application/json`
- Body：
  ```json
  {"model":"qwen-voice-enrollment",
   "input":{"action":"create","target_model":"qwen3-tts-vc-2026-01-22",
            "preferred_name":"mixcut<hash前8>",
            "audio":{"data":"data:audio/mpeg;base64,<参考mp3的base64>"}}}
  ```
- 返回：`output.voice` = 克隆音色 id（落 `Video.ClonedVoiceId`）。
- `preferred_name` 清洗：仅小写字母+数字，≤20 字符。
- **参考音频 = demucs 分离出的人声前 6 秒转 mp3**（坑 4：长参考会让 TTS「续读参考内容」，6s 实测 5/5 干净，18s 仅 1/3）。

### 4.2 `CloneTtsClient`（克隆音色合成）— HTTP POST
- Endpoint：`https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation`
- Body：`{"model":"qwen3-tts-vc-2026-01-22","input":{"text":<台词>,"voice":<voiceId>,"language_type":"Chinese"}}`
- 返回：`output.audio.url`（多为 OSS `http://`，**升级成 `https://`**，OSS V1 预签名不含 scheme 不影响签名）→ 下载 wav → `ffprobe` 测时长。
- **跑飞兜底（坑 4）**：克隆 TTS 偶发把音频念得过长/混入参考。`SynthesizeCloneRobust`：最多合成 3 次取**最短**；某次时长 ≤ `字数 × 0.28s` 视为干净即提前返回。

### 4.3 `VocalSeparationService`（人声/BGM 分离）— 内置 demucs.cpp
- 二进制：`bin/demucs.exe`（win-x64 build of demucs.cpp）。调用：`demucs.exe <model.bin> <input.wav> <outDir>`。
- 模型：`ggml-htdemucs-4s.bin`（~80MB）。
  - macOS 源：`https://huggingface.co/datasets/Retrobear/demucs.cpp/resolve/main/ggml-model-htdemucs-4s-f16.bin`
  - **Windows 必须换国内镜像**：`https://hf-mirror.com/datasets/Retrobear/demucs.cpp/resolve/main/ggml-model-htdemucs-4s-f16.bin`（复用 `ASRService` 的「国内镜像 3 次 → 国际源 3 次 + Range 续传」下载逻辑）。
- 流程：① ffmpeg 抽整轨 `-ac 2 -ar 44100 pcm_s16le` wav → ② demucs 出 4 stems(drums/bass/other/vocals) → ③ `BGM = amix(drums,bass,other, normalize=0)`，`vocals` 直接取 → 落 `StemsDirectory(hash)/{vocals.wav, bgm.wav}`。
- **整轨只分离一次并按 `ContentHash` 缓存**；克隆取人声前 6s 做参考，导出按分镜切 bgm 混音。
- demucs 子进程**必须挂 `ChildProcessTracker`**。

### 4.4 `ParaformerAsrClient`（ASR 重识别）— WebSocket
- Endpoint：`wss://dashscope.aliyuncs.com/api-ws/v1/inference/`
- Header：`Authorization: Bearer <qwen key>`，`X-DashScope-DataInspection: enable`
- 模型 `paraformer-realtime-v2`，协议：`run-task`(format=pcm, sample_rate) → 流式 send 本地 PCM 二进制帧 → `finish-task` → 累积 sentences。
- C# 用 `System.Net.WebSockets.ClientWebSocket`。协议 JSON 逐字段参考 macOS `Sources/MixCutCore/ParaformerASR.swift`（`runTaskJSON`/`finishTaskJSON`/`accumulateTranscript`）——该文件是纯逻辑、有单测，可 1:1 翻译。
- **只替换台词文本，不动分镜边界**（PRD §八）。整片重识别在导入页、单分镜 ↻ 在分镜库。

### 4.5 `ScriptRewriteService`（台词改写）— 复用现有 LLM 客户端
- 复用 `AIProviderManager.currentProvider()` + `GenerateJsonAsync`。
- Prompt 模板新增 `Resources/Prompts/script_rewrite_prompt.md`（从 macOS `script_rewrite_prompt.md` 移植）。硬约束（坑 9 + §决策10）：
  - 字数贴近原台词、**不得更短**；
  - 语义/价格指向/时间/因果/数量/比较方向**绝不反转**；
  - 保留关键事实（产品名/价格/数字/卖点）；
  - **禁用句末语气词**（啊呀哇呢吧嘛啦咯哦噢）。
- 5 套差异化风格池（口语活泼/干练直接/场景痛点/信任背书/限时促销），变体数 1~5 时按 `k % 5` 取模复用。
- 改写结果按 `segmentId` 映射回；AI 漏返回的回退原台词。逻辑（`RewritePromptBuilder` / `RewriteResultMapper` / `CharBudget`）在 macOS `Sources/MixCutCore/` 有纯函数 + 单测，可直接翻译。

### 4.6 `DubAudioFinalizer` + `AudioAligner`（时长对齐）
- `AudioAligner.Plan(targetDuration, audioDuration, fps)` → `AlignmentPlan{atempoFactor, freezePadFrames=0, trailingSilence}`：
  - 偏长 → `atempo = audioDuration/targetDuration`（**无封顶**，压到正好等于画面，绝不超过）；
  - 偏短 ≤0.15s → 不变速；偏短较多 → `atempo = max(ratio, 0.8)`（最多放慢 25%），残差末尾补静音。
- `DubAudioFinalizer`：对 TTS wav 套 atempo（**>2.0 需链式拆成多段 `atempo=2.0,atempo=...` 相乘**）→ 转 AAC/m4a 落 `DubAudioDirectory`。`atempoChain()` 逻辑见 macOS `DubAudioFinalizer.swift`，可直接翻译。
- 对齐结果驱动变体卡时长颜色（绿=贴合 / 橙=不对齐）。

---

## 5. 字幕渲染（Windows 专属，libass-free）

PRD 坑 5：内置 ffmpeg 没有 libass，不能直接 `subtitles=` 烧字。macOS 用系统原生文字渲染成透明 PNG 再 overlay，**且必须按目标像素显式渲染**（否则高 DPI 被 2× 放大、字糊）。

Windows 方案：`CaptionRenderer`（新）用 **GDI+ (`System.Drawing`) 或 DirectWrite/Skia** 把台词渲染成 RGBA PNG（带半透明底，纯色遮挡模式不带底），再 `ffmpeg overlay`：
- 画布宽 = `outW × 0.9`，居中底部布局（对齐 macOS `CaptionLayout.overlayOrigin`）。
- **按输出像素渲染**（不要按逻辑点），WPF/GDI+ 注意 DPI：用固定像素画布，不要让系统 DPI 缩放介入。
- 遮挡框 + 字幕叠加由滤镜图 `DubSegmentGraphBuilder`（新，移植 macOS `Sources/MixCutCore/DubSegmentGraph.swift`，纯字符串拼接 + 单测）生成 `filter_complex`：
  - `none`：不处理；
  - `blur`：对 maskRect 区域 `boxblur`/`gblur` 后叠字；
  - `solid`：深色矩形 `drawbox` 盖住后叠字（不需要 PNG 底）。

> `System.Drawing.Common` 在 .NET 8 仅 Windows 受支持——本项目就是 `net8.0-windows`，OK；但需确认 publish 后无额外 native 依赖（PE import 扫描）。若有顾虑可用 SkiaSharp（自带，跨平台，更可控）。

---

## 6. 导出管线（最硬的一段，坑 1/2/3 全在这）

新增 `DubExportService`（两阶段，**不动现有 `ExportService`**），移植 macOS `DubExportService.swift`：

**阶段一 · 逐分镜中间片**（`renderSegment`）：
- 输入编号：input0=源视频，依次追加 字幕PNG / 配音m4a / BGM wav。
- 滤镜图按 `(maskMode, captionOrigin, keepOriginalAudio, dubAudio, bgm)` 组装。
- 音频：锁定/回退段保留原声；配音段 = `amix(克隆配音, 按分镜切的BGM)`（坑 3：BGM 必须混回，人声为主 BGM 垫底）。
- **编码参数所有中间片必须完全一致**（阶段二要 `-c copy`）：固定一种硬件编码器（NVENC/QSV/AMF 由 `HardwareEncoderProbe`，但**全片统一一个**）+ `-pix_fmt yuv420p` + `-tag:v avc1`。
- **`-c:a aac -ar 44100 -ac 2`（坑 1 核心）**：强制所有中间片立体声 44.1k。否则原声段(立体声) vs 配音段(单声道人声/amix) 声道数不一致，concat `-c copy` 以首段为准 → 声道不符的段**被播成静音**。

**阶段二 · 拼接 + 首帧归零**（`concatCopy`）：
1. `ffmpeg -f concat -safe 0 -i list.txt -c copy joined.mp4`。
2. `ffprobe` 读 `v:0` 的 `start_time`；若 >0.001 → `ffmpeg -i joined -c copy -output_ts_offset -<start_time> out.mp4`。
   - **坑 2**：concat 把首段 AAC 编码 priming 延迟转成视频起始时间偏移(~0.023s)，t=0 处无帧 → 平台截首帧做封面得黑图。**注意：用 ffmpeg 抽帧能抽到内容会误判没事，必须用播放器/系统取帧 API 在 t=0 验证**（Windows 用 `MediaPlayer`/WIC 或直接看资源管理器缩略图）。

**组合展开**（`SchemeComboPlanner`，移植 macOS）：
- 笛卡尔积：每非锁定槽选项数 = `1 + EffectiveDubVariants.Count`，锁定槽 ×1。
- 单方案封顶 `maxCombos=256` 防爆炸；超出 `truncated=true`，**必须 `log` 告知被截断**（CLAUDE.md：不许静默 truncation）。
- 文件名后缀 `[原·A·原·B]`（原=原声、字母=改写版按镜序）。**Windows 非法字符过滤** `\ / : * ? " < > |`，UTF-8。

**导出页交互**（PRD §七）：唯一按钮「🎤 导出配音组合（共 N 条）」+ 确认弹窗 + 选输出目录 + 自适应并发（`ConcurrencyPolicy.MaxExportConcurrency`，给系统留余量）+ 红色「⏹ 停止导出」（`CancellationToken` → 杀在跑转码、删半成品、保留已完成）。

---

## 7. UI 层（VM + View 新增/改造）

| 页面 | 新增/改造 | 对齐 macOS |
|---|---|---|
| **分镜素材库** 每视频分组顶部「配音设置条」 | 新 `DubSettingsBar` 控件：变体数 Stepper(1~5)、「克隆并改写配音」按钮、状态区（✓N变体 / 忙碌进度）。**每视频独立**（坑 12，状态按 `videoId` 分桶，禁止全局联动） | `DubSettingsBar.swift` |
| **分镜卡片** 配音控件 | 「🔒保留原声」开关 + 字幕处理三胶囊(直接烧录/模糊虚化/纯色遮挡) + 「遮挡区应用到所有分镜」+ 9:16 预览上可拖拽遮挡框 | `SegmentDubControls.swift` / `SubtitleMaskOverlay.swift` |
| **配音变体检视器**（单击分镜右侧 ~340px） | 新面板：保留原声态/空白态/变体池态；每版卡(改写版A胶囊+字数提示+编辑✏/删除🗑+生成/播放/重生成+时长色)；「+手动添加一版」；「↻重新改写本分镜」 | `DubbingViewModel` 驱动 |
| **混剪方案 → 分镜序列** | 每卡加配音变体下拉(4 态：原声锁定/无改写/有改写默认原声/已选某版)；**默认原声**(决策4)；顶部「⊞ 可生成 N 个组合」提示；选项变预览实时换声 | `MixScheme` + `SelectedSegmentDubId` |
| **导出页** | 唯一「导出配音组合(共N条)」按钮 + 确认弹窗 + 进度/完成/停止 | `DubExportService` |
| **素材导入页 + 分镜库** | 「重新识别(阿里云 ASR)」按钮 | `ParaformerAsrClient` |
| **设置页** | 无新增引擎配置（复用 qwen key）；可在系统信息区显示 demucs 模型是否就绪 | — |

`DubbingViewModel`（新，移植 macOS）：`ensureClonedVoice` / `rewriteAll` / `rewriteSegment` / `addManualVariant` / `updateVariantText` / `deleteVariant` / `generateAudio` / `generateAllAudio` / `ensureSelectedAudio`。状态按 `videoId` 分桶（`busyVideoIDs`/`videoProgress`），严守 PRD「每视频独立」。

> **遵守 CLAUDE.md「数据变更→UI 刷新」铁律**：新增 `DubsChanged` 事件，失效 `SegmentLibrary`/`Schemes`/`Export` 缓存（对照 §F 通道清单）。`IProjectView.LoadProject` 切项目时重置配音忙碌态/检视器选中。

---

## 8. 国内可访问性 + 打包（CLAUDE.md 铁律）

- **demucs.exe**：需要一个 win-x64 的 demucs.cpp 构建。
  - 必做 PE import 静态分析（`Get-PeImports demucs.exe`）→ 对比 `publish/bin/`，补齐缺失 DLL。demucs.cpp 用 OpenMP → 依赖 `vcomp140.dll`（v0.4.0 已打包），仍要实扫确认。
  - 子进程挂 `ChildProcessTracker`。
- **demucs 模型**：~80MB，首次用配音时下载，走 **hf-mirror 国内镜像**（§4.3），复用 `ASRService` 的镜像+Range 续传+进度 UI。落 `%LOCALAPPDATA%\MixCut\demucs-models\`（大文件同 whisper 模型 tier）。
- **三个云 API（克隆/TTS/ASR）**：全 DashScope，国内直连，复用 qwen key——**无新增国内可访问性风险**。
- **装上即跑**：除 demucs 二进制 + 模型外无新依赖；ffmpeg/字幕渲染全自带。安装包体积新增 ≈ demucs.exe（几 MB）；模型按需下载不进安装包。

---

## 9. 风险登记

| 风险 | 影响 | 缓解 |
|---|---|---|
| **demucs.cpp 没有现成可信 win-x64 build** | 分离能力落地受阻 | 自行 CMake 构建（ggml + OpenMP），或找社区 release；构建后 PE 扫描验依赖。**列为第 0 步预研** |
| 阿里百炼 API 契约/模型名随时间变化（`qwen3-tts-vc-2026-01-22` 带日期） | 克隆/合成失败 | §10 第 0 步对真实 API curl 验证；模型名做成可配置常量 |
| 克隆 TTS「续读参考」跑飞 | 配音混入杂音 | 6s 短参考 + 合成 3 次取最短（已纳入 §4.2） |
| 中间片编码参数不统一 → concat `-c copy` 失败/花屏 | 导出坏片 | 全片统一一个编码器 + 固定 `pix_fmt/ac/ar`（§6） |
| 首帧黑（坑 2） | 平台封面黑图 | `-output_ts_offset` 归零 + **播放器 t=0 取帧验证**（不能只用 ffmpeg 抽帧） |
| GPU 厂商分支（NVENC/QSV/AMF）行为差异 | 某卡导出失败 | 构建机只能测一种 GPU，N 卡/A 卡需用户实跑（CLAUDE.md 死规矩） |
| `System.Drawing` publish 后 native 依赖 | 字幕渲染崩 | 优先用 SkiaSharp；或对渲染路径做干净机验证 |

---

## 10. 第 0 步：必须先实跑验证的清单（不验证不大规模写）

按 CLAUDE.md「自验证铁律」+「外部进程 codec/option 必须真实路径自验」，**在 Windows 构建机上用真实 qwen key 先把下列烟测跑绿**，再进入正式实现：

1. **音色注册**：curl `audio/tts/customization`（model=`qwen-voice-enrollment`，base64 6s mp3）→ 拿到 `output.voice`。确认契约/字段名/模型名仍有效。
2. **克隆合成**：curl `aigc/multimodal-generation/generation`（model=`qwen3-tts-vc-2026-01-22`，voice=上一步 id）→ 拿到 `output.audio.url` → 下载能播。
3. **paraformer WS**：`ClientWebSocket` 连 `api-ws/v1/inference/`，run-task→送一段 16k PCM→finish-task，能收到带文本的 sentences。
4. **demucs.exe**：在干净路径跑 `demucs.exe model.bin in.wav outdir`，产出 4 stems；PE import 扫描确认依赖 DLL 全在 `publish/bin/`。
5. **导出管线烟测**：构造 2 段（1 原声段立体声 + 1 配音段 amix）→ 中间片 `-ac 2` → concat `-c copy` → `-output_ts_offset` 归零 → **用 Windows 播放器/WIC 在 t=0 取帧确认非黑** + 两段都有声（验坑 1 + 坑 2）。

每步打 `[DubDiag]` 诊断日志，grep 关键计数全对再继续。

### 10.1 P0 实测结果（2026-06-26，国内网络 + 真实 qwen key）
烟测脚本见 `scripts/smoke/`（`run-all.ps1` 一键跑）。已通过：

| 烟测 | 结果 | 实测确认的契约事实 |
|---|---|---|
| ① 音色注册 | ✅ | 返回 `output.voice` 形如 `qwen-tts-vc-mixcut<hash>-voice-<时间戳>-<4位>`，契约/模型名 `qwen-voice-enrollment` + `qwen3-tts-vc-2026-01-22` 现仍有效 |
| ② 克隆合成 | ✅ | 返回 `output.audio.url`（OSS `http://dashscope-result-bj...`，需升 https），下载 wav 时长正常（实测台词 4.72s）|
| ③ paraformer ASR | ✅ | WS 协议逐字段有效；75s/92s 真实广告完整识别成中文全文 |
| ④ demucs 分离 | ✅ | win-x64 demucs.cpp 已自建（见 `docs/demucs-build.md`），4 stems + BGM 正常；依赖 PE 扫描无缺失；模型走 hf-mirror。CPU ~9x 实时（75s→680s），按 hash 缓存缓解 |
| ⑤ 导出管线 | ✅ | 本机 libx264 真跑：`start_time 0.0230→0.0000`（坑2 复现并修复）、双段 -24/-21dB 都有声（坑1）。剩真实 GPU 编码器复验 |

**实现期必带的两条经验教训（P0 踩到）**：
1. **请求体必须 UTF-8**：PowerShell 5.1 `Invoke-RestMethod` 默认非 UTF-8 发 body，中文台词被打乱 → 合成报 `InvalidParameter: invalid text`。C# 用 `HttpClient` + `StringContent(json, Encoding.UTF8, "application/json")` 天然 OK，但**务必显式 UTF-8**，勿用默认编码。
2. **ASR header 用小写 `bearer`**：mac 源码 `Authorization: bearer <key>`（注意小写），实测可用；WS 另需 `X-DashScope-DataInspection: enable`。

---

## 11. 分期交付建议（每期独立可验、不破坏已有功能）

| 期 | 范围 | 验收 |
|---|---|---|
| **P0 预研** | 第 0 步 5 个烟测全绿 + demucs win-x64 build 落地 | 5 条 `[DubDiag]` 日志 |
| **P1 数据层** | `SegmentDub` 实体 + Segment/Video/SchemeSegment 字段 + EF 迁移 + `DubsChanged` 事件 | 迁移成功、切项目不串数据 |
| **P2 克隆+改写+对齐** | `VocalSeparationService` / `VoiceCloneService` / `CloneTtsClient` / `ScriptRewriteService` / `DubAudioFinalizer`；分镜库「配音设置条」+「变体检视器」 | 一键改写 → 变体池有音频可试听、时长对齐 |
| **P3 分镜卡控件** | 保留原声开关 + 字幕处理三胶囊 + 可拖拽遮挡框 + 应用到所有分镜 | 状态正确落库、每视频独立 |
| **P4 字幕渲染 + 导出** | `CaptionRenderer` + `DubSegmentGraphBuilder` + `DubExportService` + 组合展开 + 导出页 | 组合导出 N 条、BGM 在、首帧不黑、无静音段、字幕三模式正确 |
| **P5 方案选变体** | 分镜序列下拉 + 默认原声 + 组合数提示 + 预览换声 | 默认原声、选项实时换声、方案列表不被炸开 |
| **P6 ASR 重识别** | `ParaformerAsrClient` + 导入页/分镜库入口 | 重识别只换文本不动边界 |

> 每期完成走 CLAUDE.md 标准自验证流水线（build → sync → publish → 启动 grep 诊断日志），P4 必须在干净机/真实 GPU 上跑完整导出 e2e。

---

## 附：macOS 参考文件索引（实施时逐一对照翻译）

| Windows 要写 | macOS 参考（`/tmp` clone 或 GitHub `mixed_cut@v0.5.0`） |
|---|---|
| `VocalSeparationService` | `MixCut/Services/TTS/VocalSeparationService.swift` |
| `VoiceCloneService` | `MixCut/Services/TTS/VoiceCloneService.swift` |
| `CloneTtsClient` | `MixCut/Services/TTS/QwenTTSClient.swift`（model=vc） |
| `ParaformerAsrClient` | `MixCut/Services/ASR/QwenASRClient.swift` + `Sources/MixCutCore/ParaformerASR.swift` |
| `ScriptRewriteService` | `MixCut/Services/AI/ScriptRewriteService.swift` + `Sources/MixCutCore/Rewrite*` + `CharBudget` |
| `AudioAligner`/`DubAudioFinalizer` | `Sources/MixCutCore/AlignmentPlan.swift` + `Services/TTS/DubAudioFinalizer.swift` |
| `DubExportService`/`SchemeComboPlanner` | `MixCut/Services/Export/DubExportService.swift` + `Sources/MixCutCore/{DubSegmentGraph,DubStaleness}.swift` |
| `CaptionRenderer`/字幕布局 | macOS 系统文字渲染（Windows 改 GDI+/Skia）+ `CaptionLayout` |
| `DubbingViewModel` | `MixCut/ViewModels/DubbingViewModel.swift` |
| 配音 UI 控件 | `MixCut/Views/SegmentLibrary/{DubSettingsBar,SegmentDubControls,SubtitleMaskOverlay}.swift` |
| 数据模型 | `MixCut/Models/{SegmentDub,DubEnums}.swift` + `Sources/MixCutCore/SubtitleMask*.swift` |

> 这些 macOS 文件中 `Sources/MixCutCore/` 下的多为**纯逻辑 + 带单测**（`Tests/MixCutCoreTests/`），可几乎 1:1 翻译成 C# 并连同测试一起移植，风险最低。
