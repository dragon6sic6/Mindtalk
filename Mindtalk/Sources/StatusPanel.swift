import AppKit
import SwiftUI

// MARK: - Menu bar panel
//
// Clicking Mindtalk in the menu bar (left or right) opens this panel. It is
// built like a menu bar menu — rows with a quiet hover, round badges filled
// when chosen, keyboard shortcuts at the trailing edge — but dressed like the
// app: its logo, its keycaps, its serif numbers, and room to breathe.

@MainActor
final class StatusPanel: NSObject, NSWindowDelegate {
    private var panel: KeyPanel?
    private var outsideClick: Any?
    private var insideClick: Any?
    private weak var button: NSStatusBarButton?

    var isShown: Bool { panel?.isVisible == true }

    func toggle(from button: NSStatusBarButton) {
        isShown ? close() : show(from: button)
    }

    func show(from button: NSStatusBarButton) {
        self.button = button
        if panel == nil {
            let panel = KeyPanel(contentRect: NSRect(x: 0, y: 0, width: PanelMetrics.width, height: 400),
                                 styleMask: [.nonactivatingPanel, .borderless, .fullSizeContentView],
                                 backing: .buffered, defer: false)
            panel.isOpaque = false
            panel.backgroundColor = .clear
            panel.hasShadow = true
            panel.level = .statusBar
            panel.isReleasedWhenClosed = false
            panel.hidesOnDeactivate = false
            panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .transient]
            panel.delegate = self
            panel.contentView = NSHostingView(rootView: StatusPanelView(dictation: .shared, stats: .shared) { [weak self] in
                self?.close()
            })
            self.panel = panel
        }
        guard let panel, let hosting = panel.contentView as? NSHostingView<StatusPanelView>,
              let buttonWindow = button.window, let screen = buttonWindow.screen ?? NSScreen.main else { return }
        let size = hosting.fittingSize
        let anchor = buttonWindow.convertToScreen(button.convert(button.bounds, to: nil))
        var x = anchor.midX - size.width / 2
        x = min(max(x, screen.visibleFrame.minX + 8), screen.visibleFrame.maxX - size.width - 8)
        panel.setFrame(NSRect(x: x, y: anchor.minY - size.height - 6, width: size.width, height: size.height), display: true)
        panel.alphaValue = 0
        // Take focus so Esc and the keyboard reach the panel (a menu bar app stays out of the Dock).
        NSApp.activate()
        panel.makeKeyAndOrderFront(nil)
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.16
            panel.animator().alphaValue = 1
        }
        button.highlight(true)
        // Close on any click outside: in other apps …
        outsideClick = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown]) { [weak self] _ in
            #if DEBUG
            if Demo.on { return }
            #endif
            MainActor.assumeIsolated { self?.close() }
        }
        // … and in Mindtalk's own windows (but not the status item, which toggles).
        insideClick = NSEvent.addLocalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown]) { [weak self] event in
            MainActor.assumeIsolated {
                guard let self, event.window !== self.panel, event.window !== self.button?.window else { return }
                self.close()
            }
            return event
        }
    }

    func close() {
        if let outsideClick { NSEvent.removeMonitor(outsideClick) }
        if let insideClick { NSEvent.removeMonitor(insideClick) }
        outsideClick = nil
        insideClick = nil
        button?.highlight(false)
        guard let panel, panel.isVisible else { return }
        NSAnimationContext.runAnimationGroup({ ctx in
            ctx.duration = 0.12
            panel.animator().alphaValue = 0
        }, completionHandler: {
            MainActor.assumeIsolated { panel.orderOut(nil) }
        })
    }

    func windowDidResignKey(_ notification: Notification) {
        #if DEBUG
        if Demo.on { return }   // screenshots: stay open whatever else takes focus
        #endif
        close()
    }

    /// Keeps the size in step with the content (e.g. when a dictation arrives).
    func refit() {
        guard let panel, panel.isVisible, let hosting = panel.contentView as? NSHostingView<StatusPanelView> else { return }
        let size = hosting.fittingSize
        var frame = panel.frame
        frame.origin.y += frame.height - size.height
        frame.size = size
        panel.setFrame(frame, display: true, animate: false)
    }
}

/// A borderless panel that can still take keyboard focus (Esc, buttons).
private final class KeyPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override func cancelOperation(_ sender: Any?) { (NSApp.delegate as? AppDelegate)?.closeStatusPanel() }
}

// MARK: - Content

enum PanelMetrics {
    static let width: CGFloat = 330
    /// Text starts here; row highlights sit 7 pt in from the edge.
    static let inset: CGFloat = 18
    static let highlightInset: CGFloat = 7
    static let corner: CGFloat = 18
}

