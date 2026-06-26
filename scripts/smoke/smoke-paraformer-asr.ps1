# scripts/smoke/smoke-paraformer-asr.ps1
# P0 烟测 ③ —— 阿里百炼 paraformer-realtime-v2 流式 ASR 契约验证（WebSocket）。
# 协议（逐字段对齐 mac Sources/MixCutCore/ParaformerASR.swift + QwenASRClient.swift）：
#   wss://dashscope.aliyuncs.com/api-ws/v1/inference/
#   headers: Authorization: bearer <key> ; X-DashScope-DataInspection: enable
#   1) 发 run-task(text) → 等 header.event == task-started
#   2) 流式发 PCM 二进制帧（每帧 3200 字节，16k/mono/s16le）
#   3) 发 finish-task(text) → 等 header.event == task-finished
#   过程中 result-generated 的 payload.output.sentence.{text,sentence_end} 累积，取 sentence_end 拼接
#
# 用法：
#   $env:DASHSCOPE_API_KEY = 'sk-xxxx'
#   powershell -File smoke-paraformer-asr.ps1 -Audio "C:\一段中文语音.mp4"

param(
    [Parameter(Mandatory)][string]$Audio,
    [string]$ApiKey
)

. "$PSScriptRoot\_common.ps1"

$key    = Resolve-ApiKey $ApiKey
$ffmpeg = Resolve-Ffmpeg ffmpeg
$tmp    = New-SmokeTempDir 'asr'
if (-not (Test-Path $Audio)) { throw "音频不存在：$Audio" }

Write-DubDiag "=== 烟测 ③ paraformer 流式 ASR 开始 ==="

# --- 抽 16k/mono/s16le PCM ---------------------------------------------------
$pcm = Join-Path $tmp 'audio16k.pcm'
& $ffmpeg -y -i $Audio -f s16le -acodec pcm_s16le -ac 1 -ar 16000 $pcm 2>$null
Assert-True (Test-Path $pcm) "PCM 提取成功（16k/mono/s16le）：$pcm" "PCM 提取失败"
$bytes = [System.IO.File]::ReadAllBytes($pcm)
Write-DubDiag ("PCM 字节数 = {0}（≈{1:N1}s）" -f $bytes.Length, ($bytes.Length / 32000.0))

# --- WebSocket 连接 ----------------------------------------------------------
# 注：System.Net.WebSockets.ClientWebSocket 在 PS 5.1 已随基础程序集加载，无需 Add-Type。
$ws  = [System.Net.WebSockets.ClientWebSocket]::new()
$ws.Options.SetRequestHeader('Authorization', "bearer $key")
$ws.Options.SetRequestHeader('X-DashScope-DataInspection', 'enable')
$uri = [Uri]::new('wss://dashscope.aliyuncs.com/api-ws/v1/inference/')
$cts = [System.Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(60))
$ct  = $cts.Token

function Send-Text([string]$json) {
    $buf = [System.Text.Encoding]::UTF8.GetBytes($json)
    $seg = [System.ArraySegment[byte]]::new($buf)
    $ws.SendAsync($seg, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $ct).GetAwaiter().GetResult() | Out-Null
}
function Send-Binary([byte[]]$data) {
    $seg = [System.ArraySegment[byte]]::new($data)
    $ws.SendAsync($seg, [System.Net.WebSockets.WebSocketMessageType]::Binary, $true, $ct).GetAwaiter().GetResult() | Out-Null
}
# 读一条完整 text 消息（拼分片）；返回 $null 表示非 text/已关闭
function Receive-Text {
    $sb = [System.Text.StringBuilder]::new()
    $buffer = [byte[]]::new(8192)
    do {
        $seg = [System.ArraySegment[byte]]::new($buffer)
        $res = $ws.ReceiveAsync($seg, $ct).GetAwaiter().GetResult()
        if ($res.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) { return $null }
        [void]$sb.Append([System.Text.Encoding]::UTF8.GetString($buffer, 0, $res.Count))
    } while (-not $res.EndOfMessage)
    return $sb.ToString()
}
# 持续读到 header.event == $target；把 result-generated 的 sentence_end 句累积
function Wait-Event([string]$target, [System.Collections.Generic.List[string]]$finals) {
    while ($true) {
        $msg = Receive-Text
        if ($null -eq $msg) { throw "连接被关闭，未等到 event=$target" }
        $obj = $msg | ConvertFrom-Json
        $ev = $obj.header.event
        if ($ev -eq 'result-generated') {
            $s = $obj.payload.output.sentence
            if ($s -and $s.sentence_end -eq $true -and $s.text) { $finals.Add([string]$s.text) }
        }
        if ($ev -eq 'task-failed') { throw "task-failed：$msg" }
        if ($ev -eq $target) { return }
    }
}

try {
    Write-DubDiag "连接 WebSocket …"
    $ws.ConnectAsync($uri, $ct).GetAwaiter().GetResult() | Out-Null
    Assert-True ($ws.State -eq 'Open') "WebSocket 已连接" "WebSocket 连接失败（state=$($ws.State)）"

    $taskId = [guid]::NewGuid().ToString()
    $finals = [System.Collections.Generic.List[string]]::new()

    $runTask = '{"header":{"action":"run-task","task_id":"' + $taskId + '","streaming":"duplex"},' +
               '"payload":{"task_group":"audio","task":"asr","function":"recognition",' +
               '"model":"paraformer-realtime-v2",' +
               '"parameters":{"format":"pcm","sample_rate":16000},"input":{}}}'
    Send-Text $runTask
    Wait-Event 'task-started' $finals
    Write-DubDiag "收到 task-started，开始流式推送 PCM …"

    $chunk = 3200; $offset = 0
    while ($offset -lt $bytes.Length) {
        $len = [Math]::Min($chunk, $bytes.Length - $offset)
        $frame = New-Object byte[] $len
        [Array]::Copy($bytes, $offset, $frame, 0, $len)
        Send-Binary $frame
        $offset += $len
    }
    Write-DubDiag ("PCM 推送完毕（{0} 帧），发 finish-task …" -f [Math]::Ceiling($bytes.Length / $chunk))

    $finishTask = '{"header":{"action":"finish-task","task_id":"' + $taskId + '","streaming":"duplex"},"payload":{"input":{}}}'
    Send-Text $finishTask
    Wait-Event 'task-finished' $finals

    $transcript = ($finals -join '').Trim()
    Assert-True ($transcript.Length -gt 0) "识别结果：「$transcript」" "识别完成但文本为空（音频是否有清晰人声？）"
    Write-DubDiag "=== 烟测 ③ 通过 ✅ paraformer WS 契约有效 ===" 'OK'
}
finally {
    try { $ws.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, '', [System.Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null } catch {}
    $ws.Dispose()
}
