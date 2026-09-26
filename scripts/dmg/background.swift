// Draws the DMG window's background: a quiet paper gradient, the wordmark,
// an arrow from the app to Applications and one line of instruction.
//
//   swift scripts/dmg/background.swift scripts/dmg
//
// Writes background.png (1x) and background@2x.png; release.sh joins them into
// a HiDPI TIFF. Finder draws the two icons on top, at the positions in settings.py.

import AppKit

let size = CGSize(width: 660, height: 420)
let appCenter = CGPoint(x: 180, y: 215)       // y from the top, as in settings.py
let applicationsCenter = CGPoint(x: 480, y: 215)

func render(scale: CGFloat) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size.width * scale), pixelsHigh: Int(size.height * scale),
                               bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                               colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    rep.size = size
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let ctx = NSGraphicsContext.current!.cgContext
    // Flip so y runs from the top, like Finder's icon positions.
    ctx.translateBy(x: 0, y: size.height)
    ctx.scaleBy(x: 1, y: -1)

    // Paper: white at the top, a soft grey at the bottom.
    let gradient = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(),
                              colors: [NSColor(white: 1.0, alpha: 1).cgColor, NSColor(white: 0.925, alpha: 1).cgColor] as CFArray,
                              locations: [0, 1])!
    ctx.drawLinearGradient(gradient, start: .zero, end: CGPoint(x: 0, y: size.height), options: [])

    func text(_ string: String, font: NSFont, color: NSColor, centerX: CGFloat, top: CGFloat, kern: CGFloat = 0) {
        let attributed = NSAttributedString(string: string, attributes: [.font: font, .foregroundColor: color, .kern: kern])
        let bounds = attributed.size()
        NSGraphicsContext.saveGraphicsState()
        // Text draws unflipped: undo the flip locally.
        let flipped = NSGraphicsContext(cgContext: ctx, flipped: true)
        NSGraphicsContext.current = flipped
        attributed.draw(at: CGPoint(x: centerX - bounds.width / 2, y: top))
        NSGraphicsContext.restoreGraphicsState()
    }

    let serif = NSFont(descriptor: NSFont.systemFont(ofSize: 30).fontDescriptor.withDesign(.serif)!, size: 30)!
    text("Mindtalk", font: serif, color: NSColor(white: 0.07, alpha: 1), centerX: size.width / 2, top: 42)
    text("Talk. We'll type.", font: .systemFont(ofSize: 13, weight: .regular), color: NSColor(white: 0.36, alpha: 1),
         centerX: size.width / 2, top: 84)

    // The arrow between the two icons: a gentle curve, dashed, with a head.
    let start = CGPoint(x: appCenter.x + 78, y: appCenter.y - 4)
    let end = CGPoint(x: applicationsCenter.x - 78, y: applicationsCenter.y - 4)
    let arrow = CGMutablePath()
    arrow.move(to: start)
    arrow.addQuadCurve(to: end, control: CGPoint(x: size.width / 2, y: appCenter.y - 38))
    ctx.setStrokeColor(NSColor(white: 0.07, alpha: 0.55).cgColor)
    ctx.setLineWidth(2)
    ctx.setLineCap(.round)
    ctx.setLineDash(phase: 0, lengths: [0.1, 7])
    ctx.addPath(arrow)
    ctx.strokePath()
    ctx.setLineDash(phase: 0, lengths: [])
    let head = CGMutablePath()
    head.move(to: CGPoint(x: end.x - 10, y: end.y - 9))
    head.addLine(to: end)
    head.addLine(to: CGPoint(x: end.x - 12, y: end.y + 6))
    ctx.setLineWidth(2)
    ctx.setLineJoin(.round)
    ctx.addPath(head)
    ctx.strokePath()

    text("Drag Mindtalk to Applications", font: .systemFont(ofSize: 13, weight: .semibold),
         color: NSColor(white: 0.12, alpha: 1), centerX: size.width / 2, top: 318)
    text("Dra Mindtalk till Program", font: .systemFont(ofSize: 12, weight: .regular),
         color: NSColor(white: 0.42, alpha: 1), centerX: size.width / 2, top: 338)
    text("Apple Silicon · macOS 26", font: .systemFont(ofSize: 11, weight: .medium),
         color: NSColor(white: 0.55, alpha: 1), centerX: size.width / 2, top: 386, kern: 0.3)

    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

let out = URL(fileURLWithPath: CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : ".")
try render(scale: 1).write(to: out.appendingPathComponent("background.png"))
try render(scale: 2).write(to: out.appendingPathComponent("background@2x.png"))
print("✓ background.png, background@2x.png")
