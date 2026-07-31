#!/usr/bin/env python3
"""Report what a game update changed *inside this mod's patch surface*.

The compiler already catches signature-level breakage for free (a wall of
CS0246/CS0117 when sts2.dll moved).  What it cannot see:

  * string-based reflection targets (``AccessTools.Field(typeof(X), "_f")``)
    -> silently null at runtime,
  * hand-copied vanilla method bodies (``// Base-game source: T.M``)
    -> silently executes last version's rules,
  * Harmony patch targets whose *body* changed while the signature held.

This script diffs two decompiled corpora, filtered to the members this mod
actually touches, and triages the result.

Usage:
    python scripts/adapt/surface-diff.py OLD_VERSION NEW_VERSION [--corpus DIR]
    python scripts/adapt/surface-diff.py v0.108.0 v0.109.0
    python scripts/adapt/surface-diff.py v0.108.0 v0.109.0 --show-diff
"""

from __future__ import annotations

import argparse
import difflib
import re
import sys
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
DEFAULT_CORPUS = REPO.parent / "sts2_decompiled"

# Mod source files to scan (skip worktrees / build output).
SOURCE_GLOBS = ("*.cs",)
SKIP_DIRS = {".claude", ".godot", "obj", "bin", "packages", "_release", ".decomp"}

# ---------------------------------------------------------------------------
# 1. Extract this mod's patch surface from its own source
# ---------------------------------------------------------------------------

# [HarmonyPatch(typeof(Hook), nameof(Hook.AfterCardDrawn))]
RE_HARMONY = re.compile(
    r"HarmonyPatch\(\s*typeof\(([A-Za-z0-9_.]+)\)\s*,\s*nameof\(\s*[A-Za-z0-9_.]*?([A-Za-z0-9_]+)\s*\)"
)
# [HarmonyPatch(typeof(X), "PrivateName")]
RE_HARMONY_STR = re.compile(r"HarmonyPatch\(\s*typeof\(([A-Za-z0-9_.]+)\)\s*,\s*\"([A-Za-z0-9_]+)\"")
# AccessTools.Field(typeof(CardModel), "_temporaryStarCosts")
RE_ACCESSTOOLS = re.compile(
    r"AccessTools\.(?:Declared)?(?:Method|Field|Property|PropertyGetter|PropertySetter)"
    r"\(\s*typeof\(([A-Za-z0-9_.]+)\)\s*,\s*\"([A-Za-z0-9_]+)\""
)
# // Base-game source: CardModel.OnPlayWrapper.
RE_BASEGAME = re.compile(r"Base-game source:\s*([A-Za-z0-9_.]+)\.([A-Za-z0-9_]+)")
# Reflection we cannot resolve statically (variable receiver) - listed for manual review.
RE_DYNAMIC = re.compile(
    r"(AccessTools\.(?:Method|Field|Property|TypeByName)\(\s*(?!typeof)[A-Za-z_][A-Za-z0-9_.]*\s*,?[^)]*\))"
)

KIND_HARMONY = "harmony"
KIND_REFLECT = "reflect"
KIND_COPY = "copy"

# A member reached only through reflection or copied by hand is a *silent* break;
# a Harmony target with a nameof() reference is compiler-checked.
SILENT_KINDS = {KIND_REFLECT, KIND_COPY}


@dataclass
class SurfaceMember:
    type_name: str
    member: str
    kinds: set[str] = field(default_factory=set)
    sites: list[str] = field(default_factory=list)

    @property
    def key(self) -> str:
        return f"{self.type_name}.{self.member}"

    @property
    def silent(self) -> bool:
        return bool(self.kinds & SILENT_KINDS)


def iter_source_files(root: Path):
    for path in root.rglob("*.cs"):
        if any(part in SKIP_DIRS for part in path.relative_to(root).parts):
            continue
        yield path


