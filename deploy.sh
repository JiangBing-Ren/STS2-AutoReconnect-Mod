#!/usr/bin/env bash
# deploy.sh — AutoReconnectMin 部署脚本（含版本归档）
#
# 硬性约定：
#   1) 每次改动都必须先升级 mod_manifest.json 的 version（唯一事实来源）。
#   2) 旧版本不覆盖：每个版本归档到 deploy/AutoReconnectMin/archive/<version>/，
#      若目标版本归档已存在则报错退出，强制你先升版本。
#
# 用法：bash deploy.sh
set -euo pipefail

PROJ="$PWD"
GAME="/d/Steam/steamapps/common/Slay the Spire 2/mods/AutoReconnectMin"
DOTNET="/c/Program Files/dotnet/dotnet"
STS2_GAME_DIR="D:/Steam/steamapps/common/Slay the Spire 2"
DEP="$PROJ/deploy/AutoReconnectMin"
ARCH_BASE="$DEP/archive"

cd "$PROJ"

# 1) 构建
echo "==> 构建 Release ..."
STS2_GAME_DIR="$STS2_GAME_DIR" "$DOTNET" build -c Release 2>&1 | tail -20

# 2) 读版本号（单一事实来源 = mod_manifest.json）
VERSION=$(grep -m1 '"version"' mod_manifest.json | sed -E 's/.*"version"\s*:\s*"([^"]+)".*/\1/')
if [ -z "$VERSION" ]; then
  echo "错误：无法从 mod_manifest.json 读取 version。" >&2
  exit 1
fi
echo "==> 部署版本: $VERSION"

ARCH="$ARCH_BASE/$VERSION"

# 3) 旧版本不覆盖：归档目录已存在则拒绝
if [ -d "$ARCH" ]; then
  echo "错误：版本 $VERSION 的归档已存在（$ARCH）。" >&2
  echo "      请先升级 mod_manifest.json 的 version 再部署（硬性要求：保留每一个版本）。" >&2
  exit 1
fi

# 4) 部署到游戏加载目录（latest，覆盖是预期的）
mkdir -p "$GAME"
cp -f "$PROJ/AutoReconnectMin.dll" "$GAME/AutoReconnectMin.dll"
cp -f "$PROJ/mod_manifest.json" "$GAME/AutoReconnectMin.json"

# 5) 部署到分发目录（latest）
cp -f "$PROJ/AutoReconnectMin.dll" "$DEP/AutoReconnectMin.dll"
cp -f "$PROJ/mod_manifest.json" "$DEP/AutoReconnectMin.json"

# 6) 归档该版本（永不覆盖）
mkdir -p "$ARCH"
cp -f "$PROJ/AutoReconnectMin.dll" "$ARCH/AutoReconnectMin.dll"
cp -f "$PROJ/mod_manifest.json" "$ARCH/AutoReconnectMin.json"

echo "==> 完成：游戏目录 + 分发目录(latest) + 归档 $ARCH"
