# scripts/smoke/Get-PeImports.ps1
# PE 文件 import 依赖扫描（对齐 CLAUDE.md §D「PE 文件 import 表静态分析」）。
# 用法：powershell -File Get-PeImports.ps1 -Path <demucs.exe 或任意 native dll/exe>
#   可选 -CompareDir <publish\bin>：列出该目录没覆盖、且非已知系统 DLL 的依赖（= 需要补打包的）
#
# 注：用「扫全文件抓 *.dll 字符串」的实用法（CLAUDE.md 原话：正则 [\w\-\.]+\.dll 抓所有 import），
# 会有少量误报，但能可靠发现潜伏的 native DLL 依赖，比「启动跑通」更早暴露问题。

param(
    [Parameter(Mandatory)][string]$Path,
    [string]$CompareDir
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Path)) { throw "文件不存在：$Path" }

$bytes = [System.IO.File]::ReadAllBytes($Path)
$text  = [System.Text.Encoding]::ASCII.GetString($bytes)
$dlls = [regex]::Matches($text, '(?i)[\w\-\.]+\.dll') |
    ForEach-Object { $_.Value.ToLower() } |
    Sort-Object -Unique

Write-Host "=== $Path 引用的 DLL（$($dlls.Count) 个，含少量误报）===" -ForegroundColor Cyan
$dlls | ForEach-Object { Write-Host "  $_" }

# 已知随 Windows 10/11 默认存在的系统 DLL（无需打包）
$systemDlls = @(
    'kernel32.dll','user32.dll','gdi32.dll','advapi32.dll','shell32.dll','ole32.dll',
    'oleaut32.dll','ws2_32.dll','crypt32.dll','bcrypt.dll','ntdll.dll','rpcrt4.dll',
    'shlwapi.dll','user32.dll','winmm.dll','dbghelp.dll','version.dll','psapi.dll',
    'd3d11.dll','dxgi.dll','d3d9.dll','setupapi.dll','cfgmgr32.dll','powrprof.dll',
    'api-ms-win-*','ext-ms-*','kernelbase.dll','msvcrt.dll','combase.dll','sechost.dll'
) | ForEach-Object { $_.ToLower() }

function Is-SystemDll($name) {
    foreach ($s in $systemDlls) {
        if ($s.EndsWith('*')) { if ($name.StartsWith($s.TrimEnd('*'))) { return $true } }
        elseif ($name -eq $s) { return $true }
    }
    return $false
}

if ($CompareDir) {
    if (-not (Test-Path $CompareDir)) { throw "对比目录不存在：$CompareDir" }
    $present = Get-ChildItem -Path $CompareDir -Filter *.dll | ForEach-Object { $_.Name.ToLower() }
    $missing = $dlls | Where-Object { -not (Is-SystemDll $_) -and ($present -notcontains $_) }
    Write-Host ""
    if ($missing.Count -eq 0) {
        Write-Host "[DubDiag] [OK] 所有非系统 DLL 依赖都已在 $CompareDir（无需补打包）" -ForegroundColor Green
    } else {
        Write-Host "[DubDiag] [ERR] 以下依赖既不是系统 DLL、也不在 $CompareDir —— 需补打包或确认是误报：" -ForegroundColor Red
        $missing | ForEach-Object { Write-Host "  ⚠ $_" -ForegroundColor Yellow }
    }
}
