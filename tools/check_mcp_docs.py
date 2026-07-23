"""Verify the MCP tool table in the help docs matches mcp-tools.json.

The docs page (Supervertaler-Help repo, trados/mcp-server.md) carries a
hand-written "What the AI can do" table. The prose is deliberately
hand-maintained (the JSON descriptions are written for the AI, not for
humans), but the *set of tool names* must match the shipped tool set
exactly. This check blocks the build on drift, mirroring the version
sync check in build.sh.

Usage: python tools/check_mcp_docs.py
Exit codes: 0 = in sync (or docs repo not present - warning only), 1 = drift.
"""

import json
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
TOOLS_JSON = REPO_ROOT / "src" / "Supervertaler.Trados" / "Resources" / "mcp-tools.json"
# The help docs live in the sibling Supervertaler-Help repo.
DOCS_PAGE = REPO_ROOT.parent / "Supervertaler-Help" / "trados" / "mcp-server.md"

# A docs-table row: first cell is a backticked tool name. Prose mentions of
# tool names never start a line with "| `", so this only matches table rows.
TABLE_ROW = re.compile(r"^\|\s*`([a-z0-9_]+)`\s*\|")


def main() -> int:
    with open(TOOLS_JSON, encoding="utf-8") as f:
        shipped = {t["name"] for t in json.load(f)["tools"]}

    if not DOCS_PAGE.exists():
        print(f"  WARNING: help docs page not found ({DOCS_PAGE})")
        print("  Skipping MCP docs drift check - is the Supervertaler-Help repo present?")
        return 0

    with open(DOCS_PAGE, encoding="utf-8") as f:
        documented = {m.group(1) for line in f if (m := TABLE_ROW.match(line))}

    missing = sorted(shipped - documented)   # shipped but not in the docs table
    stale = sorted(documented - shipped)     # in the docs table but no longer shipped

    if missing or stale:
        print("")
        print("  ERROR: MCP docs drift detected!")
        for name in missing:
            print(f"    missing from docs table: {name}")
        for name in stale:
            print(f"    in docs table but not shipped: {name}")
        print("")
        print(f"  Update the tool table in {DOCS_PAGE}")
        print(f"  to match {TOOLS_JSON.relative_to(REPO_ROOT)}.")
        print("")
        return 1

    print(f"  MCP docs check passed: {len(shipped)} tools documented ({DOCS_PAGE.name})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
