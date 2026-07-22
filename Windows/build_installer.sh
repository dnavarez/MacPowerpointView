#!/bin/bash
# Builds SlideViewer for Windows and packages a single-file installer.
# Runs on macOS, Linux, or Windows — only the .NET SDK is required.
#
#   ./build_installer.sh
#
# Output: Windows/dist/SlideViewer-Setup.exe
#
# The app is published self-contained (the .NET runtime ships inside) and then
# embedded in the installer, so the end user installs with zero prerequisites
# and zero downloads.
set -euo pipefail
cd "$(dirname "$0")"

PUBLISH_DIR="publish/win-x64"
DIST_DIR="dist"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}"

command -v dotnet >/dev/null || { echo "✗ .NET SDK not found (brew install dotnet)"; exit 1; }

echo "▶ [1/4] Publishing self-contained win-x64 application…"
rm -rf "$PUBLISH_DIR"
dotnet publish SlideViewer/SlideViewer.csproj \
  -c Release -r win-x64 --self-contained true \
  -o "$PUBLISH_DIR" -v q
[ -f "$PUBLISH_DIR/SlideViewer.exe" ] || { echo "✗ publish produced no SlideViewer.exe"; exit 1; }
echo "    $(find "$PUBLISH_DIR" -type f | wc -l | tr -d ' ') files, $(du -sh "$PUBLISH_DIR" | cut -f1 | tr -d ' ')"

echo "▶ [2/4] Packing application payload…"
rm -f Installer/payload.zip
# -X strips extra attributes so the archive is portable and reproducible.
(cd "$PUBLISH_DIR" && zip -q -r -X -9 "$OLDPWD/Installer/payload.zip" .)
echo "    payload.zip $(du -h Installer/payload.zip | cut -f1 | tr -d ' ')"

echo "▶ [3/4] Building installer…"
cp SlideViewer/Assets/AppIcon.ico Installer/AppIcon.ico
rm -rf Installer/bin Installer/obj
dotnet publish Installer/Installer.csproj \
  -c Release -r win-x64 --self-contained true \
  -o "$DIST_DIR" -v q

echo "▶ [4/4] Verifying…"
SETUP="$DIST_DIR/SlideViewer-Setup.exe"
[ -f "$SETUP" ] || { echo "✗ installer was not produced"; exit 1; }
file "$SETUP" | grep -q "PE32+ executable" || { echo "✗ output is not a Windows executable"; exit 1; }
# Drop build leftovers so dist/ holds just the deliverable.
find "$DIST_DIR" -type f ! -name 'SlideViewer-Setup.exe' -delete 2>/dev/null || true
find "$DIST_DIR" -type d -empty -delete 2>/dev/null || true

echo
echo "✓ Installer ready:"
echo "  $(pwd)/$SETUP  ($(du -h "$SETUP" | cut -f1 | tr -d ' '))"
echo "  $(file -b "$SETUP")"
echo
echo "  Copy it to the Windows laptop and double-click. Nothing else to install."
