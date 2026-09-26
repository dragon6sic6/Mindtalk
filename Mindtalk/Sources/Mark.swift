import AppKit
import SwiftUI

// MARK: - Mindtalk's mark
//
// Voice becomes text: a sound wave that settles into a straight line of text and
// ends in a text cursor. One geometry, used by the app icon's layers
// (scripts/make_icon_layers.swift draws the same shape), the living logo in the
// onboarding and menu panel, and the menu bar template image.
//
// Coordinates are in the icon's 1024 design space, y up, as laid out on the
// 824-pt tile of the classic macOS grid (centre 512, 512).

enum Mark {
    static let strokeWidth: CGFloat = 30

    /// The wave's centre line. `amplitude` scales the swell (1 = the icon);
    /// `phase` slides the wave along, for animation.
    static func wave(amplitude: CGFloat = 1, phase: CGFloat = 0) -> CGPath {
        let path = CGMutablePath()
        let x0: CGFloat = 230, xMid: CGFloat = 540, x1: CGFloat = 700, y: CGFloat = 512
        path.move(to: CGPoint(x: x0, y: y))
        let steps = 160
        for i in 1...steps {
            let t = CGFloat(i) / CGFloat(steps)
            // The swell rises and fades to nothing, so the wave flows into the line.
            let amp = 150 * amplitude * sin(t * .pi) * (1 - t * 0.35)
            path.addLine(to: CGPoint(x: x0 + (xMid - x0) * t, y: y + amp * sin(t * 4.2 * .pi + phase)))
        }
        path.addLine(to: CGPoint(x: x1, y: y))
        return path
    }

    /// The I-beam text cursor at the end of the line (filled shape).
    static let cursor: CGPath = {
        let p = CGMutablePath()
        let cx: CGFloat = 760, cy: CGFloat = 512, h: CGFloat = 300, stem: CGFloat = 28, serifW: CGFloat = 96
        func capsule(_ r: CGRect) -> CGPath {
            CGPath(roundedRect: r, cornerWidth: min(r.width, r.height) / 2, cornerHeight: min(r.width, r.height) / 2, transform: nil)
        }
        p.addPath(capsule(CGRect(x: cx - stem / 2, y: cy - h / 2, width: stem, height: h)))
        p.addPath(capsule(CGRect(x: cx - serifW / 2, y: cy + h / 2 - stem, width: serifW, height: stem)))
        p.addPath(capsule(CGRect(x: cx - serifW / 2, y: cy - h / 2, width: serifW, height: stem)))
        return p
    }()

    /// The mark's bounds in design space (wave stroke included).
    static let bounds = CGRect(x: 215, y: 362, width: 808 - 215, height: 300)

    /// Maps design space onto a tile of `size` points (y down), as macOS lays the
    /// icon's glyph over its full-bleed tile.
    static func tileTransform(size: CGFloat) -> CGAffineTransform {
        let k: CGFloat = 1024 / 824          // the glyph scales with the tile
        let s = size / 1024
        return CGAffineTransform(translationX: 0, y: size)
            .scaledBy(x: s, y: -s)
            .translatedBy(x: 512, y: 512).scaledBy(x: k, y: k).translatedBy(x: -512, y: -512)
    }

    /// The menu bar template: the mark fitted to the menu bar's height, with
    /// strokes and cursor drawn heavier so it reads as well as its neighbours.
    static let menuBarImage: NSImage = {
        let image = NSImage(size: NSSize(width: 24, height: 16), flipped: false) { rect in
            guard let ctx = NSGraphicsContext.current?.cgContext else { return false }
            let waveEnd: CGFloat = 700, b = bounds
            // The wave and line take the width up to the cursor.
            let scale: CGFloat = 17.5 / (waveEnd - b.minX)
            var t = CGAffineTransform(translationX: 1, y: rect.midY)
                .scaledBy(x: scale, y: scale * 1.25)
                .translatedBy(x: -b.minX, y: -512)
            ctx.setFillColor(NSColor.black.cgColor)
            ctx.setStrokeColor(NSColor.black.cgColor)
            if let w = wave().copy(using: &t) {
                ctx.addPath(w)
                ctx.setLineWidth(1.7)
                ctx.setLineCap(.round)
                ctx.setLineJoin(.round)
                ctx.strokePath()
            }
            // The cursor, sized for the menu bar.
            let cx: CGFloat = 1 + (760 - b.minX) * scale + 0.6, h: CGFloat = 13, stem: CGFloat = 1.7, serif: CGFloat = 5
            for r in [CGRect(x: cx - stem / 2, y: rect.midY - h / 2, width: stem, height: h),
                      CGRect(x: cx - serif / 2, y: rect.midY + h / 2 - stem, width: serif, height: stem),
                      CGRect(x: cx - serif / 2, y: rect.midY - h / 2, width: serif, height: stem)] {
                ctx.addPath(CGPath(roundedRect: r, cornerWidth: stem / 2, cornerHeight: stem / 2, transform: nil))
            }
            ctx.fillPath()
            return true
        }
        image.isTemplate = true
        image.accessibilityDescription = "Mindtalk"
        return image
    }()
}

/// The mark in SwiftUI, in the current foreground style — for the menu bar
/// preview in the onboarding and anywhere the glyph appears on its own.
struct MarkGlyph: View {
    var body: some View {
        Image(nsImage: Mark.menuBarImage)
            .renderingMode(.template)
            .accessibilityHidden(true)
    }
}
