"""Verify Supervertaler.Trados.plugin.xml matches the actions' attributes.

Studio builds its menus and shortcuts from the checked-in manifest, not from
the [Action], [ActionLayout] and [Shortcut] attributes in the code. Nothing
regenerates the manifest from the assembly, so an attribute changed on its own
builds, loads, and changes nothing a user can see. That happened to keyboard
shortcuts in 20.184 and to the whole right-click menu tidy in 20.210 (an
install showed the old order and names). This check blocks the build on drift.

For every class with an [Action] attribute it compares, against the manifest:
the action ID, its Name, every [ActionLayout] (location, priority, separator
flag) and the [Shortcut] keys. An action missing from either side is drift too.

Usage: python tools/check_manifest_actions.py
Exit codes: 0 = in sync, 1 = drift (each difference is listed).
"""

import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
SRC = REPO_ROOT / "src" / "Supervertaler.Trados"
MANIFEST = SRC / "Supervertaler.Trados.plugin.xml"

# System.Windows.Forms.Keys values for the keys the plugin uses. A key not in
# this table fails the check instead of being skipped: an unchecked shortcut is
# exactly the silent drift this script exists to catch.
KEYS = {"Shift": 0x10000, "Control": 0x20000, "Alt": 0x40000,
        "Up": 0x26, "Down": 0x28, "Insert": 0x2D, "Delete": 0x2E}
KEYS.update({chr(c): c for c in range(ord("A"), ord("Z") + 1)})
KEYS.update({f"D{d}": 0x30 + d for d in range(10)})
KEYS.update({f"F{n}": 0x6F + n for n in range(1, 13)})

# Drift that was there before this check and is a decision, not a typo. Each
# entry says why it is left. Keep this list short; never add to it to make a
# build pass.
KNOWN_MISSING_FROM_MANIFEST = {
    # Empty. "TermLens: Refresh termbases" (F5) was here until 20.210: it had
    # never been in the manifest, so Studio never registered it, and Michael
    # chose (22 Nov 2026) to remove it; TermLens's refresh button does the job.
}

ACTION = re.compile(r'\[Action\(\s*"([^"]+)"(.*?)\)\]', re.S)
NAME = re.compile(r'\bName\s*=\s*"((?:[^"\\]|\\.)*)"')
LAYOUT = re.compile(
    r'\[ActionLayout\(\s*typeof\(([\w.]+)\)\s*,\s*(\d+)\s*,\s*[\w.]+\s*,\s*"[^"]*"\s*,\s*(true|false)\s*\)\]', re.S)
SHORTCUT = re.compile(r'\[Shortcut\(([^)\]]*)\)\]')
CLASS = re.compile(r'\bclass\s+(\w+)')


def keys_value(expr, problems, where):
    total = 0
    for part in expr.split("|"):
        name = part.strip().replace("Keys.", "")
        if name not in KEYS:
            problems.append(f"{where}: key '{name}' is not in KEYS in this script; add it")
            return None
        total |= KEYS[name]
    return total


def from_code(problems):
    """class name -> (id, name, set of (location, priority, separator), shortcut)"""
    actions = {}
    for path in SRC.rglob("*.cs"):
        if any(p in ("obj", "bin") for p in path.parts):
            continue
        text = path.read_text(encoding="utf-8-sig")
        text = re.sub(r"^\s*//.*$", "", text, flags=re.M)  # commented-out attributes don't count
        for m in ACTION.finditer(text):
            cls = CLASS.search(text, m.end())
            if not cls:
                continue
            # Attributes may come in any order: [Shortcut] sometimes sits above
            # [Action]. The block runs from the end of the previous statement or
            # type to this class.
            start = max(text.rfind(";", 0, m.start()), text.rfind("}", 0, m.start()), 0)
            block = text[start:cls.start()]
            name = NAME.search(m.group(2))
            layouts = {(loc.split(".")[-1], int(pr), sep == "true")
                       for loc, pr, sep in LAYOUT.findall(block)}
            sc = SHORTCUT.search(block)
            where = f"{path.name} {cls.group(1)}"
            shortcut = keys_value(sc.group(1), problems, where) if sc else None
            actions[cls.group(1)] = (m.group(1), name.group(1) if name else None, layouts, shortcut)
    return actions


def from_manifest():
    root = ET.fromstring(MANIFEST.read_text(encoding="utf-16"))
    actions = {}
    for ext in root.iter("extension"):
        attr = ext.find("extensionAttribute")
        if attr is None or not attr.get("type", "").startswith(
                "Sdl.Desktop.IntegrationApi.Extensions.ActionAttribute,"):
            continue
        cls = ext.get("type").split(",")[0].split(".")[-1]
        action_id = attr.find("constructorArgs")[0].text
        props = {p.get("name"): p.text for p in list(attr.find("properties"))}
        layouts, shortcut = set(), None
        for aux in ext.iter("auxiliaryExtensionAttribute"):
            kind = aux.get("type", "").split(",")[0].split(".")[-1]
            args = [a.text for a in aux.find("constructorArgs")]
            if kind == "ActionLayoutAttribute":
                loc = args[0].split(",")[0].split(".")[-1].split("+")[-1]
                layouts.add((loc, int(args[1]), (args[4] or "").lower() == "true"))
            elif kind == "ShortcutAttribute":
                shortcut = int(args[0])
        actions[cls] = (action_id, props.get("Name"), layouts, shortcut)
    return actions


def main() -> int:
    problems = []
    code = from_code(problems)
    manifest = from_manifest()
    for cls in sorted(set(code) | set(manifest)):
        if cls not in manifest:
            if cls in KNOWN_MISSING_FROM_MANIFEST:
                continue
            problems.append(f"{cls}: has [Action] in the code but no entry in the manifest")
            continue
        if cls not in code:
            problems.append(f"{cls}: is in the manifest but has no [Action] in the code")
            continue
        (cid, cname, clay, csc), (mid, mname, mlay, msc) = code[cls], manifest[cls]
        if cid != mid:
            problems.append(f"{cls}: ID code '{cid}' vs manifest '{mid}'")
        if cname != mname:
            problems.append(f"{cls}: Name code '{cname}' vs manifest '{mname}'")
        if clay != mlay:
            problems.append(f"{cls}: ActionLayout code {sorted(clay)} vs manifest {sorted(mlay)}")
        if csc != msc:
            problems.append(f"{cls}: Shortcut code {csc} vs manifest {msc}")

    if problems:
        print("  ERROR: Supervertaler.Trados.plugin.xml does not match the action attributes.")
        print("  Studio reads the manifest, so edit it to match (UTF-16 LE; see CLAUDE.md):")
        for p in problems:
            print("    - " + p)
        return 1
    print(f"  Manifest matches the attributes of all {len(code)} actions.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
