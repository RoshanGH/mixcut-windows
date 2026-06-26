# scripts/smoke/smoke-dub-export.ps1
# P0 烟测 ⑤ —— 配音导出两阶段管线的核心修复验证（自造素材，不依赖外部文件）。
# 验三件事：
#   坑1：所有中间片统一 -ac 2 -ar 44100 → concat -c copy 后两段都有声（不会因声道数不一致被播成静音）
#   坑2：concat 后 ffprobe v:0 的 start_time>0 → -output_ts_offset 平移回 0 → 复测 start_time≤0.001
#        （首帧黑：平台截 t=0 做封面会黑；注意 ffmpeg 抽帧能抽到内容会误判，硬指标是容器 start_time）
#   双段拼接：用两段不同 testsrc + 不同声道来源（模拟「原声立体声段」+「配音 amix→立体声段」）
#
# 用法：powershell -File smoke-dub-export.ps1
#   可选 -Encoder（默认 libx264 软编，保证任何机器可跑；想验硬件编码传 h264_nvenc/h264_qsv/h264_amf）

param(
    [string]$Encoder = 'libx264'
)

. "$PSScriptRoot\_common.ps1"

$ffmpeg  = Resolve-Ffmpeg ffmpeg
$ffprobe = Resolve-Ffmpeg ffprobe
$tmp     = New-SmokeTempDir 'dubexport'

Write-DubDiag "=== 烟测 ⑤ 配音导出管线 开始（编码器=$Encoder）==="

$W = 540; $H = 960; $DUR = 2   # 9:16 测试分辨率
$encArgs = @('-c:v', $Encoder, '-pix_fmt', 'yuv420p', '-tag:v', 'avc1')
if ($Encoder -eq 'libx264') { $encArgs += @('-preset','veryfast','-b:v','2000k') }
else { $encArgs += @('-b:v','2000k','-maxrate','4000k') }

# 中间片统一音频参数（坑1核心）：-c:a aac -ar 44100 -ac 2
$audArgs = @('-c:a','aac','-b:a','192k','-ar','44100','-ac','2')

# --- 段A：模拟「原声段」= testsrc2 + 立体声 sine -----------------------------
$segA = Join-Path $tmp 'seg_000.mp4'
Write-DubDiag "生成段A（原声/立体声）…"
& $ffmpeg -y -f lavfi -i "testsrc2=size=${W}x${H}:rate=30:duration=${DUR}" `
          -f lavfi -i "sine=frequency=440:duration=${DUR}:sample_rate=44100" `
          -map 0:v -map 1:a @encArgs @audArgs -movflags '+faststart' $segA 2>$null
Assert-True (Test-Path $segA) "段A 生成成功" "段A 生成失败"

# --- 段B：模拟「配音段」= 不同 testsrc + 单声道源经 amix→立体声（模拟克隆配音+BGM 混音）---
$segB = Join-Path $tmp 'seg_001.mp4'
Write-DubDiag "生成段B（配音/amix→立体声）…"
& $ffmpeg -y -f lavfi -i "testsrc2=size=${W}x${H}:rate=30:duration=${DUR}" `
          -f lavfi -i "sine=frequency=660:duration=${DUR}:sample_rate=44100" `
          -f lavfi -i "sine=frequency=220:duration=${DUR}:sample_rate=44100" `
          -filter_complex "[1:a][2:a]amix=inputs=2:normalize=0[aout]" `
          -map 0:v -map "[aout]" @encArgs @audArgs -movflags '+faststart' $segB 2>$null
Assert-True (Test-Path $segB) "段B 生成成功" "段B 生成失败"

# --- 阶段二a：concat 无损拼接 ------------------------------------------------
$listTxt = Join-Path $tmp 'list.txt'
"file '$segA'`nfile '$segB'" | Set-Content -Path $listTxt -Encoding ascii
$joined = Join-Path $tmp 'joined.mp4'
Write-DubDiag "concat -c copy 拼接 …"
& $ffmpeg -y -f concat -safe 0 -i $listTxt -c copy -movflags '+faststart' $joined 2>$null
Assert-True (Test-Path $joined) "concat 拼接成功" "concat 失败"

# --- 阶段二b：读 start_time → 平移归零 --------------------------------------
function Get-VideoStartTime([string]$path) {
    $o = & $ffprobe -v error -select_streams v:0 -show_entries stream=start_time -of "csv=p=0" $path
    $v = 0.0; [double]::TryParse(($o | Out-String).Trim(), [ref]$v) | Out-Null; return $v
}
$startBefore = Get-VideoStartTime $joined
Write-DubDiag ("拼接后 v:0 start_time = {0:N6}s（>0 即首帧黑隐患）" -f $startBefore)

$out = Join-Path $tmp 'final.mp4'
if ($startBefore -gt 0.001) {
    Write-DubDiag "执行 -output_ts_offset 平移归零 …"
    & $ffmpeg -y -i $joined -c copy -output_ts_offset ("{0:N6}" -f (-$startBefore)) -movflags '+faststart' $out 2>$null
} else {
    Copy-Item $joined $out -Force
}
$startAfter = Get-VideoStartTime $out
Assert-True ($startAfter -le 0.001) ("归零后 start_time = {0:N6}s ≤ 0.001（坑2 修复有效）" -f $startAfter) `
                                    ("归零后 start_time 仍 = {0:N6}s（坑2 未消除）" -f $startAfter)

# --- 双段都有声（坑1）-------------------------------------------------------
function Get-MeanVolumeDb([string]$path, [double]$ss, [double]$t) {
    $err = & $ffmpeg -hide_banner -ss $ss -t $t -i $path -af volumedetect -f null NUL 2>&1 | Out-String
    $m = [regex]::Match($err, 'mean_volume:\s*(-?\d+(\.\d+)?) dB')
    if ($m.Success) { return [double]$m.Groups[1].Value } else { return -999 }
}
$volA = Get-MeanVolumeDb $out 0.2 1.2
$volB = Get-MeanVolumeDb $out ($DUR + 0.2) 1.2
Write-DubDiag ("段A mean_volume={0}dB / 段B mean_volume={1}dB" -f $volA, $volB)
Assert-True (($volA -gt -80) -and ($volB -gt -80)) "两段都有声（无声道不一致静音 bug）" `
            "有段近静音（A=$volA dB B=$volB dB）—— 声道统一失效，复现坑1"

# --- 抽 t=0 帧（次要信号；硬指标是上面的 start_time）------------------------
$frame0 = Join-Path $tmp 'frame0.png'
& $ffmpeg -y -ss 0 -i $out -frames:v 1 $frame0 2>$null
if (Test-Path $frame0) { Write-DubDiag "已抽 t=0 帧：$frame0（请肉眼/资源管理器缩略图确认封面非黑）" }

Write-DubDiag "=== 烟测 ⑤ 通过 ✅ 导出管线核心修复有效（声道统一 + 首帧归零 + 双段有声）===" 'OK'
Write-Host "成片：$out" -ForegroundColor Cyan
Write-Host "提醒：平台『封面取 t=0』的真实验证请用 Windows 资源管理器缩略图 / 播放器，不要只信 ffmpeg 抽帧。" -ForegroundColor Yellow
