import ServiceManagement
import SwiftUI

// MARK: - Main window
//
// A floating tab bar (TabBar.swift) and four pages:
//   • Diktering — what's missing, statistics, how to use it, and a box to try it in
//   • Senaste — the last dictations, to copy again
//   • Ordlista — names and words spelled your way (VocabularyPage.swift)
//   • Inställningar — shortcuts, text, app, sound, appearance, languages, about

enum Page: String, CaseIterable, Identifiable {
    case dictation, recent, vocabulary, settings

    var id: String { rawValue }

    var title: String {
        switch self {
        case .dictation: return String(localized: "Diktering")
        case .recent: return String(localized: "Senaste")
        case .vocabulary: return String(localized: "Ordlista")
        case .settings: return String(localized: "Inställningar")
        }
    }

    var icon: String {
        switch self {
        case .dictation: return "waveform"
        case .recent: return "clock"
        case .vocabulary: return "character.book.closed"
        case .settings: return "slider.horizontal.3"
        }
    }
}

struct MainView: View {
    @ObservedObject var dictation: Dictation
    @ObservedObject var prefs: AppPrefs
    @State private var page: Page = MainView.startPage
    /// Where the window opens (Settings after a language switch).
    static var startPage: Page = .dictation

    var body: some View {
        // The page gets the whole window; a tab bar floats at the leading edge.
        ZStack(alignment: .leading) {
            ZStack(alignment: .top) {
                // A soft wash of the brand's warm light behind the page headers.
                WarmGlow(intensity: 0.32)
                    .frame(height: 300)
                    .allowsHitTesting(false)
                ZStack {
                    switch page {
                    case .dictation: DictationPage(dictation: dictation, page: $page)
                    case .recent: RecentPage(dictation: dictation)
                    case .vocabulary: VocabularyPage(vocabulary: .shared)
                    case .settings: SettingsPage(dictation: dictation, prefs: prefs)
                    }
                }
                .id(page)
                .transition(.page)
            }
            .padding(.leading, 72)          // clear of the folded tab bar
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            .background(DS.Colors.paper)

            FloatingTabBar(page: $page, dictation: dictation)
                .padding(.leading, 16)
        }
        .ignoresSafeArea()
        .frame(minWidth: 820, minHeight: 580)
        .onAppear { dictation.refreshPermissions() }
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            dictation.refreshPermissions()
        }
        .onReceive(NotificationCenter.default.publisher(for: .showPage)) { note in
            if let target = note.object as? Page { withAnimation(.spring(duration: 0.45)) { page = target } }
        }
    }
}

extension Notification.Name {
    /// Switch the main window to a page (object: Page).
    static let showPage = Notification.Name("MindtalkShowPage")
    /// Scroll Settings to "Om Mindtalk".
    static let showAbout = Notification.Name("MindtalkShowAbout")
}

// MARK: - Diktering

