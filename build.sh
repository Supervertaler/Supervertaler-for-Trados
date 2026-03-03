#!/bin/bash
# Build and package Termview for Trados Studio
# Produces: dist/Termview.sdlplugin (OPC format)
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR/src/Termview"
DIST_DIR="$SCRIPT_DIR/dist"
BUILD_DIR="$PROJECT_DIR/bin/Release"
DOTNET="${HOME}/.dotnet/dotnet"

echo "=== Building Termview ==="
"$DOTNET" build "$PROJECT_DIR/Termview.csproj" -c Release

echo ""
echo "=== Packaging Termview.sdlplugin (OPC format) ==="
mkdir -p "$DIST_DIR"
rm -f "$DIST_DIR/Termview.sdlplugin"

python "$SCRIPT_DIR/package_plugin.py" "$BUILD_DIR" "$DIST_DIR/Termview.sdlplugin"

echo ""
echo "=== Done ==="
echo "Package: $DIST_DIR/Termview.sdlplugin"
echo ""
echo "To install manually:"
echo "  Copy Termview.sdlplugin to:"
echo "  %LOCALAPPDATA%\\Trados\\Trados Studio\\18\\Plugins\\Packages\\"
echo "  Then restart Trados Studio."
