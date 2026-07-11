import Foundation

/// Prints a structural summary of a parsed presentation. Diagnostic only.
enum DebugDump {
    static func run(path: String) {
        let url = URL(fileURLWithPath: path)
        do {
            let root = try Unzip.extract(url)
            defer { try? FileManager.default.removeItem(at: root) }
            let pres = try PPTXParser(extractedRoot: root).parse()
            print("Presentation: \(url.lastPathComponent)")
            print("Canvas: \(Int(pres.size.width)) x \(Int(pres.size.height)) pt")
            print("Slides: \(pres.slides.count)")
            for slide in pres.slides {
                let bgDesc: String
                switch slide.background {
                case .color: bgDesc = "color"
                case .image(let u): bgDesc = "image(\(u.lastPathComponent))"
                case nil: bgDesc = "none"
                }
                print("\n── Slide \(slide.index + 1) (bg: \(bgDesc), \(slide.elements.count) elements)")
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
                    }
                }
            }
        } catch {
            FileHandle.standardError.write(Data("Error: \(error)\n".utf8))
            exit(1)
        }
    }
}
