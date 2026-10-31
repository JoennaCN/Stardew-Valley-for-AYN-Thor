#!/usr/bin/env bash
set -euo pipefail

APP_ID=413150
PACKAGE_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
SOURCE_MOD="$PACKAGE_DIR/VerticalWorld"
GAME_DIR="${STEAM_GAME_DIR:-}"

if pgrep -f 'StardewModdingAPI --no-terminal' >/dev/null 2>&1; then
  echo "Stardew Valley is running. Save and fully exit the game, then run this installer again."
  exit 1
fi

if [[ -z "$GAME_DIR" ]]; then
  for candidate in \
    "/var/home/$USER/.local/share/Steam/steamapps/common/Stardew Valley" \
    "$HOME/.local/share/Steam/steamapps/common/Stardew Valley"; do
    if [[ -d "$candidate" ]]; then GAME_DIR="$candidate"; break; fi
  done
fi

if [[ -z "$GAME_DIR" || ! -d "$GAME_DIR" ]]; then
  echo "Steam Stardew Valley directory was not found."
  echo "Run again with: STEAM_GAME_DIR='/path/to/Stardew Valley' ./install.sh"
  exit 1
fi
if [[ ! -f "$GAME_DIR/StardewModdingAPI" ]]; then
  echo "SMAPI was not found in: $GAME_DIR"
  echo "Install SMAPI 4.5.2 or later before installing this dual-screen mod."
  exit 1
fi

MOD_DIR="$GAME_DIR/Mods/VerticalWorld"
BACKUP=""
if [[ -e "$MOD_DIR" ]]; then
  BACKUP="$MOD_DIR.backup-$(date +%Y%m%d-%H%M%S)"
  mv -- "$MOD_DIR" "$BACKUP"
fi
mkdir -p -- "$GAME_DIR/Mods"
cp -a -- "$SOURCE_MOD" "$MOD_DIR"

SERVICE_DIR="$HOME/.config/systemd/user"
SERVICE_FILE="$SERVICE_DIR/stardew-thor-bridge.service"
mkdir -p -- "$SERVICE_DIR"
python3 - "$MOD_DIR" "$SOURCE_MOD/armada/thor/systemd/stardew-thor-bridge.service.in" "$SERVICE_FILE" <<'PY'
import pathlib, sys
mod, source, target = sys.argv[1:]
text = pathlib.Path(source).read_text().replace("@MOD_DIRECTORY@", mod)
pathlib.Path(target).write_text(text)
PY

TWEAKS=/etc/armada/game-tweaks.json
if [[ ! -f "$TWEAKS" ]]; then
  echo "ArmadaOS game-tweaks configuration was not found: $TWEAKS"
  [[ -n "$BACKUP" ]] && rm -rf -- "$MOD_DIR" && mv -- "$BACKUP" "$MOD_DIR"
  exit 1
fi
TEMP_TWEAKS="$(mktemp)"
python3 - "$TWEAKS" "$TEMP_TWEAKS" "$APP_ID" <<'PY'
import json, sys
source, target, app_id = sys.argv[1:]
with open(source) as f: data = json.load(f)
game = data.setdefault("games", {}).setdefault(app_id, {})
game["enabled"] = True
env = game.setdefault("env", {})
env.update({
    "SDV_THOR_EXPORT_DIVISOR": "4",
    "SDV_THOR_EXPORT_FPS": "60",
    "SDV_THOR_UI_FPS": "10",
    "SDV_THOR_UI_DIVISOR": "2",
    "SDV_THOR_PBO": "1",
    "SDV_THOR_DMABUF": "1",
    "SMAPI_NO_TERMINAL": "true",
    "SDV_FIRST_USE_WARMUP": "1",
    "SDV_MOVEMENT_NOGC": "1",
    "SDV_MOVEMENT_NOGC_MB": "192"
})
# Diagnostic collection is intentionally disabled in public builds.
env.pop("SDV_FRAME_METRICS", None)
env.pop("SDV_FRAME_METRICS_WINDOW_MS", None)
game.setdefault("thunks", {}).update({
    "GL": True, "Vulkan": False, "WaylandClient": True,
    "asound": True, "drm": True
})
with open(target, "w") as f: json.dump(data, f, indent=2); f.write("\n")
PY
sudo install -m 0644 -- "$TEMP_TWEAKS" "$TWEAKS"
rm -f -- "$TEMP_TWEAKS"

systemctl --user daemon-reload
systemctl --user enable --now stardew-thor-bridge.service

echo "Installed Stardew Valley for AYN Thor."
echo "Game directory: $GAME_DIR"
[[ -n "$BACKUP" ]] && echo "Previous mod backup: $BACKUP"
echo "Launch Stardew Valley normally from Steam."
