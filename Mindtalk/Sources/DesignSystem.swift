import AppKit
import SwiftUI

// A quiet black-and-white look: white paper, soft grey cards without borders,
// serif page titles, ink controls — black in light mode, white in dark. Every color is
// dynamic, so it follows the Ljust / Mörkt / System choice in Settings.
// Liquid Glass is reserved for the HUD.

enum DS {
    enum Radius {
        static let card: CGFloat = 16
        static let control: CGFloat = 10
        static let chip: CGFloat = 7
    }

    enum Layout {
        /// The page column: comfortable to read, a little roomier on big windows.
        static let readingWidth: CGFloat = 720
        static let wideReadingWidth: CGFloat = 860
        static let pageHorizontal: CGFloat = 48
        static let pageTop: CGFloat = 44
        static let sidebarWidth: CGFloat = 216
    }

    enum Colors {
        /// Page background.
        static let paper = dynamic(light: 0xFBFBFB, dark: 0x0E0E0E)
        /// Sidebar.
        static let sidebar = dynamic(light: 0xF3F3F3, dark: 0x161616)
        /// Cards and grouped rows.
        static let card = dynamic(light: 0xF0F0F0, dark: 0x1E1E1E)
        /// Key chips, soft buttons, selected sidebar row.
        static let chip = dynamic(light: 0xE5E5E5, dark: 0x303030)
        /// Text fields and the shortcut box.
        static let field = dynamic(light: 0xFFFFFF, dark: 0x171717)
        static let fieldStroke = dynamic(light: 0xD4D4D4, dark: 0x3C3C3C)
        /// The raised, chosen option in a pill picker.
        static let pill = dynamic(light: 0xFFFFFF, dark: 0x585858)
        /// Dividers inside cards.
        static let divider = dynamic(light: 0xE0E0E0, dark: 0x303030)
        /// Secondary text: explanations, labels, timestamps. Darker than the system's
        /// secondary grey, so it keeps at least 6:1 contrast on paper and cards.
        static let muted = dynamic(light: 0x5C5C5C, dark: 0xA9A9A9)
        /// Deliberately quiet text (days still to come, version number).
        static let faint = dynamic(light: 0x8C8C8C, dark: 0x707070)
        /// Soft buttons and key chips: raised off the card, with an edge.
        static let control = dynamic(light: 0xFFFFFF, dark: 0x383838)
        /// Primary buttons and switches.
        static let ink = dynamic(light: 0x111111, dark: 0xF2F2F2)
        /// Switches when on — ink in light mode, a warm grey in dark so the white knob shows.
        static let switchOn = dynamic(light: 0x111111, dark: 0xD0D0D0)
        /// Text on ink.
        static let onInk = dynamic(light: 0xFFFFFF, dark: 0x0E0E0E)
        /// Mindtalk's accent — ink: black in light mode, white in dark. Selection, waveforms, highlights.
        static let accent = dynamic(light: 0x111111, dark: 0xF2F2F2)
        /// The lighter end of the accent sheen.
        static let accentSoft = dynamic(light: 0x5A5A5A, dark: 0xFFFFFF)
        static let recording = Color.red
        static let good = Color(red: 0.20, green: 0.62, blue: 0.36)

        private static func dynamic(light: UInt32, dark: UInt32) -> Color {
            Color(nsColor: NSColor(name: nil) { appearance in
                let isDark = appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
                return NSColor(hex: isDark ? dark : light)
            })
        }
    }

    enum Gradients {
        /// The accent (ink) with a soft sheen — selection rings, progress, the typing cursor.
        static let accent = LinearGradient(colors: [Colors.accentSoft, Colors.accent],
                                           startPoint: .topLeading, endPoint: .bottomTrailing)
        /// The app icon's tile: deep black with a little light at the top.
        static let tile = LinearGradient(colors: [Color(white: 0.24), Color(white: 0.04)],
                                         startPoint: .top, endPoint: .bottom)
    }

    /// Page titles: large, light serif.
    static let pageTitle = Font.system(size: 38, weight: .regular, design: .serif)
}

private extension NSColor {
    convenience init(hex: UInt32) {
        self.init(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
                  blue: CGFloat(hex & 0xFF) / 255, alpha: 1)
    }
}

// MARK: - Page building blocks

