# SlideViewer for Windows

A Windows port of SlideViewer — **Viewer for PowerPoint & PPTX**. Same custom
OOXML parser and renderer as the macOS app, rebuilt in C# on
[Avalonia UI](https://avaloniaui.net). No Office, no web view, no runtime to
install.

## Getting the installer

Build it (see below), then copy `dist/SlideViewer-Setup.exe` to the Windows
machine and double-click it. That's the whole process — the installer carries
the application *and* the .NET runtime inside itself, so nothing is downloaded
and there are no prerequisites.

The installer:

- installs per-user to `%LOCALAPPDATA%\Programs\SlideViewer` (**no admin rights
  needed**)
- creates Desktop and Start Menu shortcuts
- adds SlideViewer to the "Open with" menu for `.pptx` (without hijacking the
  existing default association)
- registers in **Settings → Apps** so it uninstalls like any normal program

Requires 64-bit Windows 10 or later.

## Building

Only the .NET SDK is needed; the build runs on macOS, Linux, or Windows:

```bash
./build_installer.sh          # → dist/SlideViewer-Setup.exe
```

Steps performed: publish self-contained `win-x64` → zip the payload → embed it
in the installer executable → verify the output is a Windows PE binary.

## Verifying rendering

The app can rasterize a slide offscreen, which is how the port was checked
against the macOS renderer:

```bash
dotnet run --project SlideViewer -- --render <deck.pptx> <slide-index> out.png
```

## Features

Text with fonts, sizes, bold/italic/underline/strike, colors, alignment,
bullets (including Wingdings→Unicode mapping and auto-numbered lists), hanging
indents, line spacing and shrink-to-fit; pictures with `srcRect` crops; tables;
autoshapes with solid/gradient/picture fills; group shapes and connectors;
placeholder, background and shape inheritance from layouts and masters; theme
colors and fonts; click-triggered animation builds (including
paragraph-by-paragraph); thumbnail navigator; and presentation mode on a second
display with a presenter console (current/next slide, build counter, timer).

## Differences from the macOS build

- **No camera panel** in the presenter console (macOS-only feature for now).
- **Legacy `.ppt`** files are not converted; save them as `.pptx` first.
- Extraction reads directly from the archive, so the macOS-only stored-permission
  workaround is unnecessary here.

## Layout

```
SlideViewer/
  Models/SlideModels.cs        geometry + content model (points)
  Parsing/Package.cs           ZIP part access, relationships
  Parsing/XmlExtensions.cs     namespace-agnostic traversal (never XPath)
  Parsing/PptxParser.cs        OOXML → model
  Parsing/SymbolFont.cs        Wingdings/Symbol → Unicode bullets
  Rendering/SlideRenderer.cs   model → Avalonia visual tree
  Rendering/ShapeGeometryFactory.cs, FontResolver.cs
  Views/                       MainWindow, PresentationWindow, state, RenderTool
Installer/                     self-contained installer that embeds the app
build_installer.sh             one-command build
```