struct StatusPanelView: View {
    @ObservedObject var dictation: Dictation
    @ObservedObject var stats: Stats
    let close: () -> Void

    /// Closes the panel and opens the window on `page`.
    private func go(_ page: Page) {
        close()
        AppDelegate.showWindow(page: page)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            PanelSeparator()
            today
            PanelSeparator()
            language
            PanelSeparator()
            recent
            PanelSeparator()
            PanelRow(title: "Öppna Mindtalk") { go(.dictation) }
            PanelRow(title: "Inställningar …", shortcut: "⌘,") { go(.settings) }
            PanelSeparator()
            PanelRow(title: "Avsluta Mindtalk", shortcut: "⌘Q") { NSApp.terminate(nil) }
        }
        .padding(.vertical, 8)
        .frame(width: PanelMetrics.width)
        // Nearly solid, so text keeps its contrast whatever is behind the panel.
        .background(DS.Colors.paper.opacity(0.97))
        .glassEffect(.regular, in: RoundedRectangle(cornerRadius: PanelMetrics.corner, style: .continuous))
        .clipShape(RoundedRectangle(cornerRadius: PanelMetrics.corner, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: PanelMetrics.corner, style: .continuous).strokeBorder(.white.opacity(0.1), lineWidth: 0.5))
        .fixedSize(horizontal: false, vertical: true)
        .background(shortcuts)
    }

    /// ⌘1 / ⌘2 switch language, ⌘, opens Settings, ⌘Q quits — as the rows show.
    private var shortcuts: some View {
        ZStack {
            ForEach(Array(SpeechModel.allCases.enumerated()), id: \.element) { i, m in
                Button("") { pick(m) }.keyboardShortcut(KeyEquivalent(Character("\(i + 1)")), modifiers: .command)
            }
            Button("") { go(.settings) }.keyboardShortcut(",", modifiers: .command)
            Button("") { NSApp.terminate(nil) }.keyboardShortcut("q", modifiers: .command)
        }
        .opacity(0)
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }

    private func pick(_ m: SpeechModel) {
        dictation.setEngine(m)
        if !m.isInstalled { dictation.downloadModel(m) }
    }

    // MARK: Header

    private var header: some View {
        HStack(spacing: 12) {
            TimelineView(.animation(paused: dictation.phase != .recording)) { context in
                LogoTile(size: 36, t: context.date.timeIntervalSinceReferenceDate,
                         animated: dictation.phase == .recording,
                         levels: dictation.phase == .recording ? dictation.levels : nil)
            }
            .shadow(color: .black.opacity(0.25), radius: 6, y: 2)
            VStack(alignment: .leading, spacing: 5) {
                Text("Mindtalk").font(.system(size: 15, weight: .semibold))
                status
            }
            Spacer(minLength: 8)
            if dictation.phase == .recording {
                Waveform(levels: Array(dictation.levels.suffix(14)), gap: 2)
                    .frame(width: 46, height: 16)
                    .transition(.opacity)
            }
        }
        .padding(.horizontal, PanelMetrics.inset)
        .padding(.top, 8)
        .padding(.bottom, 6)
        .animation(.easeOut(duration: 0.2), value: dictation.phase)
    }

    @ViewBuilder private var status: some View {
        HStack(spacing: 5) {
            switch dictation.phase {
            case .recording:
                Circle().fill(DS.Colors.recording).frame(width: 6, height: 6)
                Text(dictation.handsFree ? "Lyssnar – låst" : "Lyssnar …")
            case .transcribing:
                Text("Skriver …")
            default:
                switch dictation.model {
                case .downloading(let p):
                    Text("Laddar ned \(dictation.engine.title) · \(Int(p * 100)) %").monospacedDigit()
                case .loading:
                    Text("Startar …")
                default:
                    if dictation.isReady {
                        // The key as a keycap, as on the Diktering page.
                        Text(LocalizedStringKey(dictation.mode == .toggle ? "Tryck" : "Håll"))
                        KeyChip(text: dictation.hotkey.chip)
                            .fixedSize()
                            .layoutPriority(1)
                            .padding(.horizontal, 1)
                        Text("för att diktera")
                    } else {
                        Button {
                            close()
                            AppDelegate.showWindow()
                        } label: {
                            HStack(spacing: 5) {
                                Circle().fill(Color.orange).frame(width: 6, height: 6)
                                Text("Något behöver ordnas")
                                Image(systemName: "chevron.right").font(.system(size: 9, weight: .semibold))
                            }
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
        }
        .font(.system(size: 12.5))
        .foregroundStyle(DS.Colors.muted)
        .lineLimit(1)
        .frame(minHeight: 24, alignment: .leading)
    }

    // MARK: Today

    private var today: some View {
        let weekSaved = stats.week.reduce(0) { $0 + $1.stat.savedSeconds }
        // The bars step aside when big numbers or a long language need the room.
        return ViewThatFits(in: .horizontal) {
            todayRow(bars: true)
            todayRow(bars: false)
        }
        .font(.system(size: 12.5))
        .padding(.horizontal, PanelMetrics.inset)
        .frame(height: 40)
        .help(weekSaved >= 1 ? String(localized: "≈ \(Stats.duration(weekSaved)) sparad tid den här veckan") : "")
    }

    private func todayRow(bars: Bool) -> some View {
        HStack(alignment: .center, spacing: 14) {
            if stats.weekWords == 0 {
                Text("Inga ord den här veckan än").foregroundStyle(DS.Colors.muted)
            } else {
                figure(stats.today.words, "ord i dag")
                figure(stats.weekWords, "i veckan")
            }
            if bars {
                Spacer(minLength: 8)
                WeekBars(week: stats.week)
            } else {
                Spacer(minLength: 0)
            }
        }
    }

    private func figure(_ value: Int, _ label: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 4) {
            Text(Stats.number(value))
                .font(.system(size: 20, weight: .regular, design: .serif))
                .monospacedDigit()
                .contentTransition(.numericText())
            Text(LocalizedStringKey(label)).foregroundStyle(DS.Colors.muted)
        }
        .lineLimit(1)
        .fixedSize()
    }

    // MARK: Language

    private var language: some View {
        VStack(alignment: .leading, spacing: 0) {
            PanelSectionTitle("Språk")
            ForEach(Array(SpeechModel.allCases.enumerated()), id: \.element) { i, m in
                LanguageRow(model: m, index: i + 1, selected: dictation.engine == m,
                            installed: m.isInstalled, download: dictation.downloads[m]) { pick(m) }
            }
        }
    }

    // MARK: Recent

    private var recent: some View {
        VStack(alignment: .leading, spacing: 0) {
            PanelSectionTitle("Senaste")
            if dictation.recent.isEmpty {
                Text("Det du dikterar hamnar här.")
                    .font(.system(size: 14))
                    .foregroundStyle(DS.Colors.muted)
                    .padding(.horizontal, PanelMetrics.inset)
                    .frame(height: 30)
            } else {
                TimelineView(.everyMinute) { _ in
                    VStack(spacing: 0) {
                        ForEach(dictation.recent.prefix(3)) { entry in
                            RecentRow(entry: entry) { dictation.copy(entry) }
                        }
                    }
                }
                if dictation.recent.count > 3 {
                    PanelRow(title: "Visa alla …", muted: true) { go(.recent) }
                }
            }
        }
    }
}

// MARK: - Rows

/// A menu row: the hover is a soft rounded fill, like the system's menu bar menus.
private struct HoverRow<Content: View>: View {
    var height: CGFloat = 30
    let action: () -> Void
    @ViewBuilder let content: (_ hovering: Bool) -> Content
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            content(hovering)
                .padding(.horizontal, PanelMetrics.inset - PanelMetrics.highlightInset)
                .frame(maxWidth: .infinity, minHeight: height, alignment: .leading)
                .background(RoundedRectangle(cornerRadius: 9, style: .continuous)
                    .fill(Color.primary.opacity(hovering ? 0.08 : 0)))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .padding(.horizontal, PanelMetrics.highlightInset)
        .onHover { hovering = $0 }
    }
}

private struct PanelRow: View {
    let title: String
    var shortcut: String? = nil
    var muted = false
    let action: () -> Void

    var body: some View {
        HoverRow(action: action) { _ in
            HStack {
                Text(LocalizedStringKey(title))
                    .foregroundStyle(muted ? AnyShapeStyle(DS.Colors.muted) : AnyShapeStyle(.primary))
                Spacer(minLength: 12)
                if let shortcut { Text(shortcut).font(.system(size: 13)).foregroundStyle(DS.Colors.faint) }
            }
            .font(.system(size: 14))
        }
    }
}

/// A language: its badge in a circle — filled when it's the one in use.
private struct LanguageRow: View {
    let model: SpeechModel
    let index: Int
    let selected: Bool
    let installed: Bool
    let download: Double?
    let action: () -> Void

    var body: some View {
        HoverRow(height: 42, action: action) { hovering in
            HStack(spacing: 11) {
                ZStack {
                    Circle().fill(selected ? DS.Colors.ink : DS.Colors.chip)
                    ModelBadge(model: model, size: 11.5)
                        .foregroundStyle(selected ? DS.Colors.onInk : DS.Colors.ink)
                }
                .frame(width: 30, height: 30)
                .animation(.easeOut(duration: 0.18), value: selected)
                Text(LocalizedStringKey(model.title)).font(.system(size: 14))
                Spacer(minLength: 8)
                trailing(hovering: hovering)
                    .font(.system(size: 13))
                    .foregroundStyle(DS.Colors.faint)
            }
        }
        .help(LocalizedStringKey(model.pitch))
    }

    @ViewBuilder private func trailing(hovering: Bool) -> some View {
        if let download {
            HStack(spacing: 5) {
                Text("\(Int(download * 100)) %").monospacedDigit()
                ProgressRing(progress: download)
            }
        } else if !installed {
            HStack(spacing: 4) {
                Text(model.sizeText)
                Image(systemName: "arrow.down.circle")
            }
            .foregroundStyle(hovering ? DS.Colors.muted : DS.Colors.faint)
        } else {
            Text("⌘\(index)")
        }
    }
}

/// One dictation on one line; the time gives way to a copy glyph on hover.
private struct RecentRow: View {
    let entry: Dictation.Entry
    let copy: () -> Void
    @State private var copied = false

    var body: some View {
        HoverRow {
            copy()
            withAnimation(.easeOut(duration: 0.15)) { copied = true }
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.2) {
                withAnimation(.easeOut(duration: 0.2)) { copied = false }
            }
        } content: { hovering in
            HStack(spacing: 10) {
                Text(entry.text.replacingOccurrences(of: "\n", with: " "))
                    .font(.system(size: 14))
                    .lineLimit(1)
                    .truncationMode(.tail)
                Spacer(minLength: 8)
                Group {
                    if copied {
                        Label("Kopierat", systemImage: "checkmark").labelStyle(.titleAndIcon)
                            .foregroundStyle(.primary)
                    } else if hovering {
                        Image(systemName: "doc.on.doc").foregroundStyle(DS.Colors.muted)
                    } else {
                        Text(Self.ago(entry.date)).monospacedDigit().foregroundStyle(DS.Colors.faint)
                    }
                }
                .font(.system(size: 12))
            }
        }
        .help(String(localized: "Kopiera"))
    }

    /// "nu", "5 min", "9 tim", "2 d" — the compact form menus use.
    static func ago(_ date: Date) -> String {
        let seconds = Date().timeIntervalSince(date)
        if seconds < 60 { return String(localized: "nu") }
        let f = DateComponentsFormatter()
        f.unitsStyle = .abbreviated
        f.maximumUnitCount = 1
        f.allowedUnits = [.minute, .hour, .day, .weekOfMonth]
        var calendar = Calendar.current
        calendar.locale = AppLanguage.locale
        f.calendar = calendar
        return f.string(from: seconds) ?? ""
    }
}