private struct DictationPage: View {
    @ObservedObject var dictation: Dictation
    @Binding var page: Page
    @State private var practice = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 26) {
            PageHeader(title: "Diktering",
                       subtitle: "Prata istället för att skriva – i vilken app som helst. Allt tolkas på din Mac.")
                .staggered(0)

            if dictation.needsSetup { setupCard.staggered(1) }

            StatsCard(stats: .shared)
                .staggered(1)

            VStack(alignment: .leading, spacing: 0) {
                howTo(icon: "hand.tap", title: "Håll in och prata",
                      detail: "Släpp när du är klar – texten skrivs där markören står.") {
                    HStack(spacing: 6) { Text("Håll").foregroundStyle(DS.Colors.muted); KeyChip(text: dictation.hotkey.chip) }
                }
                if dictation.mode != .hold {
                    CardDivider()
                    howTo(icon: "lock", title: "Handsfree",
                          detail: dictation.mode == .toggle ? "Tryck en gång för att börja, en gång till för att klistra in."
                                                            : "Dubbeltryck – eller mellanslag medan du håller in – för att låsa. Tryck igen när du är klar.") {
                        HStack(spacing: 6) {
                            Text(LocalizedStringKey(dictation.mode == .toggle ? "Tryck" : "Dubbeltryck")).foregroundStyle(DS.Colors.muted)
                            KeyChip(text: dictation.hotkey.chip)
                        }
                    }
                }
                CardDivider()
                howTo(icon: "escape", title: "Ångra dig", detail: "Avbryter utan att något skrivs.") {
                    KeyChip(text: "esc")
                }
            }
            .card()
            .staggered(2)

            if !dictation.needsSetup {
                VStack(alignment: .leading, spacing: 10) {
                    SectionTitle("Prova")
                    ZStack(alignment: .topLeading) {
                        TextEditor(text: $practice)
                            .font(.system(size: 17))
                            .scrollContentBackground(.hidden)
                            .padding(12)
                            .frame(minHeight: 130)
                            .background(RoundedRectangle(cornerRadius: DS.Radius.card, style: .continuous).fill(DS.Colors.field))
                            .overlay(RoundedRectangle(cornerRadius: DS.Radius.card, style: .continuous)
                                .strokeBorder(practice.isEmpty ? AnyShapeStyle(DS.Colors.fieldStroke) : AnyShapeStyle(DS.Gradients.accent),
                                              lineWidth: practice.isEmpty ? 1 : 1.5))
                            .shadow(color: DS.Colors.accent.opacity(practice.isEmpty ? 0 : 0.12), radius: 14, y: 6)
                            .animation(.easeOut(duration: 0.3), value: practice.isEmpty)
                            .accessibilityLabel("Prova diktering här")
                        if practice.isEmpty {
                            Text("Klicka här, håll in \(dictation.hotkey.inlineName) och säg något.")
                                .font(.system(size: 17))
                                .foregroundStyle(DS.Colors.muted)
                                .padding(.horizontal, 17).padding(.vertical, 12)
                                .allowsHitTesting(false)
                        }
                    }
                }
                .staggered(3)
            }
        }
        .pageLayout()
    }

    private func howTo<Trailing: View>(icon: String, title: String, detail: String,
                                       @ViewBuilder trailing: () -> Trailing) -> some View {
        HStack(spacing: 16) {
            Image(systemName: icon)
                .font(.system(size: 16))
                .foregroundStyle(DS.Colors.muted)
                .frame(width: 22)
            VStack(alignment: .leading, spacing: 3) {
                Text(LocalizedStringKey(title)).font(.system(size: 14, weight: .semibold))
                Text(LocalizedStringKey(detail)).font(.system(size: 13)).foregroundStyle(DS.Colors.muted)
            }
            Spacer(minLength: 12)
            trailing()
        }
        .padding(.horizontal, 22)
        .padding(.vertical, 16)
    }

    /// Only what's still missing, each with one button.
    private var setupCard: some View {
        VStack(alignment: .leading, spacing: 10) {
            SectionTitle("Kom igång")
            VStack(spacing: 0) {
                CardRow(title: "Mikrofon", detail: "Hör vad du säger medan du dikterar – aldrig annars.") {
                    if dictation.micGranted { GrantedLabel(text: "Tillåten") }
                    else { Button("Tillåt") { dictation.requestMicrophone() }.buttonStyle(.ink) }
                }
                CardDivider()
                CardRow(title: "Hjälpmedel", detail: "Låter Mindtalk märka tangenten och skriva i andra appar. Den läser aldrig vad du skriver.") {
                    if dictation.accessibilityGranted { GrantedLabel() }
                    else { Button("Tillåt") { dictation.requestAccessibility() }.buttonStyle(.ink) }
                }
                CardDivider()
                CardRow(title: String(localized: "Språkmodell – \(dictation.engine.title.lowercased())"), detail: modelDetail(dictation.model, dictation.engine)) {
                    ModelControl(dictation: dictation)
                }
            }
            .card()
        }
    }
}

private func modelDetail(_ model: Dictation.ModelState, _ engine: SpeechModel) -> String {
    switch model {
    case .missing: return String(localized: "\(engine.modelName), \(engine.sizeText). Laddas ned en gång och fungerar sedan offline.")
    case .downloading(let p) where p >= 0.9: return String(localized: "Förbereder modellen för din Mac …")
    case .downloading: return String(localized: "Laddar ned \(engine.modelName) – bara den här gången.")
    case .loading: return String(localized: "Startar – första gången kan det ta upp till en minut.")
    case .failed(let message): return message
    case .ready: return String(localized: "Ljudet raderas direkt efteråt. Ingen AI skriver om dina ord.")
    }
}

private struct ModelControl: View {
    @ObservedObject var dictation: Dictation

