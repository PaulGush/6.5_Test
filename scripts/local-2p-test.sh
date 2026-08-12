#!/usr/bin/env bash
# Local multi-instance playtest: see the joined-client experience without friends.
# Uses the Linux build over KCP loopback, windowed, with per-instance logs.
#
#   scripts/local-2p-test.sh            # build host + 1 build client
#   scripts/local-2p-test.sh --join     # client(s) only (host in the Unity editor)
#   scripts/local-2p-test.sh -n 2       # ... with 2 clients
#
# Logs land in Builds/logs/{host,client1,...}.log — the client log's
# "[AutoStart] connected=..." lines are the quick replication sanity check.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BIN="$ROOT/Builds/linux64/PiecesOfFreight/PiecesOfFreight.x86_64"
LOGDIR="$ROOT/Builds/logs"

if [ ! -x "$BIN" ]; then
    echo "No Linux build at $BIN"
    echo "Run Tools > Ship > Build Test Builds (Win + Linux) in Unity first."
    exit 1
fi

JOIN_ONLY=0
CLIENTS=1
while [ $# -gt 0 ]; do
    case "$1" in
        --join) JOIN_ONLY=1 ;;
        -n) CLIENTS="$2"; shift ;;
        *) echo "unknown arg: $1"; exit 1 ;;
    esac
    shift
done

mkdir -p "$LOGDIR"
COMMON=(-kcp -screen-fullscreen 0 -screen-width 1152 -screen-height 648)

if [ "$JOIN_ONLY" = 0 ]; then
    echo "starting host..."
    "$BIN" "${COMMON[@]}" -host -logFile "$LOGDIR/host.log" &
    sleep 4 # let the host bind before clients dial in
fi

for i in $(seq 1 "$CLIENTS"); do
    echo "starting client $i..."
    "$BIN" "${COMMON[@]}" -client 127.0.0.1 -logFile "$LOGDIR/client$i.log" &
    sleep 1
done

echo "running — close the windows (or Ctrl+C here) to stop."
wait
