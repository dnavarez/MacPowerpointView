import Foundation

/// Prints a structural summary of a parsed presentation. Diagnostic only.
enum DebugDump {
    static func run(path: String) {
        let url = URL(fileURLWithPath: path)
        do {
            let source = try LegacyPPT.resolve(url)
            let root = try Unzip.extract(source)
            defer { try? FileManager.default.removeItem(at: root) }
            let pres = try PPTXParser(extractedRoot: root).parse()
            print("Presentation: \(url.lastPathComponent)")
            print("Canvas: \(Int(pres.size.width)) x \(Int(pres.size.height)) pt")
            print("Slides: \(pres.slides.count)")
            for slide in pres.slides {
                let bgDesc: String
                switch slide.background {
                case .color(let c): bgDesc = "color(\(c))"
                case .gradient(let stops, let angle): bgDesc = "gradient(\(stops.count) stops, \(Int(angle))°)"
                case .image(let u): bgDesc = "image(\(u.lastPathComponent))"
                case nil: bgDesc = "none"
                }
                var extras = ""
                if !slide.buildSteps.isEmpty {
                    let detail = slide.buildSteps.map { step -> String in
                        var parts = step.reveals.sorted()
                        parts += step.paragraphReveals.sorted(by: { $0.key < $1.key })
                            .map { "\($0.key)¶\($0.value.sorted().map(String.init).joined(separator: ","))" }
                        if !step.hides.isEmpty { parts.append("-" + step.hides.sorted().joined(separator: ",")) }
                        return "[\(parts.joined(separator: " "))]"
                    }.joined()
                    extras += ", \(slide.buildSteps.count) builds \(detail)"
                }
                if slide.hasTransition { extras += ", transition" }
                print("\n── Slide \(slide.index + 1) (bg: \(bgDesc), \(slide.elements.count) elements\(extras))")
                for el in slide.elements {
                    let f = el.frame
                    let box = "[\(Int(f.minX)),\(Int(f.minY)) \(Int(f.width))x\(Int(f.height))]"
                    switch el {
                    case .text(let t):
                        let preview = t.paragraphs.flatMap { $0.runs.map(\.text) }
                            .joined().prefix(50)
                        var style = ""
                        if let r = t.paragraphs.first?.runs.first {
                            style = " [\(Int(r.fontSize))pt\(r.bold ? " bold" : "")\(r.fontName.map { " \($0)" } ?? "")\(r.shadow ? " shdw" : "")]"
                        }
                        print("   text  \(box)\(style) \"\(preview)\"")
                    case .shape(let s):
                        let txt = s.text?.paragraphs.flatMap { $0.runs.map(\.text) }.joined() ?? ""
                        print("   shape \(box) geom=\(s.geometry) fill=\(s.fill != nil) \"\(txt.prefix(30))\"")
                    case .image(let p):
                        print("   image \(box) \(p.imageURL.lastPathComponent)")
                    case .table(let t):
                        let preview = t.rows.first?.cells.first?.paragraphs
                            .flatMap { $0.runs.map(\.text) }.joined().prefix(40) ?? ""
                        print("   table \(box) \(t.columnWidths.count)col x \(t.rows.count)row \"\(preview)\"")
                    }
                }
            }
        } catch {
            FileHandle.standardError.write(Data("Error: \(error)\n".utf8))
            exit(1)
        }
    }
}
