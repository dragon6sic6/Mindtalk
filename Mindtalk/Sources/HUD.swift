import AppKit
import SwiftUI

/// Small Liquid Glass pill at the bottom of the screen while dictating.
@MainActor
final class HUD {
    private var panel: NSPanel?

    func show() {
        if panel == nil {
            let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 240, height: 56),
                                styleMask: [.nonactivatingPanel, .borderless, .fullSizeContentView],
                                backing: .buffered, defer: false)
            panel.contentView = NSHostingView(rootView: HUDView(dictation: .shared))
            panel.isReleasedWhenClosed = false
            panel.isOpaque = false
            panel.backgroundColor = .clear
            panel.hasShadow = false
            panel.level = .statusBar
            panel.ignoresMouseEvents = true
            panel.hidesOnDeactivate = false
            panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
            self.panel = panel
        }
        guard let panel else { return }
        place(panel)
        panel.orderFrontRegardless()
        // The content settles after this runloop pass; refit then.
        DispatchQueue.main.async { [weak self] in
            guard let self, let panel = self.panel, panel.isVisible else { return }
            self.place(panel)
        }
    }

    func hide() {
        panel?.orderOut(nil)
    }

    /// Fit the content and keep it bottom-centre of the screen the user is on.
    private func place(_ panel: NSPanel) {
        guard let hosting = panel.contentView as? NSHostingView<HUDView>, let screen = NSScreen.main else { return }
        let size = hosting.fittingSize
        let f = screen.visibleFrame
        panel.setFrame(NSRect(x: f.midX - size.width / 2, y: f.minY + 56, width: size.width, height: size.height),
                       display: true)
    }
}

private struct HUDView: View {
    @ObservedObject var dictation: Dictation

    var body: some View {
        HStack(spacing: 10) {
            switch dictation.phase {
            case .recording:
                ZStack {
                    Circle().fill(DS.Colors.accent.opacity(0.18)).frame(width: 22, height: 22)
                        .scaleEffect(1 + CGFloat(min(1, dictation.level.squareRoot())) * 0.35)
                        .animation(.easeOut(duration: 0.1), value: dictation.level)
                    Image(systemName: dictation.handsFree ? "lock.fill" : "mic.fill")
                        .font(.system(size: 11, weight: .semibold))
                        .foregroundStyle(DS.Colors.accent)
                        .contentTransition(.symbolEffect(.replace))
                }
                Waveform(levels: dictation.levels, gap: 2.5)
                    .frame(width: 96, height: 22)
                if let start = dictation.recordingStarted {
                    TimelineView(.periodic(from: start, by: 1)) { context in
                        Text(Self.elapsed(from: start, to: context.date))
                            .font(.system(size: 12, weight: .semibold))
                            .monospacedDigit()
                            .foregroundStyle(.secondary)
                    }
                }
                if dictation.installed.count > 1 {
                    ModelBadge(model: dictation.engine, size: 10)
                        .foregroundStyle(DS.Colors.accent)
                        .padding(.horizontal, 5).padding(.vertical, 1.5)
                        .background(Capsule().fill(DS.Colors.accent.opacity(0.12)))
                }
                if dictation.handsFree {
                    HStack(spacing: 5) {
                        Text("Tryck").foregroundStyle(.secondary)
                        KeyChip(text: dictation.hotkey.chip)
                        Text("för att klistra in").foregroundStyle(.secondary)
                    }
                    .transition(.opacity.combined(with: .move(edge: .trailing)))
                }
            case .transcribing:
                ProgressView().controlSize(.small).tint(DS.Colors.accent)
                Waveform(levels: dictation.levels, gap: 2.5, dimmed: true)
                    .frame(width: 96, height: 22)
                Text("Skriver …").foregroundStyle(.secondary)
            case .failed(let message):
                Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                Text(message).lineLimit(2).frame(maxWidth: 320, alignment: .leading)
            case .idle:
                EmptyView()
            }
        }
        .font(.system(size: 12.5, weight: .medium))
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .fixedSize()
        // A near-solid layer under the glass keeps the text readable over busy windows.
        .background(Capsule().fill(DS.Colors.paper.opacity(0.8)))
        .glassEffect(.regular, in: .capsule)
        .padding(8)   // room for the glass shadow inside the panel
        .animation(.snappy(duration: 0.2), value: dictation.handsFree)
    }

    private static func elapsed(from start: Date, to now: Date) -> String {
        let s = max(0, Int(now.timeIntervalSince(start)))
        return String(format: "%d:%02d", s / 60, s % 60)
    }
}
