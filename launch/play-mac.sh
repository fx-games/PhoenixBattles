#!/bin/sh
# Starts the native Mac build with the PhoenixBattles mod injected through UnityDoorstop.
GAME="${PP_GAME:-/Users/Shared/Epic Games/PhoenixPoint/PhoenixPointOSX.app}"
HERE=$(cd "$(dirname "$0")" && pwd)
export DOORSTOP_ENABLED=1
export DOORSTOP_TARGET_ASSEMBLY="$HERE/PhoenixBattles.dll"
export DOORSTOP_IGNORE_DISABLED_ENV=0
export DYLD_LIBRARY_PATH="$HERE"
export DYLD_INSERT_LIBRARIES="$HERE/libdoorstop.dylib"
# PB_WINDOW=1: windowed 1280x720 (two instances side by side for testing); PB_LOG: Unity player log path
[ -n "$PB_WINDOW" ] && set -- -screen-fullscreen 0 -screen-width 1280 -screen-height 720 "$@"
[ -n "$PB_LOG" ] && set -- -logfile "$PB_LOG" "$@"
exec "$GAME/Contents/MacOS/Phoenix Point" "$@"