    var body: some View {
        switch dictation.model {
        case .ready:
            VStack(alignment: .trailing, spacing: 3) {
                Text(dictation.engine.modelName).font(.system(size: 14, weight: .semibold))
                Label("På din Mac", systemImage: "lock.fill").font(.caption).foregroundStyle(DS.Colors.muted)
            }
        case .missing, .failed:
            Button(LocalizedStringKey(dictation.model == .missing ? "Ladda ned" : "Försök igen")) { dictation.downloadModel() }
                .buttonStyle(.ink)
        case .downloading(let p):
            HStack(spacing: 10) {
                ProgressView(value: p).frame(width: 110).tint(DS.Colors.ink)
                Text(p, format: .percent.precision(.fractionLength(0)))
                    .monospacedDigit()
                    .foregroundStyle(DS.Colors.muted)
                Button("Avbryt") { dictation.cancelDownload() }.buttonStyle(.soft)
            }
        case .loading:
            HStack(spacing: 6) {
                ProgressView().controlSize(.small)
                Text("Startar …").foregroundStyle(DS.Colors.muted)
            }
        }
    }
}

// MARK: - Inställningar

struct SettingsPage: View {
    @ObservedObject var dictation: Dictation
    @ObservedObject var prefs: AppPrefs
    @ObservedObject private var mics = Microphones.shared
    @State private var launchAtLogin = SMAppService.mainApp.status == .enabled
    @State private var showMicPicker = false
    @State private var appLanguage = AppLanguage.chosen

    /// Set before opening Settings to land on "Om Mindtalk".
    static var jumpToAbout = false

    var body: some View {
        ScrollViewReader { proxy in
        VStack(alignment: .leading, spacing: 14) {
            PageHeader(title: "Inställningar")
                .staggered(0)
                .padding(.bottom, 12)

            SectionTitle("Genvägar")
            .staggered(1)
            VStack(spacing: 0) {
                CardRow(title: "Diktertangent", detail: dictation.hotkey.note) { ShortcutField(dictation: dictation) }
                CardDivider()
                CardRow(title: "Sätt att diktera", detail: dictation.mode.explanation(key: dictation.hotkey.inlineName)) {
                    MenuPicker(options: DictationMode.allCases.map { ($0, $0.title) },
                               selection: Binding(get: { dictation.mode }, set: { dictation.setMode($0) }))
                }
            }
            .card()
            .staggered(1)

            SectionTitle("Text")
            .staggered(2)
            TextCleanupCard(prefs: prefs)
                .staggered(2)

            SectionTitle("App")
            .staggered(2)
            VStack(spacing: 0) {
                CardRow(title: "Starta vid inloggning") {
                    InkToggle(label: "Starta vid inloggning", isOn: $launchAtLogin)
                        .onChange(of: launchAtLogin) { _, on in
                            if on { try? SMAppService.mainApp.register() } else { try? SMAppService.mainApp.unregister() }
                        }
                }
                CardDivider()
                CardRow(title: "Visa i Dock", detail: "Annars finns Mindtalk bara i menyraden när fönstret är stängt.") {
                    InkToggle(label: "Visa i Dock", isOn: $prefs.showInDock)
                }
                CardDivider()
                CardRow(title: "Ljud vid diktering", detail: "Ett diskret ljud när Mindtalk börjar lyssna och när texten klistras in.") {
                    InkToggle(label: "Ljud vid diktering", isOn: $prefs.sounds)
                }
                CardDivider()
                CardRow(title: "Musik medan du dikterar", detail: prefs.mediaMode.explanation) {
                    MenuPicker(options: MediaMode.allCases.map { ($0, $0.title) }, selection: $prefs.mediaMode)
                }
                CardDivider()
                CardRow(title: "Appens språk",
                        detail: appLanguage.resolved != AppLanguage.running ? "Mindtalk startar om för att byta språk." : nil) {
                    HStack(spacing: 10) {
                        if appLanguage.resolved != AppLanguage.running {
                            Button("Starta om") { AppDelegate.relaunch(showing: .settings) }
                                .buttonStyle(.ink)
                                .fixedSize()
                        }
                        PillPicker(options: AppLanguage.allCases.map { ($0, $0.title) }, selection: $appLanguage)
                            .onChange(of: appLanguage) { _, new in AppLanguage.chosen = new }
                    }
                }
                CardDivider()
                CardRow(title: "Utseende") {
                    PillPicker(options: Appearance.allCases.map { ($0, $0.title) }, selection: $prefs.appearance)
                }
            }
            .card()
            .staggered(2)

            SectionTitle("Språk")
            .staggered(3)
            VStack(spacing: 0) {
                ForEach(Array(SpeechModel.allCases.enumerated()), id: \.element) { i, m in
                    if i > 0 { CardDivider() }
                    LanguageRow(model: m, dictation: dictation)
                }
            }
            .card()
            .staggered(3)
            Text("Byt snabbt i menyraden – eller med ⌘1 och ⌘2 när menyn är öppen.")
                .font(.system(size: 12))
                .foregroundStyle(DS.Colors.muted)

            SectionTitle("Behörigheter och ljud")
            .staggered(4)
            VStack(spacing: 0) {
                CardRow(title: "Mikrofon", detail: dictation.micGranted ? mics.selectionName : "Mindtalk behöver få använda mikrofonen.") {
                    if dictation.micGranted { Button("Ändra") { showMicPicker = true }.buttonStyle(.soft) }
                    else { Button("Tillåt") { dictation.requestMicrophone() }.buttonStyle(.ink) }
                }
                CardDivider()
                CardRow(title: "Hjälpmedel") {
                    if dictation.accessibilityGranted { GrantedLabel() }
                    else { Button("Tillåt") { dictation.requestAccessibility() }.buttonStyle(.ink) }
                }
            }
            .card()
            .staggered(4)

            SectionTitle("Om Mindtalk")
            .staggered(5)
            AboutCard()
                .id("about")
                .staggered(5)
        }
        .onAppear { if Self.jumpToAbout { showAbout(proxy, after: 0.45) } }
        .onReceive(NotificationCenter.default.publisher(for: .showAbout)) { _ in showAbout(proxy, after: 0.1) }
        }
        .pageLayout()
        .onDisappear { dictation.stopPickingKey() }
        .sheet(isPresented: $showMicPicker) {
            MicrophonePicker(mics: mics) { showMicPicker = false }
        }
    }
}

