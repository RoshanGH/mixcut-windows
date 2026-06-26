# scripts/smoke/run-all.ps1
# 一键跑齐 P0 全部烟测，最后打印汇总表 + 把 [DubDiag] 归档到 dubdiag.log。
# 用法：
#   $env:DASHSCOPE_API_KEY = 'sk-xxxx'
#   powershell -File run-all.ps1 -RefAudio "C:\有人声的视频.mp4" -AsrAudio "C:\中文语音.mp4" `
#       [-SepAudio "C:\有人声有BGM.mp4"] [-DemucsExe ...] [-Model ...] [-Encoder libx264]
# 不传 -RefAudio/-AsrAudio 时对应云端烟测会跳过（标 SKIP）。

param(
    [string]$ApiKey,
    [string]$RefAudio,
    [string]$AsrAudio,
    [string]$SepAudio,
    [string]$DemucsExe,
    [string]$Model,
    [string]$Encoder = 'libx264'
)

$ErrorActionPreference = 'Continue'
. "$PSScriptRoot\_common.ps1"
"" | Set-Content $script:DubDiagLog  # 清空本轮日志

$results = [ordered]@{}
function Run-Step([string]$name, [scriptblock]$block, [bool]$enabled = $true) {
    if (-not $enabled) { $results[$name] = 'SKIP'; Write-Host "[$name] SKIP" -ForegroundColor DarkGray; return }
    Write-Host "`n========== $name ==========" -ForegroundColor Cyan
    try { & $block; $results[$name] = 'PASS' }
    catch { $results[$name] = 'FAIL'; Write-Host "[$name] FAIL: $($_.Exception.Message)" -ForegroundColor Red }
}

if (-not $ApiKey -and $env:DASHSCOPE_API_KEY) { $ApiKey = $env:DASHSCOPE_API_KEY }
$keyArg = if ($ApiKey) { @{ ApiKey = $ApiKey } } else { @{} }

Run-Step '①+② 克隆链路'   { & "$PSScriptRoot\smoke-clone-tts.ps1" -RefAudio $RefAudio @keyArg } ([bool]$RefAudio)
Run-Step '③ paraformer ASR' { & "$PSScriptRoot\smoke-paraformer-asr.ps1" -Audio $AsrAudio @keyArg } ([bool]$AsrAudio)
Run-Step '④ demucs 分离'    { & "$PSScriptRoot\smoke-demucs.ps1" -Audio $SepAudio -DemucsExe $DemucsExe -Model $Model }
Run-Step '⑤ 导出管线'       { & "$PSScriptRoot\smoke-dub-export.ps1" -Encoder $Encoder }

Write-Host "`n================ P0 烟测汇总 ================" -ForegroundColor Cyan
foreach ($k in $results.Keys) {
    $v = $results[$k]
    $color = switch ($v) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'DarkGray' } }
    Write-Host ("  {0,-18} {1}" -f $k, $v) -ForegroundColor $color
}
$fail = ($results.Values | Where-Object { $_ -eq 'FAIL' }).Count
Write-Host "`n诊断日志：$script:DubDiagLog" -ForegroundColor Cyan
if ($fail -eq 0) { Write-Host "P0 全部通过/跳过，可进入 P1 数据层。" -ForegroundColor Green }
else { Write-Host "$fail 项 FAIL —— 修绿后再进 P1。" -ForegroundColor Red; exit 1 }
