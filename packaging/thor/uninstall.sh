#!/usr/bin/env bash
set -euo pipefail

GAME_DIR="${STEAM_GAME_DIR:-/var/home/$USER/.local/share/Steam/steamapps/common/Stardew Valley}"
MOD_DIR="$GAME_DIR/Mods/VerticalWorld"

if pgrep -f 'StardewModdingAPI --no-terminal' >/dev/null 2>&1; then
  echo "Save and fully exit Stardew Valley before uninstalling."
  exit 1
fi

systemctl --user disable --now stardew-thor-bridge.service 2>/dev/null || true
rm -f -- "$HOME/.config/systemd/user/stardew-thor-bridge.service"
systemctl --user daemon-reload

if [[ -d "$MOD_DIR" ]]; then
  ARCHIVE="$MOD_DIR.removed-$(date +%Y%m%d-%H%M%S)"
  mv -- "$MOD_DIR" "$ARCHIVE"
  echo "Mod moved to: $ARCHIVE"
fi

echo "The ArmadaOS game-tweaks entry was left in place so other custom settings are not lost."
echo "The mod is disabled because its files and user service are no longer active."
