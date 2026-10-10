#!/usr/bin/env bash
set -euo pipefail

: "${XDG_RUNTIME_DIR:?XDG_RUNTIME_DIR must be set before starting D-Bus}"
: "${WAYLAND_DISPLAY:?WAYLAND_DISPLAY must be set before starting D-Bus}"

kwin_wayland --virtual --no-lockscreen --socket "$WAYLAND_DISPLAY" \
  --width 1280 --height 800 >kwin-wayland.log 2>&1 &
kwin_pid=$!
trap 'kill "$kwin_pid" 2>/dev/null || true; cat kwin-wayland.log' EXIT

for attempt in $(seq 1 40); do
  if [[ -S "$XDG_RUNTIME_DIR/$WAYLAND_DISPLAY" ]] && \
     gdbus introspect --session --dest org.kde.KWin \
       --object-path /KWin >/dev/null 2>&1; then
    /usr/bin/python3 linux/tests/wayland_smoke.py
    /usr/bin/python3 linux/tests/portal_smoke.py
    exit 0
  fi
  if ! kill -0 "$kwin_pid" 2>/dev/null; then
    echo "KWin exited before the portal test" >&2
    exit 1
  fi
  sleep 1
done

echo "KWin did not become ready" >&2
exit 1
