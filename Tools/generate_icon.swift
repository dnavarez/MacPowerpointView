import AppKit

// Renders a 1024×1024 app-icon PNG: a rounded "squircle" with an orange-red
// gradient (PowerPoint's family), a white slide card, and a play triangle to
// signal a *viewer*. Output: AppIcon-1024.png next to this script's argument.

let size = 1024.0
let rep = NSBitmapImageRep(
    bitmapDataPlanes: nil, pixelsWide: Int(size), pixelsHigh: Int(size),
    bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
    colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!

NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
let ctx = NSGraphicsContext.current!.cgContext

func color(_ hex: UInt32) -> NSColor {
    NSColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255,
            green: CGFloat((hex >> 8) & 0xFF) / 255,
            blue: CGFloat(hex & 0xFF) / 255, alpha: 1)
}

// --- Squircle background with vertical gradient ---
let inset: CGFloat = 100
let bgRect = CGRect(x: inset, y: inset, width: size - inset * 2, height: size - inset * 2)
let bgPath = NSBezierPath(roundedRect: bgRect, xRadius: 200, yRadius: 200)
ctx.saveGState()
bgPath.addClip()
let grad = NSGradient(colors: [color(0xF0653F), color(0xC43E1C)])!
grad.draw(in: bgRect, angle: -90)
ctx.restoreGState()

// Soft diagonal sheen (no hard edges).
ctx.saveGState()
bgPath.addClip()
let sheen = NSGradient(colors: [
    color(0xFFFFFF).withAlphaComponent(0.14),
    color(0xFFFFFF).withAlphaComponent(0.0)
])!
sheen.draw(in: bgRect, angle: -90)
ctx.restoreGState()

// --- White slide card ---
let cardRect = CGRect(x: 250, y: 330, width: 524, height: 364)
let cardPath = NSBezierPath(roundedRect: cardRect, xRadius: 34, yRadius: 34)
ctx.saveGState()
ctx.setShadow(offset: .init(width: 0, height: -14), blur: 40,
              color: color(0x000000).withAlphaComponent(0.25).cgColor)
color(0xFFFFFF).setFill()
cardPath.fill()
ctx.restoreGState()

// --- Play triangle (viewer motif) ---
let cx: CGFloat = 512, cy: CGFloat = 512
let tri = NSBezierPath()
tri.move(to: CGPoint(x: cx - 58, y: cy + 82))
tri.line(to: CGPoint(x: cx - 58, y: cy - 82))
tri.line(to: CGPoint(x: cx + 96, y: cy))
tri.close()
ctx.saveGState()
tri.addClip()
NSGradient(colors: [color(0xF0653F), color(0xC43E1C)])!
    .draw(in: tri.bounds, angle: -90)
ctx.restoreGState()

NSGraphicsContext.restoreGraphicsState()

let outPath = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "AppIcon-1024.png"
let data = rep.representation(using: .png, properties: [:])!
try! data.write(to: URL(fileURLWithPath: outPath))
print("wrote \(outPath)")
