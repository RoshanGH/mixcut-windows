# MixCut Windows 安装包一键构建脚本（v0.4.0+）
# 在 Windows 构建机执行：powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
#
# 流程：
# 1. 清空 publish/
# 2. dotnet publish self-contained
# 3. 验证 publish/bin 完整（11 个 native DLL + 3 个 EXE）
# 4. 用 Inno Setup 编安装包
# 5. 输出 installer/out/MixCut-Setup-vX.Y.Z-win-x64.exe

$ErrorActionPreference = 'Stop'

# 项目路径（脚本默认假设在仓库根目录跑）
$RepoRoot = Split-Path -Parent $PSScriptRoot
$ProjPath = Join-Path $RepoRoot 'src\MixCut\MixCut.csproj'
$PublishDir = Join-Path $RepoRoot 'publish'
$ResBinDir = Join-Path $RepoRoot 'src\MixCut\Resources\bin'
$InstallerIss = Join-Path $RepoRoot 'installer\MixCut.iss'
$OutDir = Join-Path $RepoRoot 'installer\out'

$Dotnet = 'C:\Users\mlamp\dotnet\dotnet.exe'
$Iscc = 'C:\Users\mlamp\AppData\Local\Programs\Inno Setup 6\iscc.exe'

# ---- Step 1: 清空 publish/ ----
if (Test-Path $PublishDir) {
    Write-Host "[1/5] 清空 publish/..."
    Remove-Item $PublishDir -Recurse -Force
}

# ---- Step 2: self-contained publish ----
Write-Host "[2/5] dotnet publish self-contained..."
& $Dotnet publish $ProjPath `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $PublishDir -nologo
if ($LASTEXITCODE -ne 0) { throw "publish 失败 ExitCode=$LASTEXITCODE" }

# ---- Step 3: 验证 publish/bin 完整 ----
Write-Host "[3/5] 验证 publish/bin..."
$RequiredBinFiles = @(
    'ffmpeg.exe', 'ffprobe.exe', 'whisper-cli.exe',
    'whisper.dll', 'ggml.dll', 'ggml-base.dll', 'ggml-cpu.dll',
    'vcruntime140.dll', 'vcruntime140_1.dll',
    'msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll',
    'concrt140.dll', 'vcomp140.dll'
)
$BinDir = Join-Path $PublishDir 'bin'
$Missing = @()
foreach ($f in $RequiredBinFiles) {
    $p = Join-Path $BinDir $f
    if (-not (Test-Path $p)) { $Missing += $f }
}
if ($Missing.Count -gt 0) {
    Write-Warning "publish/bin 缺以下文件，从 Resources/bin 补:"
    foreach ($f in $Missing) {
        $src = Join-Path $ResBinDir $f
        $dst = Join-Path $BinDir $f
        if (Test-Path $src) {
            Copy-Item $src -Destination $dst -Force
            Write-Host "  ✓ $f"
        } else {
            throw "Resources/bin/$f 也不存在！请先把 ffmpeg/whisper-cli + 所有 VC Runtime DLL 放进 Resources/bin"
        }
    }
}

# 再验一次
foreach ($f in $RequiredBinFiles) {
    $p = Join-Path $BinDir $f
    if (-not (Test-Path $p)) { throw "[FATAL] publish/bin/$f 仍缺失" }
}
Write-Host "  publish/bin 完整（14 个必备文件全在）"

# ---- Step 4: Inno Setup 编译 ----
Write-Host "[4/5] Inno Setup 编译..."
if (-not (Test-Path $Iscc)) { throw "iscc.exe 不存在：$Iscc" }
& $Iscc $InstallerIss
if ($LASTEXITCODE -ne 0) { throw "iscc 失败 ExitCode=$LASTEXITCODE" }

# ---- Step 5: 输出汇总 ----
Write-Host "[5/5] 完成"
$Out = Get-ChildItem $OutDir -Filter "MixCut-Setup-*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($Out) {
    $SizeMB = [math]::Round($Out.Length/1MB, 2)
    Write-Host ""
    Write-Host "================================="
    Write-Host "安装包: $($Out.FullName)"
    Write-Host "大小: ${SizeMB} MB"
    Write-Host "================================="
} else {
    throw "Inno Setup 出来没找到 EXE，看 $OutDir"
}
