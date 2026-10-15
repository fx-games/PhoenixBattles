#!/bin/sh
# Builds the mod and installs it next to the game: <game dir>/PhoenixBattles/{PhoenixBattles.dll,0Harmony.dll,libdoorstop.dylib,play-mac.sh}
set -e
ROOT=$(cd "$(dirname "$0")" && pwd)
DEST="${PP_DEST:-/Users/Shared/Epic Games/PhoenixPoint/PhoenixBattles}"
export DOTNET_ROOT=$HOME/.dotnet
"$HOME/.dotnet/dotnet" build "$ROOT/mod" -c Release -v q -nologo
mkdir -p "$DEST"
cp "$ROOT/mod/bin/PhoenixBattles.dll" "$ROOT/mod/bin/PhoenixBattles.pdb" "$ROOT/mod/bin/0Harmony.dll" "$ROOT/launch/libdoorstop.dylib" "$ROOT/launch/play-mac.sh" "$DEST/"
echo "installed to $DEST"
