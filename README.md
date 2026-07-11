# SlideViewer

**Viewer for PowerPoint & PPTX** — a native macOS application that opens and
displays `.pptx` presentations. It parses the Open XML (OOXML) format directly
and renders slides with SwiftUI — no Microsoft Office, no web view, no
third-party dependencies.

![status](https://img.shields.io/badge/platform-macOS%2014%2B-blue)

## Features

- **Opens `.pptx` files** via the Open dialog, drag-and-drop, or the File menu
  (legacy `.ppt` converts automatically when LibreOffice is installed)
- **Native slide rendering** — text (fonts, size, bold/italic/underline/strike,
  color, alignment, line spacing, shrink-to-fit), pictures with crops, tables,
  group shapes, connectors, and 15+ autoshape geometries with solid, gradient,
  and picture fills
- **Animations** — click-triggered entrance/exit builds (including
  paragraph-by-paragraph), and slide transitions rendered as crossfades
- **Presenter mode** — separate full-screen window on the extended display,
  presenter console with current/next previews, camera panel, elapsed timer
- **Placeholder inheritance** — titles and body text positioned correctly by
  resolving geometry and default text sizes from slide layouts and masters
- **Theme colors** — resolves `schemeClr` references against the deck's theme
- **Thumbnail navigator** in a sidebar
- **Presentation mode** — full-screen, click / arrow-key / space navigation
- **Keyboard navigation** — ←/→ between slides, ⌘⇧P to present, ⌘O to open

## Requirements

- macOS 14 (Sonoma) or later
- Xcode 15+ / Swift 5.9+ (to build)

## Build & Run

Build the double-clickable app bundle:

```bash
./build_app.sh
open SlideViewer.app
```

Or run directly with Swift Package Manager:

```bash
swift run PowerpointViewer
```

### Headless parse check

To inspect how a deck parses without launching the UI:

```bash
swift run PowerpointViewer --dump path/to/deck.pptx
```

## How it works

A `.pptx` file is a ZIP archive of XML parts. The pipeline:

1. **`Unzip`** — extracts the archive to a temporary directory using the system
   `unzip` (dependency-free).
2. **`PPTXParser`** — reads `ppt/presentation.xml` for the slide size and order,
   follows relationship (`.rels`) files to locate each slide, layout, master, and
   media part, then walks each slide's shape tree. All XML lookups are
   namespace-agnostic (matched by local name) so decks from any producer work.
3. **`SlideModels`** — a geometry model in points (OOXML's EMU units are converted
   at parse time).
4. **`SlideView`** / **`ContentView`** — SwiftUI renders each slide at its native
   point size and scales it uniformly to fit.

## Scope & limitations

This is a focused viewer covering the common shape vocabulary. Not yet handled:
gradient/picture fills, tables, charts, SmartArt, WordArt effects, animations, and
embedded video. Slides using these render their supported elements and skip the
rest. Contributions welcome.

## Project layout

```
Sources/PowerpointViewer/
  main.swift                 Entry point (GUI or --dump)
  PowerpointViewerApp.swift  App scene + menu commands
  ContentView.swift          Window layout, sidebar, present mode
  SlideView.swift            Slide + element rendering
  PresentationStore.swift    Open/navigation state
  PPTXParser.swift           OOXML → model
  SlideModels.swift          Geometry & content model
  Unzip.swift                Archive extraction
  XMLExtensions.swift        Namespace-agnostic XML helpers
  ColorExtensions.swift      Hex / theme color helpers
  DebugDump.swift            Headless diagnostic mode
```