/// The key in a white field with a pencil — click, then press any key.
private struct ShortcutField: View {
    @ObservedObject var dictation: Dictation

    var body: some View {
        Button {
            dictation.pickingKey ? dictation.stopPickingKey() : dictation.pickKey()
        } label: {
            HStack(spacing: 10) {
                if dictation.pickingKey {
                    Text("Tryck på en tangent …").font(.system(size: 13)).foregroundStyle(DS.Colors.muted)
                } else {
                    KeyChip(text: dictation.hotkey.chip)
                }
                Spacer(minLength: 16)
                Image(systemName: "pencil").foregroundStyle(DS.Colors.muted)
            }
            .padding(.leading, 8)
            .padding(.trailing, 12)
            .frame(width: 210, height: 38)
            .background(RoundedRectangle(cornerRadius: DS.Radius.control, style: .continuous).fill(DS.Colors.field))
            .overlay(RoundedRectangle(cornerRadius: DS.Radius.control, style: .continuous)
                .strokeBorder(dictation.pickingKey ? DS.Colors.ink : DS.Colors.fieldStroke, lineWidth: 1))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .disabled(!dictation.accessibilityGranted)
        .help(LocalizedStringKey(dictation.accessibilityGranted ? "Klicka och tryck sedan på den tangent du vill använda."
                                             : "Tillåt Hjälpmedel först."))
    }
}

// MARK: - Senaste

private struct RecentPage: View {
    @ObservedObject var dictation: Dictation

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            HStack(alignment: .firstTextBaseline) {
                PageHeader(title: "Senaste").staggered(0)
                Spacer()
                if !dictation.recent.isEmpty {
                    Button("Rensa") { dictation.clearRecent() }.buttonStyle(.soft)
                }
            }
            if dictation.recent.isEmpty {
                VStack(spacing: 10) {
                    Image(systemName: "waveform").font(.system(size: 28)).foregroundStyle(DS.Colors.faint)
                    Text("Inget dikterat än").font(.system(size: 15, weight: .semibold))
                    Text("Det du dikterar dyker upp här, så att du kan kopiera det igen om det hamnade fel.")
                        .font(.system(size: 13))
                        .foregroundStyle(DS.Colors.muted)
                        .multilineTextAlignment(.center)
                }
                .frame(maxWidth: .infinity)
                .padding(40)
                .card()
                .staggered(1)
            } else {
                VStack(spacing: 0) {
                    ForEach(Array(dictation.recent.enumerated()), id: \.element.id) { i, entry in
                        if i > 0 { CardDivider() }
                        RecentRow(entry: entry) { dictation.copy(entry) }
                    }
                }
                .card()
                .staggered(1)
                Text("Sparas bara på din Mac, de senaste 50. Rensa när du vill.")
                    .font(.system(size: 13))
                    .foregroundStyle(DS.Colors.muted)
            }
        }
        .pageLayout()
    }
}

