# SlideViewer (MacPowerpointView)

Native macOS `.pptx` viewer: SwiftPM app, SwiftUI rendering, custom OOXML
parser. No Office, no web view, no third-party deps.

## Build & run

```bash
swift build                                # library/CLI build
./build_app.sh && open SlideViewer.app     # double-clickable bundle
.build/debug/PowerpointViewer --dump <deck.pptx>            # parse diagnostics
.build/debug/PowerpointViewer --render <deck.pptx> <i> out.png  # rasterize slide i
```

## MANDATORY before claiming any rendering change is done

```bash
swift test            # parser regression tests (fixture + real decks)
./verify_renders.sh   # golden-image comparison of previously-approved slides
```

Both must pass. If a golden fails because the change is an *intended*
improvement, visually compare old vs new (`open .goldens/<name>.png
.goldens/<name>.FAILED.png`), get the user's approval, then re-record with
`./verify_renders.sh --record`. Never re-record to silence a failure.

When you fix a new rendering bug: add a test in
`Tests/PowerpointViewerTests/ParserRegressionTests.swift` (and a golden case in
`verify_renders.sh` if it's visual), extend `Tools/make_fixture.py` if the
fixture needs the scenario. A fix without a test will regress.

Commit at every user-approved milestone — an uncommitted good state cannot be
bisected against.

## Danger zones (each of these shipped broken once)

- **Never use `nodes(forXPath:)`** — Foundation XPath escapes the context node
  on some documents. Use the manual walkers in `XMLExtensions.swift`.
- **Never use `.primary`/appearance-dependent colors for slide content** —
  dark mode turned default text white-on-white. Slide content defaults to
  black (`TextRun.color`).
- **Paragraph alignment**: text frames must use the paragraph's parsed
  alignment; a hardcoded `.leading` once broke centered hymn lyrics
  (`SlideView.swift`, `paragraphView`).
- **Whitespace-only `<a:t>` runs**: libxml2 drops them even with
  `.nodePreserveWhitespace`; `loadSlideDocument` swaps them for a sentinel.
  Any new text-extraction path must go through it.
- **Extracted archives may have non-traversable dirs** (missing execute bit);
  `Unzip.normalizePermissions` fixes them — keep it after extraction.
- **`ImageRenderer` (`--render`) runs light-mode and no preference loop**:
  shrink-to-fit doesn't apply offscreen, and dark-mode bugs are invisible
  there. UI-level checks still need the live app.

## Architecture crib

- `PPTXParser.swift` — OOXML → model. Placeholder/background/shape inheritance
  from layout & master (cached); theme colors + fonts; lstStyle/defRPr text
  defaults; tables; groups (child-space transform); animation timing →
  `BuildStep`s (incl. paragraph `pRg`); `srcRect` crops.
- `SlideModels.swift` — pure model, points (EMU ÷ 12700).
- `SlideView.swift` — scale-aware rendering (geometry × scale, no
  `scaleEffect` for layout); shrink-to-fit via height preference;
  bullets in hanging indent; `FillPainter` for gradients.
- `PresentationStore.swift` — nav + build stepping; `PresentationWindow.swift`
  — external-display show window + key monitor; `ContentView.swift` — browser
  UI + presenter console (camera via `CameraManager`).
- `FontResolver`/`SymbolFont` — Windows-font substitution, Wingdings→Unicode.
- `LegacyPPT` — OLE `.ppt` detection + LibreOffice conversion.
