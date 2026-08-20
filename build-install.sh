#!/usr/bin/env bash
set -euo pipefail

GAME_MANAGED_DIR="${STS2_MANAGED_DIR:-$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64}"
MODS_DIR="${STS2_MODS_DIR:-$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods}"

dotnet build Sts2LlmAgent.csproj -c Release -p:Sts2ManagedDir="$GAME_MANAGED_DIR"
mkdir -p "$MODS_DIR/Sts2LlmAgent"
cp "bin/Release/net9.0/Sts2LlmAgent.dll" "$MODS_DIR/Sts2LlmAgent/"
cp "bin/Release/net9.0/Sts2LlmAgent.Core.dll" "$MODS_DIR/Sts2LlmAgent/"
cp Sts2LlmAgent.json "$MODS_DIR/Sts2LlmAgent/"
printf 'Installed to %s\n' "$MODS_DIR/Sts2LlmAgent"
