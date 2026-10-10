#!/usr/bin/env bash
set -euo pipefail

export XDG_RUNTIME_DIR="$(mktemp -d)"
chmod 700 "$XDG_RUNTIME_DIR"
export XDG_CURRENT_DESKTOP=GNOME
export XDG_SESSION_TYPE=wayland
export GDK_BACKEND=wayland
export LIBGL_ALWAYS_SOFTWARE=1
export WAYLAND_DISPLAY=wayland-sts

gnome-shell --wayland --no-x11 --headless --virtual-monitor=1280x800 \
  --wayland-display="$WAYLAND_DISPLAY" >gnome-shell.log 2>&1 &
shell_pid=$!
trap 'kill "$shell_pid" 2>/dev/null || true; cat gnome-shell.log' EXIT

for attempt in $(seq 1 40); do
  if [[ -S "$XDG_RUNTIME_DIR/$WAYLAND_DISPLAY" ]] && \
     gdbus introspect --session --dest org.gnome.Shell \
       --object-path /org/gnome/Shell >/dev/null 2>&1; then
    /usr/bin/python3 linux/tests/portal_smoke.py
    exit 0
  fi
  if ! kill -0 "$shell_pid" 2>/dev/null; then
    echo "GNOME Shell exited before the portal test" >&2
    exit 1
  fi
  sleep 1
done

echo "GNOME Shell did not become ready" >&2
exit 1
