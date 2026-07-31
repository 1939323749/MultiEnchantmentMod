#!/usr/bin/env bash
# Where am I? Prints every place a game version is pinned, so drift is obvious
# before you start debugging phantom errors.
#
#   scripts/adapt/version-status.sh
set -uo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CORPUS="${CORPUS:-$(cd "$REPO/.." && pwd)/sts2_decompiled}"

json_get() { python -c "import json,sys;print(json.load(sys.stdin).get('$1','?'))" 2>/dev/null || echo "?"; }

steam_path() {
  local p
  p=$(reg query "HKCU\\Software\\Valve\\Steam" //v SteamPath 2>/dev/null | sed -n 's/.*REG_SZ[[:space:]]*//p' | tr -d '\r')
  [ -n "$p" ] && echo "$p" || echo "C:/Program Files (x86)/Steam"
}

GAME="${STS2_PATH:-$(steam_path)/steamapps/common/Slay the Spire 2}"
DLL="$GAME/data_sts2_windows_x86_64/sts2.dll"

echo "== Installed game =="
if [ -f "$DLL" ]; then
  # NOTE: read JSON via stdin — python.exe does not understand MSYS /c/… paths.
  ver=$(json_get version < "$GAME/release_info.json")
  cmt=$(json_get commit < "$GAME/release_info.json")
  echo "  path            : $GAME"
  echo "  release_info    : $ver ($cmt)"
  echo "  sts2.dll mtime  : $(date -r "$DLL" '+%Y-%m-%d %H:%M')"
  echo "  sts2.dll sha256 : $(sha256sum "$DLL" | cut -c1-16)…"
  # release_info.json is written by the build, but a Steam branch switch can
  # leave a file whose date disagrees with the binaries. Trust the hash.
  if [ -d "$CORPUS/$ver" ] && [ -f "$CORPUS/$ver/.dll-sha256" ]; then
    if [ "$(sha256sum "$DLL" | cut -d' ' -f1)" != "$(cat "$CORPUS/$ver/.dll-sha256")" ]; then
      echo "  !! sts2.dll does NOT match the corpus decompiled as $ver — re-decompile"
    fi
  fi
else
  echo "  !! sts2.dll not found at $DLL (set STS2_PATH=…)"
fi

echo
echo "== Decompiled corpus ($CORPUS) =="
ls -1 "$CORPUS" 2>/dev/null | sed 's/^/  /'

echo
echo "== Repo pins =="
echo "  .ci/dll-version.txt (CI build target) : $(cat "$REPO/.ci/dll-version.txt" 2>/dev/null)"
echo "  manifest min_game_version             : $(json_get min_game_version < "$REPO/MultiEnchantmentMod.json")"
echo "  manifest version                      : $(json_get version < "$REPO/MultiEnchantmentMod.json")"
echo "  branch                                : $(git -C "$REPO" rev-parse --abbrev-ref HEAD 2>/dev/null)"

echo
echo "== Deployed copies =="
for d in "$GAME/mods/MultiEnchantmentMod/MultiEnchantmentMod.dll" \
         "$HOME/Downloads/ModUploader-win-x64/content/MultiEnchantmentMod.dll"; do
  [ -f "$d" ] && echo "  $(date -r "$d" '+%Y-%m-%d %H:%M')  $d"
done
ws=$(ls -d "$(steam_path)"/steamapps/workshop/content/2868840/* 2>/dev/null | head -3)
[ -n "$ws" ] && echo "  workshop subscriptions:" && echo "$ws" | sed 's/^/    /'
