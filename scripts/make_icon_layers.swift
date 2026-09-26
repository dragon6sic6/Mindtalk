// Renders the layers of Mindtalk's Icon Composer icon (Mindtalk/Resources/AppIcon.icon):
// the wave-into-a-line and the text cursor, flat white on a transparent 1024 canvas.
// macOS adds the black tile, glass and shadows itself. Same geometry as Sources/Mark.swift.
//
//   swiftc -O scripts/make_icon_layers.swift -o /tmp/mklayers && /tmp/mklayers Mindtalk/Resources/AppIcon.icon/Assets
import AppKit

let size: CGFloat = 1024
let cs = CGColorSpace(name: CGColorSpace.displayP3)!
let out = URL(fileURLWithPath: CommandLine.arguments[1])

// Design space is the 824-pt tile of the classic grid; the layer canvas is the full tile.
let k: CGFloat = 1024 / 824
var toCanvas = CGAffineTransform(translationX: 512, y: 512).scaledBy(x: k, y: k).translatedBy(x: -512, y: -512)

func wave() -> CGPath {
    let path = CGMutablePath()
    let x0: CGFloat = 230, xMid: CGFloat = 540, x1: CGFloat = 700, y: CGFloat = 512
    path.move(to: CGPoint(x: x0, y: y))
    for i in 1...160 {
        let t = CGFloat(i) / 160
        let amp = 150 * sin(t * .pi) * (1 - t * 0.35)
        path.addLine(to: CGPoint(x: x0 + (xMid - x0) * t, y: y + amp * sin(t * 4.2 * .pi)))
    }
    path.addLine(to: CGPoint(x: x1, y: y))
    return path.copy(strokingWithWidth: 30, lineCap: .round, lineJoin: .round, miterLimit: 2)
}

func capsule(_ r: CGRect) -> CGPath {
    CGPath(roundedRect: r, cornerWidth: min(r.width, r.height) / 2, cornerHeight: min(r.width, r.height) / 2, transform: nil)
}

func cursor() -> CGPath {
    let p = CGMutablePath()
    let cx: CGFloat = 760, cy: CGFloat = 512, h: CGFloat = 300, stem: CGFloat = 28, serifW: CGFloat = 96
    p.addPath(capsule(CGRect(x: cx - stem / 2, y: cy - h / 2, width: stem, height: h)))
    p.addPath(capsule(CGRect(x: cx - serifW / 2, y: cy + h / 2 - stem, width: serifW, height: stem)))
    p.addPath(capsule(CGRect(x: cx - serifW / 2, y: cy - h / 2, width: serifW, height: stem)))
    return p
}

func render(_ name: String, _ path: CGPath) {
    let ctx = CGContext(data: nil, width: Int(size), height: Int(size), bitsPerComponent: 8, bytesPerRow: 0,
                        space: cs, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    ctx.addPath(path.copy(using: &toCanvas)!)
    ctx.setFillColor(CGColor(colorSpace: cs, components: [1, 1, 1, 1])!)
    ctx.fillPath()
    let rep = NSBitmapImageRep(cgImage: ctx.makeImage()!)
    try! rep.representation(using: .png, properties: [:])!.write(to: out.appendingPathComponent(name))
}

render("wave.png", wave())
render("cursor.png", cursor())