def extract_surface(root: Path) -> tuple[dict[str, SurfaceMember], list[str]]:
    surface: dict[str, SurfaceMember] = {}
    dynamic: list[str] = []

    def add(type_name: str, member: str, kind: str, site: str) -> None:
        type_name = type_name.split(".")[-1]  # corpus is one file per type
        key = f"{type_name}.{member}"
        entry = surface.setdefault(key, SurfaceMember(type_name, member))
        entry.kinds.add(kind)
        if site not in entry.sites:
            entry.sites.append(site)

    for path in iter_source_files(root):
        rel = path.relative_to(root).as_posix()
        try:
            text = path.read_text(encoding="utf-8")
        except UnicodeDecodeError:
            continue
        for lineno, line in enumerate(text.splitlines(), 1):
            site = f"{rel}:{lineno}"
            for regex, kind in (
                (RE_HARMONY, KIND_HARMONY),
                (RE_HARMONY_STR, KIND_HARMONY),
                (RE_ACCESSTOOLS, KIND_REFLECT),
                (RE_BASEGAME, KIND_COPY),
            ):
                for m in regex.finditer(line):
                    add(m.group(1), m.group(2), kind, site)
            for m in RE_DYNAMIC.finditer(line):
                dynamic.append(f"{site}: {m.group(1).strip()}")

    return surface, dynamic


# ---------------------------------------------------------------------------
# 2. Locate types + members in a decompiled corpus
# ---------------------------------------------------------------------------

MODIFIERS = (
    "public|private|protected|internal|static|virtual|override|abstract|sealed|"
    "async|extern|unsafe|new|readonly|partial|const|event"
)


def build_index(corpus_root: Path) -> dict[str, list[Path]]:
    index: dict[str, list[Path]] = defaultdict(list)
    for path in corpus_root.rglob("*.cs"):
        index[path.stem].append(path)
    return index


RE_GODOT_NAME_CACHE = re.compile(r"\bStringName\b[^=]*=\s*\"")


def decl_regex(member: str) -> re.Pattern[str]:
    return re.compile(
        r"^[ \t]*(?:\[[^\]]*\][ \t]*)*"
        rf"(?:(?:{MODIFIERS})[ \t]+)+"
        # return type: anything but a bare paren, plus one balanced group so
        # tuple returns like `(PileType, CardPilePosition)` still parse.
        r"(?:[^;={}()]|\([^()]*\))*?"
        rf"\b{re.escape(member)}\b[ \t]*"
        r"(?:<[^<>()]*>[ \t]*)?"
        r"(?:\(|=>|\{|;|=[^=])"
    )


def extract_members(text: str, member: str) -> list[tuple[str, str]]:
    """Return [(declaration, body)] for every overload of `member` in `text`."""
    lines = text.splitlines()
    regex = decl_regex(member)
    found: list[tuple[str, str]] = []
    for i, line in enumerate(lines):
        if not regex.match(line):
            continue
        if RE_GODOT_NAME_CACHE.search(line):
            continue  # Godot source-gen PropertyName/MethodName/SignalName constants
        decl, body = read_member(lines, i)
        found.append((decl, body))
    return found


def read_member(lines: list[str], start: int) -> tuple[str, str]:
    """Read a member starting at `start`: signature line(s) + braced/expression body."""
    # Signature spans until the parameter list closes (or the line ends the decl).
    depth = 0
    sig_end = start
    for i in range(start, min(start + 40, len(lines))):
        depth += lines[i].count("(") - lines[i].count(")")
        sig_end = i
        if depth <= 0 and ("(" in "".join(lines[start : i + 1]) or i == start):
            break
    decl = " ".join(l.strip() for l in lines[start : sig_end + 1])

    # Field / const / abstract member: declaration only, nothing to compare.
    if decl.rstrip().endswith(";") and "=>" not in decl:
        return decl, ""

    # Expression body: consume until the terminating ';'.
    tail = "\n".join(lines[sig_end : sig_end + 2])
    if "=>" in decl or "=>" in tail:
        chunk = []
        for i in range(sig_end, min(sig_end + 60, len(lines))):
            chunk.append(lines[i])
            if lines[i].rstrip().endswith(";"):
                break
        return decl, "\n".join(chunk)

    # Braced body: brace-match from the first '{' at or after the signature.
    body_start = None
    for i in range(sig_end, min(sig_end + 6, len(lines))):
        if "{" in lines[i]:
            body_start = i
            break
    if body_start is None:
        return decl, ""  # abstract / interface member

    depth = 0
    chunk = []
    for i in range(body_start, len(lines)):
        depth += lines[i].count("{") - lines[i].count("}")
        chunk.append(lines[i])
        if depth <= 0:
            break
    return decl, "\n".join(chunk)


def normalize(text: str) -> str:
    text = re.sub(r"//.*", "", text)
    text = re.sub(r"\s+", " ", text)
    return text.strip()


# ---------------------------------------------------------------------------
# 3. Compare
# ---------------------------------------------------------------------------

@dataclass
class Finding:
    severity: str  # BREAK | SIGNATURE | BODY
    member: SurfaceMember
    detail: str
    diff: str = ""


