#!/usr/bin/env python3
"""Refresh the installed Claude Desktop extension's exe with the current build.

The .mcpb extension is a FROZEN copy of SupervertalerMcpServer.exe taken when the
bundle was built. Rebuilding the server (or adding tools) does NOT update it, so
Claude Desktop keeps serving the old tool list until this runs.

This publishes a fresh self-contained win-x64 exe and copies it over the exe
inside the installed extension folder. Claude Desktop MUST be closed first (it
locks the running exe). Restart Claude Desktop afterwards to pick up the new tools.

Usage:
    python tools/update_extension.py            # publish + copy
    python tools/update_extension.py --skip-publish   # reuse last publish
"""
import argparse
import glob
import os
import shutil
import subprocess
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROJECT = os.path.join(REPO, "src", "Supervertaler.McpServer")
PUBLISH_EXE = os.path.join(PROJECT, "bin", "Release", "net8.0", "win-x64",
                           "publish", "SupervertalerMcpServer.exe")


def find_extension_exe():
    root = os.path.join(os.environ["APPDATA"], "Claude", "Claude Extensions")
    hits = glob.glob(os.path.join(root, "*supervertaler-mcp-server*",
                                  "server", "SupervertalerMcpServer.exe"))
    return hits[0] if hits else None


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--skip-publish", action="store_true")
    args = ap.parse_args()

    target = find_extension_exe()
    if not target:
        print("ERROR: Supervertaler extension not found in Claude Extensions folder.",
              file=sys.stderr)
        print("Install the .mcpb once (Settings > Extensions > Advanced settings), then re-run.",
              file=sys.stderr)
        return 1

    if not args.skip_publish:
        print("== dotnet publish (Release, win-x64, self-contained single file) ==")
        r = subprocess.run(
            ["dotnet", "publish", "-c", "Release", "-r", "win-x64",
             "--self-contained", "true",
             "-p:PublishSingleFile=true",
             "-p:IncludeNativeLibrariesForSelfExtract=true"],
            cwd=PROJECT)
        if r.returncode != 0:
            print("ERROR: publish failed", file=sys.stderr)
            return 1

    if not os.path.exists(PUBLISH_EXE):
        print(f"ERROR: publish output not found at {PUBLISH_EXE}", file=sys.stderr)
        return 1

    try:
        shutil.copy2(PUBLISH_EXE, target)
    except PermissionError:
        print("ERROR: could not overwrite the extension exe - is Claude Desktop still "
              "running? Quit it fully (including the system-tray icon) and re-run.",
              file=sys.stderr)
        return 1

    size_mb = os.path.getsize(target) / (1024 * 1024)
    print(f"Updated extension exe: {target}  ({size_mb:.1f} MB)")
    print("Restart Claude Desktop to load the new tool list.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