extension View {
    /// Standard page: a centred column (so a zoomed or full-screen window stays
    /// balanced), generous padding, scrolls.
    func pageLayout() -> some View {
        GeometryReader { geo in
            ScrollView {
                self
                    .frame(maxWidth: geo.size.width > 1200 ? DS.Layout.wideReadingWidth : DS.Layout.readingWidth,
                           alignment: .leading)
                    .padding(.horizontal, DS.Layout.pageHorizontal)
                    .padding(.top, DS.Layout.pageTop)
                    .padding(.bottom, 40)
                    .frame(maxWidth: .infinity)
            }
            .scrollContentBackground(.hidden)
        }
    }

    /// A raised surface: paper-white with a clear edge and a soft shadow, so it
    /// stands off the page in light mode as well as dark.
    func raised(cornerRadius: CGFloat = 20, selected: Bool = false) -> some View {
        let shape = RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
        return self
            .background(shape.fill(DS.Colors.field)
                .shadow(color: .black.opacity(selected ? 0.14 : 0.06), radius: selected ? 18 : 8, y: selected ? 8 : 3))
            .overlay(shape.strokeBorder(selected ? DS.Colors.ink : DS.Colors.fieldStroke, lineWidth: selected ? 2 : 1))
    }

    /// A soft grey card holding rows.
    func card() -> some View {
        self.background(RoundedRectangle(cornerRadius: DS.Radius.card, style: .continuous).fill(DS.Colors.card))
    }
}

// Components take plain Strings and look them up in the string catalog, so a
// literal like CardRow(title: "Mikrofon") is translated, and a String that is
// already localized simply isn't found and shows as is.

/// Serif page title with an optional line under it.
struct PageHeader: View {
    let title: String
    var subtitle: String? = nil

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(LocalizedStringKey(title)).font(DS.pageTitle)
            if let subtitle {
                Text(LocalizedStringKey(subtitle))
                    .font(.title3)
                    .foregroundStyle(DS.Colors.muted)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}

/// "App-inställningar", "Ljud" … above a card.
struct SectionTitle: View {
    let text: String
    init(_ text: String) { self.text = text }
    var body: some View {
        Text(LocalizedStringKey(text))
            .font(.system(size: 15, weight: .semibold))
            .padding(.top, 8)
            .accessibilityAddTraits(.isHeader)
    }
}

/// One row in a card: title, optional explanation, control on the right.
struct CardRow<Control: View>: View {
    let title: String
    var detail: String? = nil
    @ViewBuilder var control: Control

    var body: some View {
        HStack(alignment: .center, spacing: 20) {
            VStack(alignment: .leading, spacing: 4) {
                Text(LocalizedStringKey(title)).font(.system(size: 14, weight: .semibold))
                if let detail {
                    Text(LocalizedStringKey(detail))
                        .font(.system(size: 13))
                        .foregroundStyle(DS.Colors.muted)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: 12)
            control
        }
        .padding(.horizontal, 22)
        .padding(.vertical, 18)
    }
}

/// Divider inset like the card's content.
struct CardDivider: View {
    var body: some View {
        Rectangle().fill(DS.Colors.divider).frame(height: 1).padding(.horizontal, 22)
    }
}

/// A key as a small keycap: "⌥ Opt →".
struct KeyChip: View {
    let text: String
    var body: some View {
        let shape = RoundedRectangle(cornerRadius: DS.Radius.chip, style: .continuous)
        Text(text)
            .font(.system(size: 13, weight: .semibold))
            .foregroundStyle(.primary)
            .padding(.horizontal, 8)
            .padding(.vertical, 3)
            .background(shape.fill(DS.Colors.control).shadow(color: .black.opacity(0.08), radius: 0, y: 1))
            .overlay(shape.strokeBorder(DS.Colors.fieldStroke, lineWidth: 1))
    }
}

/// "✓ Tillåtet" — ink text, with green only in the check itself.
struct GrantedLabel: View {
    var text = "Tillåtet"
    var body: some View {
        Label {
            Text(LocalizedStringKey(text))
        } icon: {
            Image(systemName: "checkmark.circle.fill").foregroundStyle(DS.Colors.good)
        }
        .font(.system(size: 13, weight: .semibold))
        .foregroundStyle(.primary)
    }
}

// MARK: - Buttons & switches

/// Soft button ("Ändra"): a raised control with a clear edge.
struct SoftButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        let shape = RoundedRectangle(cornerRadius: DS.Radius.control, style: .continuous)
        return configuration.label
            .font(.system(size: 13, weight: .semibold))
            .foregroundStyle(.primary)
            .padding(.horizontal, 16)
            .padding(.vertical, 7)
            .background(shape.fill(DS.Colors.control.opacity(configuration.isPressed ? 0.7 : 1))
                .shadow(color: .black.opacity(0.06), radius: 2, y: 1))
            .overlay(shape.strokeBorder(DS.Colors.fieldStroke, lineWidth: 1))
            .contentShape(Rectangle())
    }
}