def compare(surface, old_index, new_index, corpus_old, corpus_new) -> list[Finding]:
    findings: list[Finding] = []
    for key, entry in sorted(surface.items()):
        old_paths = old_index.get(entry.type_name, [])
        new_paths = new_index.get(entry.type_name, [])
        if not old_paths:
            continue  # unknown in the old corpus too - not an update regression
        if not new_paths:
            findings.append(Finding("BREAK", entry, f"type `{entry.type_name}` no longer exists"))
            continue

        old_text = old_paths[0].read_text(encoding="utf-8", errors="replace")
        new_text = new_paths[0].read_text(encoding="utf-8", errors="replace")

        old_members = extract_members(old_text, entry.member)
        new_members = extract_members(new_text, entry.member)

        if old_members and not new_members:
            hint = "renamed or removed"
            if re.search(rf"\b{re.escape(entry.member)}\b", new_text):
                hint = "declaration gone (still referenced elsewhere in the type)"
            findings.append(Finding("BREAK", entry, f"member `{entry.member}` {hint}"))
            continue
        if not old_members:
            continue  # could not parse it before either - nothing to compare

        old_decls = {normalize(d) for d, _ in old_members}
        new_decls = {normalize(d) for d, _ in new_members}
        if old_decls != new_decls:
            lost = sorted(old_decls - new_decls)
            gained = sorted(new_decls - old_decls)
            detail = "signature changed"
            diff = "\n".join(["- " + d for d in lost] + ["+ " + d for d in gained])
            findings.append(Finding("SIGNATURE", entry, detail, diff))
            continue

        old_bodies = [normalize(b) for _, b in old_members]
        new_bodies = [normalize(b) for _, b in new_members]
        if old_bodies != new_bodies:
            raw_old = "\n".join(b for _, b in old_members).splitlines()
            raw_new = "\n".join(b for _, b in new_members).splitlines()
            diff = "\n".join(
                difflib.unified_diff(raw_old, raw_new, lineterm="", n=2, fromfile="old", tofile="new")
            )
            findings.append(Finding("BODY", entry, "body changed, signature identical", diff))
    return findings


SEVERITY_ORDER = {"BREAK": 0, "SIGNATURE": 1, "BODY": 2}


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("old", help="old corpus version dir name, e.g. v0.108.0")
    ap.add_argument("new", help="new corpus version dir name, e.g. v0.109.0")
    ap.add_argument("--corpus", default=str(DEFAULT_CORPUS), help="decompiled corpus root")
    ap.add_argument("--repo", default=str(REPO), help="mod source root")
    ap.add_argument("--show-diff", action="store_true", help="print body/signature diffs")
    ap.add_argument("--list-dynamic", action="store_true", help="list unresolvable reflection sites")
    args = ap.parse_args()

    corpus = Path(args.corpus)
    corpus_old, corpus_new = corpus / args.old, corpus / args.new
    for p in (corpus_old, corpus_new):
        if not p.is_dir():
            print(f"error: corpus not found: {p}", file=sys.stderr)
            return 2

    surface, dynamic = extract_surface(Path(args.repo))
    findings = compare(surface, build_index(corpus_old), build_index(corpus_new), corpus_old, corpus_new)
    findings.sort(key=lambda f: (SEVERITY_ORDER[f.severity], not f.member.silent, f.member.key))

    print(f"# Patch-surface diff {args.old} -> {args.new}\n")
    print(f"Tracked members: {len(surface)}  |  findings: {len(findings)}\n")

    if not findings:
        print("No tracked member changed. Build + smoke test is likely all that's needed.\n")
    for f in findings:
        flag = "SILENT" if f.member.silent else "compiler-checked"
        kinds = ",".join(sorted(f.member.kinds))
        print(f"## [{f.severity}] {f.member.key}  ({kinds}; {flag})")
        print(f"    {f.detail}")
        for site in f.member.sites[:4]:
            print(f"    site: {site}")
        if args.show_diff and f.diff:
            print()
            for line in f.diff.splitlines()[:60]:
                print(f"    {line}")
        print()

    if args.list_dynamic and dynamic:
        print("## Reflection sites not statically resolvable (manual review)\n")
        for d in dynamic:
            print(f"    {d}")

    # Exit 1 when something silent needs a human: CI/agent gate.
    return 1 if any(f.member.silent for f in findings) else 0


if __name__ == "__main__":
    raise SystemExit(main())
