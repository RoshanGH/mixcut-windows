#!/usr/bin/env bash
# 同步源码到 Windows 构建机并执行构建。
# 用法: scripts/build.sh [Debug|Release]
set -uo pipefail

CONFIG="${1:-Debug}"
SSH_KEY="$HOME/.ssh/mixcut_win"
REMOTE="mlamp@100.112.4.71"
LOCAL_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

SSH_BASE=(-i "$SSH_KEY" -o IdentitiesOnly=yes -o BatchMode=yes
          -o ConnectTimeout=20 -o ServerAliveInterval=8 -o ServerAliveCountMax=8)

# 1) 同步源码
bash "$LOCAL_DIR/scripts/sync.sh" || exit 1

# 2) 生成构建用 PowerShell 脚本（base64 编码，规避引号/编码问题）
PS_SCRIPT=$(cat <<EOF
\$ProgressPreference='SilentlyContinue'
[Console]::OutputEncoding=[Text.Encoding]::UTF8
\$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
Get-Process dotnet,MSBuild,VBCSCompiler -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
\$dotnet="\$env:USERPROFILE\\dotnet\\dotnet.exe"
& \$dotnet build "C:\\Users\\mlamp\\MixCutWindows\\MixCut.sln" -c $CONFIG --nologo -v minimal 2>&1
"EXITCODE=\$LASTEXITCODE"
EOF
)
B64=$(printf '%s' "$PS_SCRIPT" | iconv -t UTF-16LE | base64)

# 3) 同步构建（连接中断时重试）
for attempt in 1 2 3; do
  echo "=== 构建尝试 $attempt ($CONFIG) ==="
  R=$(ssh "${SSH_BASE[@]}" "$REMOTE" "powershell -NoProfile -EncodedCommand $B64" 2>&1)
  printf '%s\n' "$R"
  if grep -q "EXITCODE=0" <<< "$R"; then echo "构建成功 ✓"; exit 0; fi
  if grep -q "EXITCODE=" <<< "$R"; then echo "构建失败 ✗"; exit 1; fi
  echo "(连接中断，重试...)"; sleep 5
done
echo "多次重试后仍无法完成构建"; exit 1