/// Ink-black primary button ("Klart").
struct InkButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 13, weight: .semibold))
            .foregroundStyle(DS.Colors.onInk)
            .padding(.horizontal, 16)
            .padding(.vertical, 7)
            .background(RoundedRectangle(cornerRadius: DS.Radius.control, style: .continuous)
                .fill(DS.Colors.ink.opacity(configuration.isPressed ? 0.8 : 1)))
            .contentShape(Rectangle())
    }
}

extension ButtonStyle where Self == SoftButtonStyle {
    static var soft: SoftButtonStyle { SoftButtonStyle() }
}

extension ButtonStyle where Self == InkButtonStyle {
    static var ink: InkButtonStyle { InkButtonStyle() }
}

/// An ink switch, like Wispr Flow's.
struct InkToggle: View {
    let label: String
    @Binding var isOn: Bool
    var body: some View {
        Toggle(LocalizedStringKey(label), isOn: $isOn)
            .toggleStyle(.switch)
            .tint(DS.Colors.switchOn)
            .labelsHidden()
    }
}

// MARK: - Waveform

/// Bars scrolling right-to-left — the last moments of the mic, newest brightest.
/// Used in the mic picker and the dictation HUD.
struct Waveform: View {
    let levels: [Float]
    var gap: CGFloat = 3
    var dimmed = false

    var body: some View {
        GeometryReader { geo in
            let count = levels.count
            let width = max(1, (geo.size.width - gap * CGFloat(count - 1)) / CGFloat(count))
            HStack(alignment: .center, spacing: gap) {
                ForEach(levels.indices, id: \.self) { i in
                    // Square root lifts quiet speech so the wave reads at a distance too.
                    let v = CGFloat(min(1, levels[i].squareRoot() * 1.15))
                    let fade = 0.35 + 0.65 * Double(i) / Double(max(1, count - 1))
                    Capsule()
                        .fill(DS.Colors.accent.opacity(dimmed ? fade * 0.4 : fade))
                        .frame(width: width, height: max(width, v * geo.size.height))
                }
            }
            .frame(width: geo.size.width, height: geo.size.height)
        }
        .animation(.linear(duration: 0.06), value: levels)
        .animation(.easeOut(duration: 0.3), value: dimmed)
        .accessibilityHidden(true)
    }
}

// MARK: - Pill picker

/// Mindtalk's segmented control: a warm track with the chosen option lifted out
/// as a paper pill that slides between options. Replaces the system's blue one.
struct PillPicker<Value: Hashable>: View {
    let options: [(value: Value, title: String)]
    @Binding var selection: Value
    var compact = false
    @Namespace private var pill

    var body: some View {
        HStack(spacing: 0) {
            ForEach(options, id: \.value) { option in
                let selected = option.value == selection
                Button {
                    withAnimation(.spring(duration: 0.35, bounce: 0.2)) { selection = option.value }
                } label: {
                    Text(LocalizedStringKey(option.title))
                        .font(.system(size: compact ? 12 : 12.5, weight: selected ? .semibold : .medium))
                        .foregroundStyle(selected ? AnyShapeStyle(.primary) : AnyShapeStyle(DS.Colors.muted))
                        .padding(.horizontal, compact ? 10 : 13)
                        .frame(height: compact ? 24 : 28)
                        .frame(maxWidth: compact ? .infinity : nil)
                        .background {
                            if selected {
                                Capsule()
                                    .fill(DS.Colors.pill)
                                    .shadow(color: .black.opacity(0.14), radius: 3, y: 1)
                                    .overlay(Capsule().strokeBorder(DS.Colors.fieldStroke.opacity(0.6), lineWidth: 0.5))
                                    .matchedGeometryEffect(id: "pill", in: pill)
                            }
                        }
                        .contentShape(Capsule())
                }
                .buttonStyle(.plain)
                .accessibilityAddTraits(selected ? .isSelected : [])
            }
        }
        .padding(3)
        .background(Capsule().fill(DS.Colors.chip))
        .fixedSize(horizontal: !compact, vertical: true)
    }
}

