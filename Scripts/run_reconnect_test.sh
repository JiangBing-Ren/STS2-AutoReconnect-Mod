#!/usr/bin/env bash
# Cross-platform reconnect test helper (bash)
# Usage: ./run_reconnect_test.sh role host|client logpath adaptername disable_seconds prewait outdir
ROLE=${1:-client}
LOGFILE=${2:-"/path/to/game.log"}
ADAPTER=${3:-""}
DISABLE_SECONDS=${4:-10}
PREWAIT=${5:-5}
OUTDIR=${6:-"reconnect_test_$(date +%Y%m%d_%H%M%S)"}

mkdir -p "$OUTDIR"
LOGOUT="$OUTDIR/${ROLE}_game.log"
ACTIONS="$OUTDIR/${ROLE}_actions.log"
META="$OUTDIR/${ROLE}_meta.txt"

echo "Role: $ROLE" > "$META"
echo "Started: $(date -Iseconds)" >> "$META"
echo "LogFile: $LOGFILE" >> "$META"
echo "Adapter: $ADAPTER" >> "$META"
echo "DisableSeconds: $DISABLE_SECONDS" >> "$META"

echo "Starting log capture to $LOGOUT"
# Use tail -F for robust following; write to background
if [ -f "$LOGFILE" ]; then
  tail -F "$LOGFILE" >> "$LOGOUT" 2>/dev/null &
  TAIL_PID=$!
else
  echo "[run_reconnect_test] Log file not found: $LOGFILE" > "$LOGOUT"
  TAIL_PID=""
fi

stamp() { echo "$(date -Iseconds)\t$*" | tee -a "$ACTIONS"; }

stamp "PreWait: waiting $PREWAIT seconds"
sleep $PREWAIT

if [ -n "$ADAPTER" ]; then
  stamp "Disabling adapter: $ADAPTER"
  unameOut=$(uname -s)
  case "$unameOut" in
    Linux*)
      if command -v nmcli >/dev/null 2>&1; then
        sudo nmcli device disconnect "$ADAPTER" || sudo ip link set "$ADAPTER" down
      else
        sudo ip link set "$ADAPTER" down
      fi
      ;;
    Darwin*)
      # macOS: if wireless, use networksetup; else try ifconfig
      if command -v networksetup >/dev/null 2>&1; then
        sudo networksetup -setnetworkserviceenabled "$ADAPTER" off || sudo ifconfig "$ADAPTER" down
      else
        sudo ifconfig "$ADAPTER" down
      fi
      ;;
    *)
      stamp "Unknown OS for adapter control: $unameOut"
      ;;
  esac
  stamp "Adapter disabled"
  stamp "Sleeping for $DISABLE_SECONDS seconds"
  sleep $DISABLE_SECONDS
  stamp "Enabling adapter: $ADAPTER"
  case "$unameOut" in
    Linux*)
      if command -v nmcli >/dev/null 2>&1; then
        sudo nmcli device connect "$ADAPTER" || sudo ip link set "$ADAPTER" up
      else
        sudo ip link set "$ADAPTER" up
      fi
      ;;
    Darwin*)
      if command -v networksetup >/dev/null 2>&1; then
        sudo networksetup -setnetworkserviceenabled "$ADAPTER" on || sudo ifconfig "$ADAPTER" up
      else
        sudo ifconfig "$ADAPTER" up
      fi
      ;;
    *)
      stamp "Unknown OS for adapter control: $unameOut"
      ;;
  esac
  stamp "Adapter enabled"
else
  stamp "No adapter provided — skipping network toggle"
fi

stamp "Post-wait: sleeping 8s to allow logs to flush"
sleep 8

if [ -n "$TAIL_PID" ]; then
  stamp "Stopping tail (pid $TAIL_PID)"
  kill "$TAIL_PID" 2>/dev/null || true
fi

stamp "Test finished. Packaging results"
ZIPNAME="$OUTDIR.zip"
if command -v zip >/dev/null 2>&1; then
  zip -r "$ZIPNAME" "$OUTDIR" >/dev/null
  stamp "Packaged logs -> $ZIPNAME"
else
  stamp "zip not available; results in $OUTDIR"
fi

stamp "Completed: $(date -Iseconds)"

echo "Output saved to: $OUTDIR"