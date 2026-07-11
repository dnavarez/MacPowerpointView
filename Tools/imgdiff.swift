import Foundation
import CoreGraphics
import ImageIO

// Pixel-diff two PNGs: exits 0 when visually identical (≤0.1% of pixels
// differ by more than 8/255 per channel), 1 when different, 2 on usage error.
// Usage: swift Tools/imgdiff.swift a.png b.png

func load(_ path: String) -> CGImage? {
    guard let src = CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, nil) else { return nil }
    return CGImageSourceCreateImageAtIndex(src, 0, nil)
}

func rgba(_ image: CGImage) -> [UInt8]? {
    let w = image.width, h = image.height
    var data = [UInt8](repeating: 0, count: w * h * 4)
    guard let ctx = CGContext(data: &data, width: w, height: h, bitsPerComponent: 8,
                              bytesPerRow: w * 4, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                              bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
    ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
    return data
}

guard CommandLine.arguments.count == 3,
      let a = load(CommandLine.arguments[1]),
      let b = load(CommandLine.arguments[2]) else {
    FileHandle.standardError.write(Data("usage: imgdiff a.png b.png\n".utf8))
    exit(2)
}
guard a.width == b.width, a.height == b.height else {
    print("DIFFER: size \(a.width)x\(a.height) vs \(b.width)x\(b.height)")
    exit(1)
}
guard let pa = rgba(a), let pb = rgba(b) else { exit(2) }

var badPixels = 0
let pixelCount = a.width * a.height
for i in stride(from: 0, to: pa.count, by: 4) {
    for c in 0..<3 where abs(Int(pa[i + c]) - Int(pb[i + c])) > 8 {
        badPixels += 1
        break
    }
}
let fraction = Double(badPixels) / Double(pixelCount)
if fraction > 0.001 {
    print(String(format: "DIFFER: %.3f%% of pixels changed (%d px)", fraction * 100, badPixels))
    exit(1)
}
print(String(format: "OK (%.4f%% changed)", fraction * 100))
