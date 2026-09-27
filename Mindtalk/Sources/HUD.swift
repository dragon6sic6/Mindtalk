import AppKit
import SwiftUI

// MARK: - Dictation pill
//
// A small black pill at the bottom of the screen while you dictate — just the
// waveform. Locked (hands-free), it grows a ✕ to cancel and a ✓ — our text
// cursor — to type it in, both clickable. When you finish, the wave settles
// into a line and ends in a blinking cursor: speech becoming text, as in the icon.

@MainActor
final class HUD {
    private var panel: NSPanel?
    /// The panel is a fixed stage; the pill animates inside it, centred at the bottom.
    private static let stage = NSSize(width: 260, height: 64)

    func show() {
        if panel == nil {
            let panel = NSPanel(contentRect: NSRect(origin: .zero, size: Self.stage),
                                styleMask: [.nonactivatingPanel, .borderless, .fullSizeContentView],
                                backing: .buffered, defer: false)
            panel.contentView = ClickThroughHostingView(rootView: HUDView(dictation: .shared))
            panel.isReleasedWhenClosed = false
            panel.isOpaque = false
            panel.backgroundColor = .clear
            panel.hasShadow = false
            panel.level = .statusBar
            panel.hidesOnDeactivate = false
            panel.becomesKeyOnlyIfNeeded = true
            panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
            self.panel = panel
        }
        guard let panel else { return }
        // Clickable only while locked, when it has buttons; otherwise it never gets in the way.
        let dictation = Dictation.shared
        panel.ignoresMouseEvents = !(dictation.handsFree && dictation.phase == .recording)
        place(panel)
        panel.orderFrontRegardless()
    }

    func hide() {
        panel?.orderOut(nil)
    }

    /// Bottom centre of the screen you're on: just above the Dock, or near the
    /// bottom edge when the Dock is hidden or on the side.
    private func place(_ panel: NSPanel) {
        guard let screen = NSScreen.main else { return }
        let full = screen.frame, visible = screen.visibleFrame
        let dockBelow = visible.minY > full.minY + 1
        let pillBottom = dockBelow ? visible.minY + 10 : full.minY + 14
        let origin = NSPoint(x: full.midX - Self.stage.width / 2, y: pillBottom - HUDView.stagePadding)
        panel.setFrame(NSRect(origin: origin, size: Self.stage), display: true)
    }
}

/// Buttons answer the first click, even though the panel never becomes active —
/// so the app you're typing in keeps focus and receives the text.
private final class ClickThroughHostingView<Content: View>: NSHostingView<Content> {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

private struct HUDView: View {
    @ObservedObject var dictation: Dictation
    /// Space under the pill inside the stage, for its shadow.
    static let stagePadding: CGFloat = 10

    private static let ink = Color(white: 0.07)

    var body: some View {
        VStack {
            Spacer(minLength: 0)
            pill
                .padding(.bottom, Self.stagePadding)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var pill: some View {
        HStack(spacing: 10) {
            switch dictation.phase {
            case .recording:
                if dictation.handsFree {
                    RoundButton(help: "Avbryt (esc)", filled: false) { dictation.cancelFromHUD() } label: {
                        Image(systemName: "xmark").font(.system(size: 10, weight: .bold))
                    }
                    .transition(.scale(scale: 0.4).combined(with: .opacity))
                }
                Waveform(levels: Array(dictation.levels.suffix(14)), gap: 2.5, color: .white)
                    .frame(width: 52, height: 16)
                if dictation.showsLanguage {
                    ModelBadge(model: dictation.engine, size: 10)
                        .foregroundStyle(.white.opacity(0.85))
                        .transition(.opacity.combined(with: .scale(scale: 0.6)))
                }
                if dictation.handsFree {
                    RoundButton(help: "Skriv in", filled: true) { dictation.finishFromHUD() } label: {
                        CursorGlyph().frame(width: 8, height: 13)
                    }
                    .transition(.scale(scale: 0.4).combined(with: .opacity))
                }
            case .transcribing:
                SettlingLine()
                    .frame(width: 60, height: 16)
            case .failed(let message):
                Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                Text(message)
                    .font(.system(size: 12, weight: .medium))
                    .foregroundStyle(.white)
                    .lineLimit(2)
                    .frame(maxWidth: 220, alignment: .leading)
            case .idle:
                EmptyView()
            }
        }
        .padding(.horizontal, dictation.handsFree && dictation.phase == .recording ? 5 : 14)
        .frame(height: 32)
        .fixedSize()
        .background(Capsule().fill(Self.ink))
        .overlay(Capsule().strokeBorder(.white.opacity(0.14), lineWidth: 0.5))
        .shadow(color: .black.opacity(0.28), radius: 6, y: 2)
        .animation(.spring(duration: 0.32, bounce: 0.18), value: dictation.handsFree)
        .animation(.spring(duration: 0.32, bounce: 0.1), value: dictation.showsLanguage)
        .animation(.easeOut(duration: 0.25), value: dictation.phase)
    }
}

/// A 24-pt round button in the pill: ✕ on smoked glass, ✓ as a white disc.
private struct RoundButton<Label: View>: View {
    let help: String
    let filled: Bool
    let action: () -> Void
    @ViewBuilder let label: Label
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            label
                .foregroundStyle(filled ? Color.black : .white)
                .frame(width: 24, height: 24)
                .background(Circle().fill(filled ? Color.white.opacity(hovering ? 1 : 0.92)
                                                 : Color.white.opacity(hovering ? 0.28 : 0.18)))
                .scaleEffect(hovering ? 1.06 : 1)
                .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .animation(.easeOut(duration: 0.12), value: hovering)
        .help(LocalizedStringKey(help))
        .accessibilityLabel(LocalizedStringKey(help))
    }
}

/// Mindtalk's text cursor — the I-beam from the icon.
private struct CursorGlyph: View {
    var body: some View {
        Canvas { ctx, size in
            let stem: CGFloat = 1.8, w = size.width, h = size.height
            var p = Path()
            p.addRoundedRect(in: CGRect(x: (w - stem) / 2, y: 0, width: stem, height: h), cornerSize: CGSize(width: stem / 2, height: stem / 2))
            p.addRoundedRect(in: CGRect(x: 0, y: 0, width: w, height: stem), cornerSize: CGSize(width: stem / 2, height: stem / 2))
            p.addRoundedRect(in: CGRect(x: 0, y: h - stem, width: w, height: stem), cornerSize: CGSize(width: stem / 2, height: stem / 2))
            ctx.fill(p, with: .foreground)
        }
    }
}

/// While the text is typed: the wave has settled into a line of text that ends
/// in a blinking cursor — the icon, in motion.
private struct SettlingLine: View {
    @State private var drawn = false

    var body: some View {
        TimelineView(.periodic(from: .now, by: 0.45)) { context in
            let on = Int(context.date.timeIntervalSinceReferenceDate / 0.45) % 2 == 0
            HStack(spacing: 4) {
                Capsule()
                    .fill(.white.opacity(0.75))
                    .frame(width: drawn ? 44 : 8, height: 2)
                CursorGlyph()
                    .foregroundStyle(.white)
                    .frame(width: 7, height: 14)
                    .opacity(on ? 1 : 0.25)
            }
        }
        .onAppear { withAnimation(.easeOut(duration: 0.35)) { drawn = true } }
        .accessibilityLabel(Text("Skriver…"))
    }
}
