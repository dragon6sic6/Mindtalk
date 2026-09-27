import SwiftUI

// MARK: - Floating tab bar
//
// Instead of a sidebar: a slim pill floating at the window's leading edge,
// icons only. Point at it and the whole pill widens to show the labels —
// then folds away again, leaving the page the full width of the window.

struct FloatingTabBar: View {
    @Binding var page: Page
    @ObservedObject var dictation: Dictation
    @State private var hovering = false
    @Namespace private var selection

    private var expanded: Bool {
        #if DEBUG
        if CommandLine.arguments.contains("--tabbar-expanded") { return true }   // screenshots
        #endif
        return hovering
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            ForEach(Page.allCases) { p in
                tab(p)
            }
            Rectangle().fill(DS.Colors.divider)
                .frame(width: (expanded ? 196 : 40) - 16, height: 1)
                .padding(.horizontal, 8).padding(.vertical, 6)
            status
        }
        .padding(8)
        .fixedSize()
        .background(RoundedRectangle(cornerRadius: 28, style: .continuous).fill(DS.Colors.card)
            .shadow(color: .black.opacity(0.12), radius: 16, y: 6))
        .overlay(RoundedRectangle(cornerRadius: 28, style: .continuous).strokeBorder(DS.Colors.fieldStroke, lineWidth: 1))
        .onHover { h in withAnimation(.spring(duration: 0.4, bounce: 0.12)) { hovering = h } }
    }

    private func tab(_ p: Page) -> some View {
        let selected = page == p
        return Button {
            guard page != p else { return }
            withAnimation(.spring(duration: 0.45, bounce: 0.15)) { page = p }
        } label: {
            HStack(spacing: 12) {
                Image(systemName: p.icon)
                    .font(.system(size: 16, weight: .medium))
                    .foregroundStyle(selected ? DS.Colors.onInk : .primary)
                    .frame(width: 40, height: 40)
                    .background {
                        if selected {
                            Circle().fill(DS.Colors.ink)
                                .matchedGeometryEffect(id: "tab", in: selection)
                        }
                    }
                if expanded {
                    Text(p.title)
                        .font(.system(size: 15, weight: selected ? .semibold : .regular))
                        .fixedSize()
                        .transition(.opacity.combined(with: .move(edge: .leading)))
                    Spacer(minLength: 8)
                    if p == .recent, dictation.recent.count > 0 {
                        Text("\(dictation.recent.count)")
                            .font(.system(size: 12)).monospacedDigit()
                            .foregroundStyle(DS.Colors.muted)
                            .transition(.opacity)
                    }
                }
            }
            .padding(.trailing, expanded ? 12 : 0)
            .frame(width: expanded ? 196 : 40, alignment: .leading)
            .contentShape(Capsule())
        }
        .buttonStyle(TabHoverStyle())
        .help(expanded ? "" : p.title)
        .accessibilityLabel(p.title)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }

    /// Ready or not, at the foot of the bar: a dot, and the words when open.
    private var status: some View {
        HStack(spacing: 12) {
            Circle().fill(dictation.needsSetup ? Color.orange : DS.Colors.good)
                .frame(width: 7, height: 7)
                .frame(width: 40, height: 22)
            if expanded {
                Text(statusText).font(.system(size: 12)).foregroundStyle(DS.Colors.muted)
                    .fixedSize()
                    .transition(.opacity)
                Spacer(minLength: 0)
            }
        }
        .frame(width: expanded ? 196 : 40, alignment: .leading)
        .help(expanded ? "" : statusText)
    }

    private var statusText: String {
        switch dictation.model {
        case .downloading(let p): return String(localized: "Laddar ned \(Int(p * 100)) %")
        case .loading: return String(localized: "Startar…")
        default: return dictation.isReady ? String(localized: "Redo") : String(localized: "Inte klar än")
        }
    }
}

/// A soft light under the row you point at.
private struct TabHoverStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        Row(configuration: configuration)
    }

    private struct Row: View {
        let configuration: ButtonStyle.Configuration
        @State private var hovering = false
        var body: some View {
            configuration.label
                .background(Capsule().fill(DS.Colors.chip.opacity(hovering ? 0.7 : 0)))
                .scaleEffect(configuration.isPressed ? 0.97 : 1)
                .animation(.spring(duration: 0.18), value: configuration.isPressed)
                .animation(.easeOut(duration: 0.15), value: hovering)
                .onHover { hovering = $0 }
        }
    }
}
