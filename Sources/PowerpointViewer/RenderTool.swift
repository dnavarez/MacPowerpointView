import SwiftUI
import AppKit

/// Offscreen slide rendering for diagnostics: `--render <file> <index> <out.png>`.
/// Rasterizes a single slide via `ImageRenderer` so rendering can be inspected
/// without launching the GUI. (Note: `onPreferenceChange`-driven shrink-to-fit
/// needs a live render loop, so exported images show the pre-shrink layout.)
@MainActor
enum RenderTool {
    static func run(path: String, index: Int, outPath: String) {
        do {
            let root = try Unzip.extract(try LegacyPPT.resolve(URL(fileURLWithPath: path)))
            defer { try? FileManager.default.removeItem(at: root) }
            let pres = try PPTXParser(extractedRoot: root).parse()
            guard pres.slides.indices.contains(index) else {
                FileHandle.standardError.write(Data("Slide \(index) out of range (0..<\(pres.slides.count))\n".utf8))
                exit(1)
            }
            let width: CGFloat = 1280
            let height = width * pres.size.height / pres.size.width
            let view = SlideView(slide: pres.slides[index], slideSize: pres.size, showShadow: false)
                .frame(width: width, height: height)
                .background(Color.white)

            let renderer = ImageRenderer(content: view)
            renderer.scale = 2
            guard let nsImage = renderer.nsImage,
                  let tiff = nsImage.tiffRepresentation,
                  let rep = NSBitmapImageRep(data: tiff),
                  let png = rep.representation(using: .png, properties: [:]) else {
                FileHandle.standardError.write(Data("Render failed\n".utf8))
                exit(1)
            }
            try png.write(to: URL(fileURLWithPath: outPath))
            print("Rendered slide \(index + 1) → \(outPath)")
        } catch {
            FileHandle.standardError.write(Data("Error: \(error)\n".utf8))
            exit(1)
        }
    }
}
