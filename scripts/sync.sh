#!/usr/bin/env bash
# 把 Mac 上的源码同步到 Windows 构建机（通过 Tailscale SSH）。
# 用法: scripts/sync.sh
set -euo pipefail

SSH_KEY="$HOME/.ssh/mixcut_win"
REMOTE="mlamp@100.112.4.71"
WIN_DIR="C:/Users/mlamp/MixCutWindows"
LOCAL_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

SSH_OPTS=(-i "$SSH_KEY" -o IdentitiesOnly=yes -o BatchMode=yes)

# 确保远程目录存在
ssh "${SSH_OPTS[@]}" "$REMOTE" \
  "powershell -NoProfile -Command \"New-Item -ItemType Directory -Force '$WIN_DIR' | Out-Null\""

# 同步源码与解决方案文件（Mac 端不含 bin/obj，源码干净）
scp "${SSH_OPTS[@]}" -q -r "$LOCAL_DIR/src" "$LOCAL_DIR/MixCut.sln" "$REMOTE:$WIN_DIR/"

echo "同步完成 -> $WIN_DIR"
