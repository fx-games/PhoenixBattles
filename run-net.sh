#!/bin/sh
# run-net.sh [timeout s] [log pattern]: starts the relay and two windowed game instances that auto-connect to room
# "test", auto-FIGHT and auto-end their turns. Instance 2 runs from a second mod copy so the logs stay apart.
ROOT=$(cd "$(dirname "$0")" && pwd)
D1="/Users/Shared/Epic Games/PhoenixPoint/PhoenixBattles"; D2="/Users/Shared/Epic Games/PhoenixPoint/PhoenixBattles2"
pkill -f "Phoenix Point" 2>/dev/null; pkill -x server 2>/dev/null; sleep 1
PP_DEST="$D2" "$ROOT/install.sh" >/dev/null
rm -f "$D1/PhoenixBattles.log" "$D2/PhoenixBattles.log" /tmp/pb1.log /tmp/pb2.log
(cd "$ROOT/server" && go build -o server . && ./server >/tmp/pb-relay.log 2>&1 &)
sleep 1
PB_KILL=$PB_KILL PB_AUTO=net PB_WINDOW=1 PB_LOG=/tmp/pb1.log "$D1/play-mac.sh" >/dev/null 2>&1 &
sleep 20
PB_AUTO=net PB_WINDOW=1 PB_LOG=/tmp/pb2.log "$D2/play-mac.sh" >/dev/null 2>&1 &
T=${1:-300}; t=0
while [ $t -lt $T ]; do sleep 5; t=$((t+5)); grep -q "$2" "$D1/PhoenixBattles.log" "$D2/PhoenixBattles.log" 2>/dev/null && break; done
echo "--- relay"; cat /tmp/pb-relay.log
echo "--- instance 1 ($t s)"; cat "$D1/PhoenixBattles.log"; echo "--- instance 2"; cat "$D2/PhoenixBattles.log"
echo "--- exceptions: 1=$(grep -c Exception /tmp/pb1.log) 2=$(grep -c Exception /tmp/pb2.log)"; grep -h -m3 -A4 'Exception' /tmp/pb1.log /tmp/pb2.log | head -40
