#!/bin/bash
# Golden-image regression check.
#
#   ./verify_renders.sh            compare renders against recorded goldens
#   ./verify_renders.sh --record   (re)record goldens after an APPROVED change
#
# Renders reference slides with `SlideViewer --render` and pixel-diffs them
# against goldens in .goldens/ (machine-local, gitignored — the reference decks
# are copyrighted and live in ~/Downloads; cases skip when a deck is absent).
#
# Record new goldens ONLY after visually confirming the change is intended.
set -uo pipefail
cd "$(dirname "$0")"

GOLDEN_DIR=".goldens"
OUT_DIR=$(mktemp -d)
BIN=".build/debug/PowerpointViewer"
RECORD=0
[ "${1:-}" = "--record" ] && RECORD=1

echo "▶ Building…"
swift build >/dev/null 2>&1 || { echo "BUILD FAILED"; exit 1; }
mkdir -p "$GOLDEN_DIR"

# deck|slide-index|name  — the slides that regressed at least once.
CASES=(
  "$HOME/Downloads/HymnLyrics_PanataAwit_TAGALOG.pptx|0|panata-index-table"
  "$HOME/Downloads/HymnLyrics_PanataAwit_TAGALOG.pptx|4|panata-centered-lyrics"
  "$HOME/Downloads/HymnLyrics_PanataAwit_TAGALOG.pptx|15|panata-title-one-line"
  "$HOME/Downloads/HymnLyrics_AnniversaryThanksgiving2026_TAGALOG.pptx|3|anniv-lyrics-overlay"
  "$HOME/Downloads/PANGKALAHATANG PULONG_011226.pptx|0|pulong-title"
  "$HOME/Downloads/PANGKALAHATANG PULONG_011226.pptx|11|pulong-bullets"
  "$HOME/Downloads/PANGKALAHATANG PULONG_011226.pptx|12|pulong-cropped-posters"
)

fail=0; ran=0; skipped=0
for case_line in "${CASES[@]}"; do
  IFS='|' read -r deck index name <<< "$case_line"
  if [ ! -f "$deck" ]; then
    echo "SKIP  $name (deck not present)"
    skipped=$((skipped + 1)); continue
  fi
  out="$OUT_DIR/$name.png"
  if ! "$BIN" --render "$deck" "$index" "$out" >/dev/null 2>&1; then
    echo "FAIL  $name (render crashed)"; fail=1; continue
  fi
  golden="$GOLDEN_DIR/$name.png"
  if [ "$RECORD" = 1 ] || [ ! -f "$golden" ]; then
    cp "$out" "$golden"
    echo "REC   $name"
    continue
  fi
  ran=$((ran + 1))
  if result=$(swift Tools/imgdiff.swift "$golden" "$out" 2>&1); then
    echo "PASS  $name ($result)"
  else
    cp "$out" "$GOLDEN_DIR/$name.FAILED.png"
    echo "FAIL  $name — $result"
    echo "      compare: open $golden $GOLDEN_DIR/$name.FAILED.png"
    fail=1
  fi
done

echo
if [ "$fail" = 1 ]; then
  echo "✗ Golden check FAILED — a slide renders differently than the approved state."
  exit 1
fi
echo "✓ Golden check passed ($ran compared, $skipped skipped)"
