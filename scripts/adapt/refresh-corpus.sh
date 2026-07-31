#!/usr/bin/env bash
# Decompile the installed sts2.dll into the corpus, then summarise what the
# update touched at large (per-mod triage is surface-diff.py).
#
#   scripts/adapt/refresh-corpus.sh            # decompile installed version
#   scripts/adapt/refresh-corpus.sh --force    # re-decompile even if dir exists
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CORPUS="${CORPUS:-$(cd "$REPO/.." && pwd)/sts2_decompiled}"

# python.exe / ilspycmd are Windows binaries: hand them Windows paths, and read
# JSON through stdin so MSYS /c/… never reaches them.
winpath() { command -v cygpath >/dev/null && cygpath -w "$1" || echo "$1"; }
json_get() { python -c "import json,sys;print(json.load(sys.stdin)['$1'])"; }

steam_path() {
  local p
  p=$(reg query "HKCU\\Software\\Valve\\Steam" //v SteamPath 2>/dev/null | sed -n 's/.*REG_SZ[[:space:]]*//p' | tr -d '\r')
  [ -n "$p" ] && echo "$p" || echo "C:/Program Files (x86)/Steam"
}

GAME="${STS2_PATH:-$(steam_path)/steamapps/common/Slay the Spire 2}"
DLL="$GAME/data_sts2_windows_x86_64/sts2.dll"
FORCE="${1:-}"

[ -f "$DLL" ] || { echo "error: $DLL not found (set STS2_PATH=…)" >&2; exit 2; }
command -v ilspycmd >/dev/null || { echo "error: ilspycmd not installed (dotnet tool install -g ilspycmd)" >&2; exit 2; }

VER=$(json_get version < "$GAME/release_info.json")
OUT="$CORPUS/$VER"

if [ -d "$OUT" ] && [ "$FORCE" != "--force" ]; then
  echo "corpus $OUT already exists (pass --force to redo)"
else
  echo "decompiling $VER → $OUT  (~1 min)"
  rm -rf "$OUT"
  mkdir -p "$OUT"
  ilspycmd "$(winpath "$DLL")" -p -o "$(winpath "$OUT")" --nested-directories
  cp "$GAME/release_info.json" "$OUT/"
  # Identity anchor: release_info.json can survive a Steam branch switch, the
  # hash cannot. version-status.sh compares against this.
  sha256sum "$DLL" | cut -d' ' -f1 > "$OUT/.dll-sha256"
fi

# The version immediately below the installed one (not simply "the newest
# other one" — the corpus may already hold a beta ahead of what's installed).
PREV=$(ls -1 "$CORPUS" | grep '^v' | grep -v "^$VER\$" | sort -V | awk -v cur="$VER" '$0 < cur' | tail -1)
[ -n "$PREV" ] || exit 0

echo
echo "== $PREV → $VER, whole-assembly churn =="
{ diff -rq "$CORPUS/$PREV" "$OUT" 2>/dev/null || true; } | awk -v new_v="$VER" -v old_v="$PREV" '
  index($0, "Only in") == 1 && index($0, new_v) { new++ }
  index($0, "Only in") == 1 && index($0, old_v) { gone++ }
  /^Files .* differ$/ { chg++ }
  END { printf "  new types: %d   removed types: %d   changed types: %d\n", new, gone, chg }'
echo
echo "next: python scripts/adapt/surface-diff.py $PREV $VER --show-diff"
