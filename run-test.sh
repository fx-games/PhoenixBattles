#!/bin/sh
# run-test.sh <PB_AUTO value> [timeout s]: launches the game with the auto action, waits for the mod log to settle, prints it.
D="${PP_DEST:-/Users/Shared/Epic Games/PhoenixPoint/PhoenixBattles}"
P="$HOME/Library/Logs/Snapshot Games Inc/Phoenix Point/Player.log"
pkill -f "Phoenix Point" 2>/dev/null; sleep 1
rm -f "$D/PhoenixBattles.log" "$P"
PB_AUTO="$1" "$D/play-mac.sh" >/dev/null 2>&1 &
T=${2:-240}; t=0
while [ $t -lt $T ]; do sleep 3; t=$((t+3)); grep -q "$3" "$D/PhoenixBattles.log" 2>/dev/null && break; done
sleep 2; echo "--- mod log ($t s)"; cat "$D/PhoenixBattles.log"
echo "--- player log exceptions: $(grep -c 'Exception' "$P")"; grep -n -m5 -A3 'Exception' "$P"
