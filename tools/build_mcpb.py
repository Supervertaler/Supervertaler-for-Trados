#!/usr/bin/env python3
"""Build the Supervertaler MCP Server .mcpb bundle (Claude Desktop extension).

Publishes src/Supervertaler.McpServer as a self-contained single-file
win-x64 exe and packs it into dist/Supervertaler-MCP-Server.mcpb, which
users install by double-clicking (or dragging into Claude Desktop's
Settings > Extensions). MCPB spec: https://github.com/anthropics/mcpb

Usage:
    python tools/build_mcpb.py [--version 0.1.0]
"""
import argparse
import json
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
PROJECT = REPO / "src" / "Supervertaler.McpServer"
PUBLISH_DIR = PROJECT / "bin" / "Release" / "net8.0" / "win-x64" / "publish"
DIST = REPO / "dist"
ICON = REPO / "sv-icon-512.png"


def manifest(version: str) -> dict:
    return {
        "manifest_version": "0.3",
        "name": "supervertaler-mcp-server",
        "display_name": "Supervertaler MCP Server",
        "version": version,
        "description": "Connect your AI assistant directly to your live Trados Studio project "
                       "via Supervertaler for Trados.",
        "long_description": (
            "Gives AI assistants live access to the project open in Trados Studio: project "
            "status and statistics, segment browsing with filters, translation memory search, "
            "termbase lookups, and inserting translations into the active segment. Requires "
            "Trados Studio with the Supervertaler for Trados plugin (supervertaler.com/trados). "
            "Everything stays on your machine: the connection is loopback-only and "
            "token-authenticated."
        ),
        "author": {
            "name": "Michael Beijer (Supervertaler)",
            "url": "https://supervertaler.com",
        },
        "homepage": "https://supervertaler.com/trados.html",
        "documentation": "https://docs.supervertaler.com/trados/",
        "icon": "icon.png",
        "server": {
            "type": "binary",
            "entry_point": "server/SupervertalerMcpServer.exe",
            "mcp_config": {
                "command": "${__dirname}/server/SupervertalerMcpServer.exe",
                "args": [],
                "env": {},
            },
        },
        "compatibility": {
            "platforms": ["win32"],
        },
    }


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", default="0.1.0")
    ap.add_argument("--skip-publish", action="store_true",
                    help="reuse the existing publish output")
    args = ap.parse_args()

    if not args.skip_publish:
        print("== dotnet publish (Release, win-x64, self-contained single file) ==")
        subprocess.run(
            ["dotnet", "publish", "-c", "Release", "-r", "win-x64",
             "--self-contained", "true",
             "-p:PublishSingleFile=true",
             "-p:IncludeNativeLibrariesForSelfExtract=true"],
            cwd=PROJECT, check=True)

    exe = PUBLISH_DIR / "SupervertalerMcpServer.exe"
    if not exe.exists():
        print(f"ERROR: publish output not found at {exe}", file=sys.stderr)
        return 1

    DIST.mkdir(exist_ok=True)
    out = DIST / "Supervertaler-MCP-Server.mcpb"
    if out.exists():
        out.unlink()

    print(f"== packing {out.name} ==")
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("manifest.json", json.dumps(manifest(args.version), indent=2))
        z.write(exe, "server/SupervertalerMcpServer.exe")
        if ICON.exists():
            z.write(ICON, "icon.png")
        else:
            print(f"  (icon not found at {ICON} - skipped)")

    size_mb = out.stat().st_size / (1024 * 1024)
    print(f"Done: {out}  ({size_mb:.1f} MB, server exe "
          f"{exe.stat().st_size / (1024 * 1024):.1f} MB uncompressed)")
    print("Install: double-click the .mcpb, or Claude Desktop > Settings > Extensions.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
