# scripts/smoke/smoke-clone-tts.ps1
# P0 烟测 ①+② —— 阿里百炼声音克隆链路契约验证（纯 HTTP）。
#   ① 音色注册   qwen-voice-enrollment   POST audio/tts/customization（参考音 base64 内联）
#   ② 克隆合成   qwen3-tts-vc-2026-01-22  POST aigc/multimodal-generation/generation（返回 OSS url）
#
# 这是 TRD 里「必须对真实 API 实跑验证」的核心：API 契约/模型名只能从 mac 源码提取，
# 没法离线复核，必须在国内网络 + 真实 qwen key 下 curl 验证后再大规模实现。
#
# 用法：
#   $env:DASHSCOPE_API_KEY = 'sk-xxxx'
#   powershell -File smoke-clone-tts.ps1 -RefAudio "C:\path\一条有人声的视频或音频.mp4"
# 可选：-ApiKey、-Text（要合成的测试台词）、-TargetModel（默认 qwen3-tts-vc-2026-01-22）

param(
    [Parameter(Mandatory)][string]$RefAudio,
    [string]$ApiKey,
    [string]$Text = "这款产品真的很好用，喜欢的话点击下方链接了解一下。",
    [string]$TargetModel = "qwen3-tts-vc-2026-01-22"
)

. "$PSScriptRoot\_common.ps1"

$key     = Resolve-ApiKey $ApiKey
$ffmpeg  = Resolve-Ffmpeg ffmpeg
$ffprobe = Resolve-Ffmpeg ffprobe
$tmp     = New-SmokeTempDir 'clone'
if (-not (Test-Path $RefAudio)) { throw "参考音频不存在：$RefAudio（需一段含人声的视频/音频）" }

Write-DubDiag "=== 烟测 ①+② 声音克隆链路 开始 ==="

# --- 准备 6s 短参考（坑4：长参考会让 TTS 续读参考内容）-----------------------
$refMp3 = Join-Path $tmp 'ref6.mp3'
Write-DubDiag "提取参考音频前 6 秒 → mp3 …"
& $ffmpeg -y -i $RefAudio -t 6 -ac 1 -ar 24000 -c:a libmp3lame -b:a 128k $refMp3 2>$null
Assert-True (Test-Path $refMp3) "参考 mp3 生成成功：$refMp3" "参考 mp3 生成失败"
$b64 = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($refMp3))
Write-DubDiag ("参考 mp3 base64 长度 = {0}" -f $b64.Length)

# --- ① 音色注册 -------------------------------------------------------------
$enrollUrl = 'https://dashscope.aliyuncs.com/api/v1/services/audio/tts/customization'
$prefName  = 'mixcut' + ([guid]::NewGuid().ToString('N').Substring(0,8))
$enrollBody = @{
    model = 'qwen-voice-enrollment'
    input = @{
        action         = 'create'
        target_model   = $TargetModel
        preferred_name = $prefName
        audio          = @{ data = "data:audio/mpeg;base64,$b64" }
    }
} | ConvertTo-Json -Depth 6 -Compress

Write-DubDiag "POST 音色注册（model=qwen-voice-enrollment, target=$TargetModel）…"
try {
    $enrollResp = Invoke-RestMethod -Method Post -Uri $enrollUrl `
        -Headers @{ Authorization = "Bearer $key"; 'Content-Type' = 'application/json; charset=utf-8' } `
        -Body ([System.Text.Encoding]::UTF8.GetBytes($enrollBody))
} catch {
    Write-DubDiag ("注册请求失败：{0}" -f $_.Exception.Message) 'ERR'
    if ($_.ErrorDetails.Message) { Write-DubDiag ("响应：{0}" -f $_.ErrorDetails.Message) 'ERR' }
    throw
}
$voiceId = $enrollResp.output.voice
Assert-True ([bool]$voiceId) "音色注册成功，voice_id = $voiceId" "注册未返回 output.voice（契约可能已变，检查响应：$($enrollResp | ConvertTo-Json -Depth 6 -Compress))"

# --- ② 克隆合成 -------------------------------------------------------------
$synthUrl = 'https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation'
$synthBody = @{
    model = $TargetModel
    input = @{ text = $Text; voice = $voiceId; language_type = 'Chinese' }
} | ConvertTo-Json -Depth 6 -Compress

Write-DubDiag "POST 克隆合成（model=$TargetModel, voice=$voiceId）…"
try {
    $synthResp = Invoke-RestMethod -Method Post -Uri $synthUrl `
        -Headers @{ Authorization = "Bearer $key"; 'Content-Type' = 'application/json; charset=utf-8' } `
        -Body ([System.Text.Encoding]::UTF8.GetBytes($synthBody))
} catch {
    Write-DubDiag ("合成请求失败：{0}" -f $_.Exception.Message) 'ERR'
    if ($_.ErrorDetails.Message) { Write-DubDiag ("响应：{0}" -f $_.ErrorDetails.Message) 'ERR' }
    throw
}
$audioUrl = $synthResp.output.audio.url
Assert-True ([bool]$audioUrl) "合成返回音频 URL：$audioUrl" "合成未返回 output.audio.url（检查响应：$($synthResp | ConvertTo-Json -Depth 6 -Compress))"

# http → https（OSS 预签名 V1 不含 scheme，升级不影响签名）
if ($audioUrl -like 'http://*') { $audioUrl = 'https://' + $audioUrl.Substring(7) }

# --- 下载 + 测时长 ----------------------------------------------------------
$wav = Join-Path $tmp 'clone-out.wav'
Write-DubDiag "下载合成音频 …"
Invoke-WebRequest -Uri $audioUrl -OutFile $wav | Out-Null
$dur = [double](& $ffprobe -v error -show_entries format=duration -of "default=noprint_wrappers=1:nokey=1" $wav)
Assert-True ($dur -gt 0) ("合成音频时长 = {0:N2}s（文件：{1}）" -f $dur, $wav) "合成音频时长为 0 / 不可解析"

Write-DubDiag "=== 烟测 ①+② 通过 ✅ 克隆契约有效（注册→合成→可播）===" 'OK'
Write-Host ""
Write-Host "请人工试听确认音色像原声：$wav" -ForegroundColor Cyan