private struct RecentRow: View {
    let entry: Dictation.Entry
    let copy: () -> Void
    @State private var copied = false

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 14) {
            VStack(alignment: .leading, spacing: 5) {
                Text(entry.text)
                    .font(.system(size: 14))
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
                Text(entry.date, format: .relative(presentation: .named, unitsStyle: .wide))
                    .font(.system(size: 12))
                    .foregroundStyle(DS.Colors.muted)
            }
            Button {
                copy()
                copied = true
                DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { copied = false }
            } label: {
                Image(systemName: copied ? "checkmark" : "doc.on.doc")
                    .contentTransition(.symbolEffect(.replace))
                    .frame(width: 18)
            }
            .buttonStyle(.borderless)
            .help("Kopiera")
            .accessibilityLabel("Kopiera")
        }
        .padding(.horizontal, 22)
        .padding(.vertical, 16)
    }
}

// MARK: - Språk

/// One language: use it, download it, or remove it.
private struct LanguageRow: View {
    let model: SpeechModel
    @ObservedObject var dictation: Dictation

    private var active: Bool { dictation.engine == model }

    var body: some View {
        HStack(spacing: 16) {
            ModelBadge(model: model, size: 13)
                .foregroundStyle(active ? DS.Colors.accent : .secondary)
                .frame(width: 38, height: 38)
                .background(Circle().fill(active ? DS.Colors.accent.opacity(0.12) : DS.Colors.chip))
            VStack(alignment: .leading, spacing: 3) {
                Text(model.title).font(.system(size: 14, weight: .semibold))
                Text(detail).font(.system(size: 13)).foregroundStyle(DS.Colors.muted)
                    .fixedSize(horizontal: false, vertical: true)
                if dictation.downloadErrors[model] == nil {
                    Text(String(localized: "\(model.modelName) av \(model.credit) · \(model.sizeText)"))
                        .font(.system(size: 11.5)).foregroundStyle(DS.Colors.muted)
                }
            }
            Spacer(minLength: 12)
            control
        }
        .padding(.horizontal, 22)
        .padding(.vertical, 16)
    }

    private var detail: String {
        dictation.downloadErrors[model] ?? model.pitch
    }

    @ViewBuilder private var control: some View {
        if let p = dictation.downloads[model] {
            HStack(spacing: 10) {
                ProgressView(value: p).frame(width: 90).tint(DS.Colors.accent)
                Text(p, format: .percent.precision(.fractionLength(0)))
                    .font(.system(size: 12))
                    .monospacedDigit()
                    .foregroundStyle(DS.Colors.muted)
                Button("Avbryt") { dictation.cancelDownload(model) }.buttonStyle(.soft)
            }
        } else if !dictation.installed.contains(model) {
            Button(LocalizedStringKey(dictation.downloadErrors[model] == nil ? "Ladda ned" : "Försök igen")) {
                dictation.downloadModel(model)
            }
            .buttonStyle(.ink)
        } else if active {
            HStack(spacing: 8) {
                if dictation.model == .loading { ProgressView().controlSize(.small) }
                Label("Används", systemImage: "checkmark.circle.fill")
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(DS.Colors.accent)
            }
        } else {
            HStack(spacing: 8) {
                if model.isOwnInstall {
                    Button {
                        dictation.removeModel(model)
                    } label: {
                        Image(systemName: "trash")
                    }
                    .buttonStyle(.plain)
                    .foregroundStyle(DS.Colors.muted)
                    .help("Ta bort \(model.modelName) (\(model.sizeText)) från din Mac")
                    .accessibilityLabel("Ta bort \(model.modelName)")
                }
                Button("Använd") { dictation.setEngine(model) }.buttonStyle(.soft)
            }
        }
    }
}

// MARK: - Om

/// Credits and licenses — what CC BY 4.0 asks of us, said plainly.
/// What happens to the text between the model and your cursor.
private struct TextCleanupCard: View {
    @ObservedObject var prefs: AppPrefs
    @State private var status = Polisher.status

