#!/bin/sh
# Adds DS5 Untethered (experimental Linux build) to your application menu, for your user only: the desktop entry goes to
# ~/.local/share/applications and the icon to ~/.local/share/icons. The app itself stays in this folder; run this again
# after moving the folder. Nothing here needs root. Remove with: ./install.sh --remove
set -eu

here=$(cd "$(dirname "$0")" && pwd)
data=${XDG_DATA_HOME:-$HOME/.local/share}
entry="$data/applications/ds5-untethered.desktop"
icon="$data/icons/hicolor/256x256/apps/ds5-untethered.png"

if [ "${1:-}" = "--remove" ]; then
	rm -f "$entry" "$icon"
	echo "Removed the menu entry and the icon."
	exit 0
fi

mkdir -p "$data/applications" "$data/icons/hicolor/256x256/apps"
cp "$here/ds5-untethered.png" "$icon"
exe="$here/Kopru.App"

# Exec (Desktop Entry Specification): the path in double quotes with " ` $ \ escaped by a backslash and % doubled.
# TryExec takes the plain path. Both are then escaped for sed's replacement (\ & and the | delimiter).
exec_value=$(printf '%s' "$exe" | sed -e 's/[\\"`$]/\\&/g' -e 's/%/%%/g')
exec_value="\"$exec_value\""
sed_escape() { printf '%s' "$1" | sed -e 's/[\\&|]/\\&/g'; }
exec_sed=$(sed_escape "$exec_value")
tryexec_sed=$(sed_escape "$exe")

sed -e "s|^Exec=.*|Exec=$exec_sed|" -e "s|^TryExec=.*|TryExec=$tryexec_sed|" "$here/ds5-untethered.desktop" > "$entry"
chmod +x "$exe"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$data/applications" >/dev/null 2>&1 || true
echo "DS5 Untethered is in your application menu now ($entry)."
