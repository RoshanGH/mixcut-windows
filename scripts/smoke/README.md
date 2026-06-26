# P0 预研烟测 — 分镜级 AI 配音（v0.5.0）

对应 `docs/TRD-v0.5.0-dubbing.md` §10「第 0 步：必须先实跑验证的清单」。
**这些脚本就是验证手段本身**：阿里百炼的 API 契约/模型名只能从 mac 源码提取、无法离线复核，
必须在**国内网络 + 真实 qwen key** 下实跑验证通过，再进入正式实现（P1+）。

## 前置

```powershell
# 1) 设置阿里百炼(千问) API Key —— 与应用「设置」里的千问 Key 同一把。绝不写进脚本/提交。
$env:DASHSCOPE_API_KEY = 'sk-xxxx'

# 2) 确保内置 ffmpeg 已就绪（脚本自动找 publish\bin 或 src\MixCut\Resources\bin）
```

## 一键跑齐

```powershell
powershell -ExecutionPolicy Bypass -File scripts\smoke\run-all.ps1 `
    -RefAudio "D:\素材\一条有人声的口播广告.mp4" `
    -AsrAudio "D:\素材\一段清晰中文语音.mp4" `
    -SepAudio "D:\素材\有人声有BGM的视频.mp4"
```

未提供某素材则对应烟测标 `SKIP`。汇总表 + `[DubDiag]` 日志见 `scripts\smoke\dubdiag.log`。

## 单项

| 脚本 | 验证（TRD §10） | 依赖 |
|---|---|---|
| `smoke-clone-tts.ps1 -RefAudio <含人声音视频>` | ①音色注册(`qwen-voice-enrollment`) + ②克隆合成(`qwen3-tts-vc`)，纯 HTTP，参考音 base64 内联 | qwen key |
| `smoke-paraformer-asr.ps1 -Audio <中文语音>` | ③`paraformer-realtime-v2` WebSocket 流式（传本地 PCM，不传文件 URL） | qwen key |
| `smoke-demucs.ps1 [-Audio <有人声有BGM>] [-DemucsExe] [-Model]` | ④demucs.cpp 实跑出 4 stems + PE import 静态扫描 | **demucs.exe + 模型（P0 交付物）** |
| `smoke-dub-export.ps1 [-Encoder libx264]` | ⑤`-ac 2` 声道统一(坑1) + concat 后 `-output_ts_offset` 首帧归零(坑2) + 双段都有声 | 仅 ffmpeg（自造素材） |
| `Get-PeImports.ps1 -Path <exe/dll> [-CompareDir publish\bin]` | 通用 PE 依赖扫描（任何新 native 二进制都先过这关） | — |

## 判定

- 每条 `[DubDiag] [OK]` 才算该步通过；出现 `[ERR]` 即 FAIL，按提示排查。
- **④ demucs** 在二进制/模型未就绪时不算 FAIL，而是提示「P0 交付物待补」——
  需先获取/构建 win-x64 demucs.cpp 二进制，模型走 hf-mirror 国内镜像：
  `https://hf-mirror.com/datasets/Retrobear/demucs.cpp/resolve/main/ggml-model-htdemucs-4s-f16.bin`
- **⑤ 首帧黑** 的硬指标是容器 `start_time≤0.001`（脚本已断言）；
  「平台封面取 t=0」的最终确认请用 Windows 资源管理器缩略图/播放器，**不要只信 ffmpeg 抽帧**（会误判）。
- 全绿 → 进 P1 数据层；任何 FAIL → 修绿再走。
