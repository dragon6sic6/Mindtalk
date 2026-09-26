// The README's hero: the app window, the menu bar panel and the dictation pill
// together on a quiet backdrop — once for light mode, once for dark.
//
//   swift scripts/screenshots/hero.swift docs/images/en
//
// Reads dictation-<mode>.png, panel-<mode>.png and hud-<mode>.png (window
// captures with their shadows, 2x) and writes hero-<mode>.png.

import AppKit

let dir = URL(fileURLWithPath: CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : ".")

func load(_ name: String) -> CGImage? {
    guard let image = NSImage(contentsOf: dir.appendingPathComponent(name)) else { return nil }
    return image.cgImage(forProposedRect: nil, context: nil, hints: nil)
}

for mode in ["light", "dark"] {
    guard let window = load("dictation-\(mode).png"), let panel = load("panel-\(mode).png"), let hud = load("hud-\(mode).png") else {
        print("✗ hero-\(mode): missing captures"); continue
    }
    let dark = mode == "dark"
    // Canvas in pixels (captures are 2x): room around the window, the panel overhanging to the right.
    let width = CGFloat(window.width) + 560, height = CGFloat(window.height) + 250
    let ctx = CGContext(data: nil, width: Int(width), height: Int(height), bitsPerComponent: 8, bytesPerRow: 0,
                        space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!

    // Backdrop: a soft vertical wash with a faint glow behind the window.
    let top = dark ? CGColor(gray: 0.10, alpha: 1) : CGColor(gray: 0.985, alpha: 1)
    let bottom = dark ? CGColor(gray: 0.03, alpha: 1) : CGColor(gray: 0.90, alpha: 1)
    let wash = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB)!, colors: [top, bottom] as CFArray, locations: [0, 1])!
    ctx.drawLinearGradient(wash, start: CGPoint(x: 0, y: height), end: .zero, options: [])
    let glow = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB)!,
                          colors: [CGColor(gray: dark ? 1 : 1, alpha: dark ? 0.07 : 0.7), CGColor(gray: 1, alpha: 0)] as CFArray,
                          locations: [0, 1])!
    ctx.drawRadialGradient(glow, startCenter: CGPoint(x: width * 0.42, y: height * 0.55), startRadius: 0,
                           endCenter: CGPoint(x: width * 0.42, y: height * 0.55), endRadius: width * 0.55, options: [])

    // Window left of centre, panel overlapping its top-right corner, pill below.
    let windowOrigin = CGPoint(x: 120, y: 150)
    ctx.draw(window, in: CGRect(origin: windowOrigin, size: CGSize(width: window.width, height: window.height)))
    let panelOrigin = CGPoint(x: width - CGFloat(panel.width) - 70, y: height - CGFloat(panel.height) - 40)
    ctx.draw(panel, in: CGRect(origin: panelOrigin, size: CGSize(width: panel.width, height: panel.height)))
    // The pill, a little larger so it reads at README size, straddling the window's bottom edge.
    let hudSize = CGSize(width: CGFloat(hud.width) * 1.5, height: CGFloat(hud.height) * 1.5)
    let hudOrigin = CGPoint(x: windowOrigin.x + (CGFloat(window.width) - hudSize.width) / 2, y: windowOrigin.y - hudSize.height * 0.45)
    ctx.interpolationQuality = .high
    ctx.draw(hud, in: CGRect(origin: hudOrigin, size: hudSize))

    let rep = NSBitmapImageRep(cgImage: ctx.makeImage()!)
    let out = dir.appendingPathComponent("hero-\(mode).png")
    try rep.representation(using: .png, properties: [:])!.write(to: out)
    print("✓ hero-\(mode)")
}
