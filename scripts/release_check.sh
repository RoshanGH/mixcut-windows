#!/usr/bin/env bash
# 发版前自动化兜底检查脚本（v0.6.0+）
#
# 用法：
#   scripts/release_check.sh
#
# 作用：
#   1. sync + 远端 publish + 检查 publish/ 体积合理
#   2. 验证 6 个 VC Runtime DLL 全部存在
#   3. 验证 LibVLC win-x64 完整（libvlc.dll + libvlccore.dll + plugins ≥ 300）
#   4. 验证 win-x86 已被 StripLibVlcX86 target 删除（不浪费体积）
#   5. 启动 app 跑 15 秒，grep 启动诊断日志：
#      [VcRuntimeDiag] all 6 VC Runtime DLLs present
#      [VlcDiag] libvlc=... plugins=365 (lazy-init on first hover)
#      [VlcRuntimeDiag] libvlc deps OK
#      [EnvDiag] pass=True
#   6. 任一项失败 → 退出码 1，发版必须中止；全过 → RELEASE_READY=true
#
# 设计原则（对齐 CLAUDE.md §目标平台铁律）：
#   - 装上即跑：用户机器不依赖额外安装
#   - 干净环境兜底：构建机自带 VC++ Redist，本脚本静态分析依赖避免漏网
#   - 错误码可追踪：VLC-01/02/03/04 → 用户报告时一眼定位

set -e

SSH_KEY="$HOME/.ssh/mixcut_win"
SSH_HOST="mlamp@100.112.4.71"
REMOTE_PUBLISH="C:\\Users\\mlamp\\MixCutWindows\\publish"
REMOTE_LOG_DIR='$env:APPDATA\\MixCut\\logs'

echo "==> Step 1: sync + remote publish"
scripts/sync.sh | tail -1

cat > /tmp/release_check_publish.ps1 << 'PS_EOF'
Get-Process MixCut -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 2
if (Test-Path C:\Users\mlamp\MixCutWindows\publish) {
    Remove-Item C:\Users\mlamp\MixCutWindows\publish -Recurse -Force
}
& C:\Users\mlamp\dotnet\dotnet.exe publish C:\Users\mlamp\MixCutWindows\src\MixCut\MixCut.csproj `
    -c Release -p:SelfContained=true `
    -o C:\Users\mlamp\MixCutWindows\publish 2>&1 | Select-Object -Last 3
PS_EOF
B64=$(iconv -t UTF-16LE /tmp/release_check_publish.ps1 | base64)
ssh -i "$SSH_KEY" -o IdentitiesOnly=yes "$SSH_HOST" "powershell -NoProfile -EncodedCommand $B64" 2>&1 \
    | grep -v "CLIXML\|Objs\|RefId\|SourceId" | tail -5

echo ""
echo "==> Step 2: verify deployment completeness"
cat > /tmp/release_check_files.ps1 << 'PS_EOF'
$ok = $true
$pub = "C:\Users\mlamp\MixCutWindows\publish"

# 1. publish 体积（300-600 MB 合理范围）
$total = (Get-ChildItem $pub -Recurse | Measure-Object Length -Sum).Sum
$totalMB = [math]::Round($total/1MB, 1)
Write-Output "publish total: $totalMB MB"
if ($totalMB -lt 300 -or $totalMB -gt 700) {
    Write-Output "  FAIL: publish size out of range (300-700 MB)"
    $ok = $false
}

# 2. 6 个 VC Runtime DLL
$vcDlls = @("vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll",
            "msvcp140_1.dll", "msvcp140_2.dll", "concrt140.dll")
$vcOk = 0
foreach ($d in $vcDlls) {
    if (Test-Path "$pub\bin\$d") { $vcOk++ }
}
Write-Output "VC Runtime DLLs in publish/bin/: $vcOk/6"
if ($vcOk -lt 6) {
    Write-Output "  FAIL: VC Runtime incomplete"
    $ok = $false
}

# 3. vcomp140 + concrt140（v0.4.0 沉淀：whisper ggml-cpu 依赖）
$vcomp = Test-Path "$pub\bin\vcomp140.dll"
Write-Output "vcomp140.dll: $vcomp"
if (-not $vcomp) {
    Write-Output "  WARN: vcomp140.dll missing (whisper may crash on clean Win)"
}

# 4. LibVLC win-x64
$libvlc = Test-Path "$pub\libvlc\win-x64\libvlc.dll"
$libvlccore = Test-Path "$pub\libvlc\win-x64\libvlccore.dll"
$pluginsDir = "$pub\libvlc\win-x64\plugins"
$pluginCount = 0
if (Test-Path $pluginsDir) {
    $pluginCount = (Get-ChildItem $pluginsDir -Recurse -Filter *.dll).Count
}
Write-Output "libvlc.dll: $libvlc, libvlccore.dll: $libvlccore, plugins: $pluginCount"
if (-not $libvlc -or -not $libvlccore -or $pluginCount -lt 300) {
    Write-Output "  FAIL: LibVLC deployment incomplete"
    $ok = $false
}

