# scripts/smoke/smoke-demucs.ps1
# P0 烟测 ④ —— demucs.cpp 人声/BGM 分离实跑 + PE import 静态分析。
# 调用契约（对齐 mac VocalSeparationService.swift）：demucs.exe <model.bin> <input.wav> <outDir>
#   产出 4 stems：target_0_drums / target_1_bass / target_2_other / target_3_vocals .wav
#   BGM = amix(drums,bass,other, normalize=0)；vocals 直接取
#
# 前置（P0 交付物）：win-x64 的 demucs.exe + ggml-htdemucs-4s.bin 模型。二者缺失时本脚本只做
# 「报告缺失 + 对已有 exe 做 PE 扫描」，不算失败——提示这是 P0-build 待办。
#
# 用法：
#   powershell -File smoke-demucs.ps1 -Audio "C:\一段有人声有BGM的视频.mp4" `
#       -DemucsExe "C:\path\demucs.exe" -Model "C:\path\ggml-htdemucs-4s.bin"
# 省略 -DemucsExe / -Model 时自动找 publish\bin\demucs.exe 与 %LOCALAPPDATA%\MixCut\demucs-models\

param(
    [string]$Audio,
    [string]$DemucsExe,
    [string]$Model
)

. "$PSScriptRoot\_common.ps1"

$ffmpeg = Resolve-Ffmpeg ffmpeg
$root   = Get-RepoRoot

# --- 定位二进制与模型 --------------------------------------------------------
if (-not $DemucsExe) { $DemucsExe = Join-Path $root 'publish\bin\demucs.exe' }
if (-not $Model)     { $Model = Join-Path $env:LOCALAPPDATA 'MixCut\demucs-models\ggml-htdemucs-4s.bin' }

Write-DubDiag "=== 烟测 ④ demucs 人声分离 开始 ==="
$haveExe   = Test-Path $DemucsExe
$haveModel = Test-Path $Model

if ($haveExe) {
    Write-DubDiag "找到 demucs.exe：$DemucsExe，做 PE import 扫描 …"
    & "$PSScriptRoot\Get-PeImports.ps1" -Path $DemucsExe -CompareDir (Join-Path $root 'publish\bin')
} else {
    Write-DubDiag "未找到 demucs.exe（$DemucsExe）—— 这是 P0 交付物：需先获取/构建 win-x64 demucs.cpp 二进制" 'ERR'
}
if (-not $haveModel) {
    Write-DubDiag "未找到模型（$Model）—— 首次用配音时走 hf-mirror 国内镜像下载（~80MB）：" 'ERR'
    Write-DubDiag "  https://hf-mirror.com/datasets/Retrobear/demucs.cpp/resolve/main/ggml-model-htdemucs-4s-f16.bin"
}

if (-not ($haveExe -and $haveModel)) {
    Write-DubDiag "demucs 二进制/模型未就绪，跳过实跑分离（PE 扫描已给出依赖结论）。补齐后重跑本脚本。" 'ERR'
    return
}
if (-not $Audio -or -not (Test-Path $Audio)) { throw "请提供 -Audio（一段有人声+BGM 的视频/音频）做实跑分离" }

# --- 实跑分离 ----------------------------------------------------------------
$tmp = New-SmokeTempDir 'demucs'
$inWav = Join-Path $tmp 'sep-in.wav'
Write-DubDiag "抽整轨音频 → 44.1k/立体声/pcm_s16le（demucs.cpp 输入要求）…"
& $ffmpeg -y -i $Audio -ac 2 -ar 44100 -c:a pcm_s16le $inWav 2>$null
Assert-True (Test-Path $inWav) "输入 wav 就绪：$inWav" "输入 wav 提取失败"

$outDir = Join-Path $tmp 'stems'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
Write-DubDiag "运行 demucs（较慢，请稍候）…"
$sw = [System.Diagnostics.Stopwatch]::StartNew()
& $DemucsExe $Model $inWav $outDir
$sw.Stop()
Write-DubDiag ("demucs 退出码={0}，耗时 {1:N1}s" -f $LASTEXITCODE, $sw.Elapsed.TotalSeconds)
Assert-True ($LASTEXITCODE -eq 0) "demucs 正常退出" "demucs 退出码非 0（=$LASTEXITCODE）"

$vocals = Join-Path $outDir 'target_3_vocals.wav'
$drums  = Join-Path $outDir 'target_0_drums.wav'
$bass   = Join-Path $outDir 'target_1_bass.wav'
$other  = Join-Path $outDir 'target_2_other.wav'
Assert-True (Test-Path $vocals) "产出人声轨 target_3_vocals.wav" "缺失人声轨（4 stems 命名是否变化？）"
Assert-True ((Test-Path $drums) -and (Test-Path $bass) -and (Test-Path $other)) "产出 drums/bass/other 三轨" "缺失部分 stems"

# --- 合成 BGM（坑3 同源逻辑）------------------------------------------------
$bgm = Join-Path $outDir 'bgm.wav'
& $ffmpeg -y -i $drums -i $bass -i $other -filter_complex "amix=inputs=3:normalize=0" -c:a pcm_s16le $bgm 2>$null
Assert-True (Test-Path $bgm) "BGM 轨合成成功（drums+bass+other）：$bgm" "BGM 合成失败"

Write-DubDiag "=== 烟测 ④ 通过 ✅ demucs 分离链路有效 ===" 'OK'
Write-Host "请人工试听：人声 $vocals / 背景 $bgm" -ForegroundColor Cyan