// MARK: - Menu picker

/// A pop-up menu that looks like the rest of Mindtalk: the choice in a soft
/// capsule with a small chevron.
struct MenuPicker<Value: Hashable>: View {
    let options: [(value: Value, title: String)]
    @Binding var selection: Value

    var body: some View {
        Menu {
            ForEach(options, id: \.value) { option in
                Button {
                    selection = option.value
                } label: {
                    if option.value == selection {
                        Label(LocalizedStringKey(option.title), systemImage: "checkmark")
                    } else {
                        Text(LocalizedStringKey(option.title))
                    }
                }
            }
        } label: {
            HStack(spacing: 7) {
                Text(LocalizedStringKey(options.first { $0.value == selection }?.title ?? ""))
                    .font(.system(size: 12.5, weight: .semibold))
                Image(systemName: "chevron.up.chevron.down")
                    .font(.system(size: 9, weight: .bold))
                    .foregroundStyle(DS.Colors.muted)
                    .accessibilityHidden(true)
            }
            .padding(.horizontal, 13)
            .frame(height: 30)
            .background(Capsule().fill(DS.Colors.control).shadow(color: .black.opacity(0.06), radius: 2, y: 1))
            .overlay(Capsule().strokeBorder(DS.Colors.fieldStroke, lineWidth: 1))
            .contentShape(Capsule())
        }
        .menuStyle(.button)
        .buttonStyle(.plain)
        .menuIndicator(.hidden)
        .fixedSize()
    }
}

// MARK: - The logo mark

/// The app icon — the mark on its black tile — drawn live. Idle it's the icon;
/// animated, the wave rolls along and the cursor blinks; given `levels` (while
/// dictating) the wave swells with your voice.
struct LogoTile: View {
    let size: CGFloat
    var t: Double = 0
    var animated = false
    var levels: [Float]? = nil

    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: size * 0.225, style: .continuous)
                .fill(DS.Gradients.tile)
                .overlay(
                    RoundedRectangle(cornerRadius: size * 0.225, style: .continuous)
                        .fill(LinearGradient(colors: [.white.opacity(0.16), .clear], startPoint: .top, endPoint: .center))
                )
                .overlay(RoundedRectangle(cornerRadius: size * 0.225, style: .continuous)
                    .strokeBorder(.white.opacity(0.18), lineWidth: max(0.5, size / 118)))
            Canvas { ctx, canvas in
                let transform = Mark.tileTransform(size: canvas.width)
                let line = Mark.strokeWidth * canvas.width / 824
                let wave = Path(Mark.wave(amplitude: amplitude, phase: phase)).applying(transform)
                ctx.stroke(wave, with: .color(.white), style: StrokeStyle(lineWidth: line, lineCap: .round, lineJoin: .round))
                let on = !animated || Int(t * 1.9) % 2 == 0
                ctx.fill(Path(Mark.cursor).applying(transform), with: .color(.white.opacity(on ? 1 : 0.2)))
            }
            .shadow(color: .black.opacity(0.4), radius: size / 40, y: size / 80)
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }

    private var amplitude: CGFloat {
        if let levels, let last = levels.last {
            // Follow the voice: quiet is a ripple, speech is a full swell.
            let recent = levels.suffix(4).reduce(0, +) / Float(min(4, levels.count))
            return 0.25 + CGFloat(min(1, (max(last, recent)).squareRoot() * 1.1)) * 0.95
        }
        return animated ? 0.9 + 0.12 * sin(t * 1.7) : 1
    }

    private var phase: CGFloat { animated || levels != nil ? CGFloat(-t * 4) : 0 }
}

// MARK: - Model badge

/// The language model as a small mark: "SV" for Swedish, a globe for the
/// multilingual one (a two-letter code there would suggest a single language).
struct ModelBadge: View {
    let model: SpeechModel
    var size: CGFloat = 13
    var serif = false

    var body: some View {
        switch model {
        case .swedish:
            Text(model.code)
                .font(.system(size: size, weight: serif ? .regular : .bold, design: serif ? .serif : .rounded))
        case .multilingual:
            Image(systemName: "globe")
                .font(.system(size: size * (serif ? 0.95 : 1.05), weight: .medium))
        }
    }
}