    var body: some View {
        VStack(spacing: 0) {
            CardRow(title: "Ta bort tvekljud", detail: "Eh, öh och um försvinner ur texten.") {
                InkToggle(label: "Ta bort tvekljud", isOn: $prefs.removeFillers)
            }
            CardDivider()
            CardRow(title: "Röstkommandon", detail: "Säg ”ny rad” eller ”nytt stycke” – på engelska ”new line” och ”new paragraph”.") {
                InkToggle(label: "Röstkommandon", isOn: $prefs.voiceCommands)
            }
            CardDivider()
            CardRow(title: "Putsa med AI", detail: polishDetail) {
                if status == .notEnabled {
                    Button("Slå på") {
                        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.Siri-Settings.extension")!)
                    }
                    .buttonStyle(.soft)
                } else {
                    InkToggle(label: "Putsa med AI", isOn: $prefs.aiPolish)
                        .disabled(status != .available)
                        .opacity(status == .available ? 1 : 0.4)
                }
            }
        }
        .card()
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            status = Polisher.status
        }
    }

    private var polishDetail: String {
        switch status {
        case .available:
            return String(localized: "Rättar skiljetecken, upprepningar och självrättelser som ”nej, jag menar …”. Körs med Apple Intelligence på din Mac – inget skickas iväg.")
        case .notEnabled:
            return String(localized: "Kräver Apple Intelligence, som är avstängt på den här Macen.")
        case .notReady:
            return String(localized: "Apple Intelligence laddas ner. Det går att slå på när det är klart.")
        case .unsupported:
            return String(localized: "Kräver Apple Intelligence, som inte finns på den här Macen.")
        }
    }
}

extension SettingsPage {
    fileprivate func showAbout(_ proxy: ScrollViewProxy, after delay: Double) {
        Self.jumpToAbout = false
        DispatchQueue.main.asyncAfter(deadline: .now() + delay) {
            withAnimation(.spring(duration: 0.6)) { proxy.scrollTo("about", anchor: .top) }
        }
    }
}

private struct AboutCard: View {
    private static let ccBy = URL(string: "https://creativecommons.org/licenses/by/4.0/deed.sv")!
    private static let parakeet = URL(string: "https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3")!
    private static let fluidAudio = URL(string: "https://github.com/FluidInference/FluidAudio")!
    private static let apache = URL(string: "https://www.apache.org/licenses/LICENSE-2.0")!

    private var version: String {
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? ""
    }

    var body: some View {
        VStack(spacing: 0) {
            row(title: "Mindtalk \(version)", detail: "© 2026 Mindact Solutions AB") {
                Button("Visa introduktionen") { AppDelegate.showIntroduction() }
                    .buttonStyle(.soft)
            }
            ForEach(SpeechModel.allCases) { m in
                CardDivider()
                row(title: m.modelName,
                    detail: String(localized: "av \(m.credit), vidareutvecklad från NVIDIA Parakeet TDT 0.6B v3. Core ML-version av \(m.conversionCredit). Licens CC BY 4.0.")) {
                    links([("Modell", m.modelPage), ("Core ML", m.conversionPage), ("Licens", Self.ccBy)])
                }
            }
            CardDivider()
            row(title: "NVIDIA Parakeet TDT 0.6B v3",
                detail: "Grundmodellen båda bygger på, av NVIDIA. Licens CC BY 4.0.") {
                links([("Modell", Self.parakeet), ("Licens", Self.ccBy)])
            }
            CardDivider()
            row(title: "FluidAudio", detail: "Kör modellerna på Neural Engine. Av FluidInference, licens Apache 2.0.") {
                links([("Källkod", Self.fluidAudio), ("Licens", Self.apache)])
            }
        }
        .card()
    }

    private func row<Trailing: View>(title: String, detail: String, @ViewBuilder trailing: () -> Trailing) -> some View {
        HStack(alignment: .center, spacing: 16) {
            VStack(alignment: .leading, spacing: 3) {
                Text(LocalizedStringKey(title)).font(.system(size: 14, weight: .semibold))
                Text(LocalizedStringKey(detail)).font(.system(size: 12.5)).foregroundStyle(DS.Colors.muted)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 12)
            trailing()
        }
        .padding(.horizontal, 22)
        .padding(.vertical, 14)
    }

    private func links(_ items: [(String, URL)]) -> some View {
        HStack(spacing: 12) {
            ForEach(items, id: \.0) { title, url in
                Link(destination: url) {
                    HStack(spacing: 2) {
                        Text(LocalizedStringKey(title))
                        Image(systemName: "arrow.up.right").font(.system(size: 9, weight: .semibold))
                    }
                }
                .font(.system(size: 12, weight: .medium))
                .foregroundStyle(DS.Colors.accent)
            }
        }
    }
}