private struct PanelSectionTitle: View {
    let title: String
    init(_ title: String) { self.title = title }

    var body: some View {
        Text(LocalizedStringKey(title))
            .font(.system(size: 12, weight: .semibold))
            .foregroundStyle(DS.Colors.muted)
            .padding(.horizontal, PanelMetrics.inset)
            .padding(.top, 6)
            .padding(.bottom, 4)
    }
}

private struct PanelSeparator: View {
    var body: some View {
        Rectangle()
            .fill(DS.Colors.divider)
            .frame(height: 1)
            .padding(.horizontal, PanelMetrics.inset)
            .padding(.vertical, 7)
    }
}

/// Monday to Sunday in seven slim bars: today in ink, other days grey,
/// days without words a hairline.
private struct WeekBars: View {
    let week: [Stats.WeekDay]

    var body: some View {
        let peak = max(1, week.map(\.stat.words).max() ?? 1)
        HStack(alignment: .bottom, spacing: 3) {
            ForEach(week.indices, id: \.self) { i in
                let day = week[i]
                RoundedRectangle(cornerRadius: 1.5, style: .continuous)
                    .fill(DS.Colors.ink.opacity(day.isToday ? 1 : day.stat.words > 0 ? 0.3 : day.isFuture ? 0.08 : 0.15))
                    .frame(width: 6, height: day.stat.words > 0 ? max(3, CGFloat(day.stat.words) / CGFloat(peak) * 20) : 2)
            }
        }
        .frame(height: 20, alignment: .bottom)
        .accessibilityHidden(true)
    }
}

private struct ProgressRing: View {
    let progress: Double
    var body: some View {
        ZStack {
            Circle().stroke(DS.Colors.divider, lineWidth: 2)
            Circle().trim(from: 0, to: max(0.02, progress))
                .stroke(DS.Colors.ink, style: StrokeStyle(lineWidth: 2, lineCap: .round))
                .rotationEffect(.degrees(-90))
        }
        .frame(width: 12, height: 12)
    }
}
