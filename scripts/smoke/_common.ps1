# scripts/smoke/_common.ps1
# 配音 v0.5.0 P0 预研烟测的共享 helper。各 smoke-*.ps1 dot-source 本文件。
# 用法：. "$PSScriptRoot\_common.ps1"

# 注意：不设 'Stop'。PowerShell 5.1 在 Stop 下会把原生程序（ffmpeg）写到 stderr 的 banner
# 当成终止错误抛出（即便 2>$null）。本套脚本靠显式 Assert-True / $LASTEXITCODE / try-catch
# 控制流，无需全局 Stop。Web 调用用 try-catch 显式兜。
$ErrorActionPreference = 'Continue'

# --- 仓库根 & 内置 ffmpeg/ffprobe 定位 ---------------------------------------
function Get-RepoRoot {
    # scripts/smoke/ → 上两级
    return (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
}

function Resolve-Ffmpeg {
    param([string]$Name = 'ffmpeg')  # ffmpeg / ffprobe
    $root = Get-RepoRoot
    $candidates = @(
        (Join-Path $root "publish\bin\$Name.exe"),
        (Join-Path $root "src\MixCut\Resources\bin\$Name.exe"),
        (Join-Path $root "src\MixCut\bin\Release\net8.0-windows\bin\$Name.exe")
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    throw "找不到内置 $Name.exe，请先 publish（在以下任一位置）：`n  $($candidates -join "`n  ")"
}

# --- API Key（绝不硬编码；从环境变量或 -ApiKey 参数取）-----------------------
function Resolve-ApiKey {
    param([string]$ApiKey)
    if ($ApiKey) { return $ApiKey }
    if ($env:DASHSCOPE_API_KEY) { return $env:DASHSCOPE_API_KEY }
    throw "未提供阿里百炼(qwen) API Key。请先设置环境变量：`n  `$env:DASHSCOPE_API_KEY = 'sk-xxxx'`n或给脚本传 -ApiKey 'sk-xxxx'（与应用「设置」里的千问 Key 是同一把）。"
}

# --- 诊断日志（对齐 CLAUDE.md 自验证铁律，grep 关键字 [DubDiag]）------------
$script:DubDiagLog = Join-Path (Get-RepoRoot) 'scripts\smoke\dubdiag.log'
function Write-DubDiag {
    param([string]$Message, [string]$Level = 'INF')
    $line = "[{0}] [DubDiag] [{1}] {2}" -f (Get-Date -Format 'HH:mm:ss'), $Level, $Message
    Write-Host $line -ForegroundColor $(if ($Level -eq 'ERR') { 'Red' } elseif ($Level -eq 'OK') { 'Green' } else { 'Gray' })
    Add-Content -Path $script:DubDiagLog -Value $line -Encoding utf8
}

function Assert-True {
    param([bool]$Condition, [string]$PassMsg, [string]$FailMsg)
    if ($Condition) { Write-DubDiag $PassMsg 'OK' }
    else { Write-DubDiag $FailMsg 'ERR'; throw $FailMsg }
}

# --- 临时工作目录 ------------------------------------------------------------
function New-SmokeTempDir {
    param([string]$Tag = 'smoke')
    $d = Join-Path $env:TEMP ("mixcut-{0}-{1}" -f $Tag, ([guid]::NewGuid().ToString('N').Substring(0,8)))
    New-Item -ItemType Directory -Path $d -Force | Out-Null
    return $d
}