# 5. win-x86 必须被 StripLibVlcX86 删掉（节省 130MB）
$x86 = Test-Path "$pub\libvlc\win-x86"
Write-Output "libvlc/win-x86 exists: $x86 (expect False)"
if ($x86) {
    Write-Output "  FAIL: win-x86 should be stripped by StripLibVlcX86 target"
    $ok = $false
}

# 6. FFmpeg / whisper-cli
$ffmpeg = Test-Path "$pub\bin\ffmpeg.exe"
$whisper = Test-Path "$pub\bin\whisper-cli.exe"
Write-Output "ffmpeg.exe: $ffmpeg, whisper-cli.exe: $whisper"
if (-not $ffmpeg -or -not $whisper) {
    Write-Output "  FAIL: FFmpeg / whisper missing"
    $ok = $false
}

# 7. MixCut.exe + 关键 .dll
$exe = Test-Path "$pub\MixCut.exe"
$libvlcsharp = Test-Path "$pub\LibVLCSharp.dll"
$libvlcsharpWpf = Test-Path "$pub\LibVLCSharp.WPF.dll"
Write-Output "MixCut.exe: $exe, LibVLCSharp.dll: $libvlcsharp, LibVLCSharp.WPF.dll: $libvlcsharpWpf"
if (-not $exe -or -not $libvlcsharp -or -not $libvlcsharpWpf) {
    Write-Output "  FAIL: critical .NET dll missing"
    $ok = $false
}

if ($ok) { Write-Output "FILES_OK=true" } else { Write-Output "FILES_OK=false" }
PS_EOF
B64=$(iconv -t UTF-16LE /tmp/release_check_files.ps1 | base64)
FILES_OUT=$(ssh -i "$SSH_KEY" -o IdentitiesOnly=yes "$SSH_HOST" "powershell -NoProfile -EncodedCommand $B64" 2>&1 | grep -v "CLIXML\|Objs\|RefId\|SourceId")
echo "$FILES_OUT"
if ! echo "$FILES_OUT" | grep -q "FILES_OK=true"; then
    echo "❌ FAIL: deployment completeness check failed"
    exit 1
fi

echo ""
echo "==> Step 3: launch app + verify startup diagnostics"
cat > /tmp/release_check_launch.ps1 << 'PS_EOF'
Get-Process MixCut -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 2
Start-Process C:\Users\mlamp\MixCutWindows\publish\MixCut.exe
Start-Sleep 15
$p = Get-Process MixCut -ErrorAction SilentlyContinue
if (-not $p) {
    Write-Output "FAIL: app died within 15s"
    exit 1
}
Write-Output "ALIVE pid=$($p.Id) mem=$([math]::Round($p.WorkingSet64/1MB,1))MB"

$log = Get-ChildItem $env:APPDATA\MixCut\logs\ -Filter mixcut-*.log | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$content = Get-Content $log.FullName -Tail 50

# 必须全部命中的诊断行
$required = @(
    "VlcDiag.*libvlc.*plugins=",
    "VlcRuntimeDiag.*libvlc deps OK",
    "VcRuntimeDiag.*all 6 VC Runtime DLLs present",
    "EnvDiag.*pass=True"
)
$missing = @()
foreach ($pat in $required) {
    if (-not ($content -match $pat)) {
        $missing += $pat
    }
}
if ($missing.Count -eq 0) {
    Write-Output "DIAG_OK=true"
} else {
    Write-Output "DIAG_OK=false missing=$($missing -join ',')"
}

# 任何 ERR/FTL 都不允许
$errs = $content | Select-String -Pattern "\[ERR\]|\[FTL\]|Exception|Unhandled" | Select-Object -First 3
if ($errs) {
    Write-Output "ERR_DETECTED=true"
    $errs | ForEach-Object { Write-Output "  $_" }
} else {
    Write-Output "ERR_DETECTED=false"
}
PS_EOF
B64=$(iconv -t UTF-16LE /tmp/release_check_launch.ps1 | base64)
LAUNCH_OUT=$(ssh -i "$SSH_KEY" -o IdentitiesOnly=yes "$SSH_HOST" "powershell -NoProfile -EncodedCommand $B64" 2>&1 | grep -v "CLIXML\|Objs\|RefId\|SourceId")
echo "$LAUNCH_OUT"

if ! echo "$LAUNCH_OUT" | grep -q "DIAG_OK=true"; then
    echo "❌ FAIL: startup diagnostics incomplete"
    exit 1
fi
if echo "$LAUNCH_OUT" | grep -q "ERR_DETECTED=true"; then
    echo "❌ FAIL: ERR/FTL/Exception detected in startup log"
    exit 1
fi

echo ""
echo "✅ RELEASE_READY=true"
echo ""
echo "下一步可手动跑："
echo "  1. 在物理机 / RDP 进构建机 → 启动 publish/MixCut.exe → hover 分镜测试播放不闪"
echo "  2. 跑 Inno Setup：iscc installer/MixCut.iss → 生成 setup.exe + .bin 分卷"
echo "  3. (条件允许) 在干净 Win 10/11 VM 装一遍 + 跑完整 e2e"
