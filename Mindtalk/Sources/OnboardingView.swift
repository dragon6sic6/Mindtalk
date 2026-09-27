import SwiftUI

// MARK: - First run
//
// Five steps — welcome → language (starts the download) → permissions (while it
// downloads) → your key → try it for real — on a slowly moving warm backdrop.
// Everything eases in, the logo speaks, the headline types itself, and the first
// dictation is celebrated. With Reduce Motion on, it's all calm and still.

struct OnboardingView: View {
    @ObservedObject var dictation: Dictation
    let finish: () -> Void

    enum Step: Int, CaseIterable { case welcome, language, permissions, key, tryIt }

    @State private var step: Step = OnboardingView.firstStep

    private static var firstStep: Step {
        #if DEBUG
        let names: [String: Step] = ["welcome": .welcome, "language": .language, "permissions": .permissions, "key": .key, "tryIt": .tryIt]
        if let name = Demo.onboardingStep, let step = names[name] { return step }
        #endif
        return .welcome
    }
    @State private var chosen: Set<SpeechModel> = [Settings.engine]
    @State private var forward = true

    var body: some View {
        ZStack {
            WarmGlow(intensity: step == .welcome ? 1 : 0.45)

            VStack(spacing: 0) {
                ProgressSegments(count: Step.allCases.count, current: step.rawValue)
                    .padding(.top, 20)

                ZStack {
                    stepView
                        .id(step)
                        .transition(.asymmetric(
                            insertion: .modifier(active: StepShift(offset: forward ? 40 : -40), identity: StepShift(offset: 0)),
                            removal: .modifier(active: StepShift(offset: forward ? -40 : 40), identity: StepShift(offset: 0))))
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .padding(.horizontal, 60)

                footer
                    .padding(.horizontal, 32)
                    .padding(.bottom, 26)
            }
        }
        .frame(width: 760, height: 640)
        // Centred whatever size the window ends up.
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    @ViewBuilder private var stepView: some View {
        switch step {
        case .welcome: WelcomeStep()
        case .language: LanguageStep(chosen: $chosen)
        case .permissions: PermissionsStep(dictation: dictation)
        case .key: KeyStep(dictation: dictation)
        case .tryIt: TryStep(dictation: dictation)
        }
    }

    // MARK: Footer

    private var footer: some View {
        HStack(spacing: 14) {
            if step != .welcome {
                Button { go(-1) } label: {
                    Label("Tillbaka", systemImage: "chevron.left")
                        .font(.system(size: 13, weight: .medium))
                        .foregroundStyle(DS.Colors.muted)
                }
                .buttonStyle(.plain)
                .transition(.opacity)
            }
            if step.rawValue >= Step.permissions.rawValue { DownloadStatus(dictation: dictation) }
            Spacer()
            GlowButton(title: primaryTitle, action: primary)
                .keyboardShortcut(step == .tryIt ? nil : .defaultAction)
        }
        .frame(height: 44)
        .animation(.snappy(duration: 0.25), value: step)
    }

    private var primaryTitle: String {
        switch step {
        case .welcome: return String(localized: "Kom igång")
        case .language: return chosen.allSatisfy(\.isInstalled) ? String(localized: "Fortsätt") : String(localized: "Ladda ned och fortsätt")
        case .tryIt: return String(localized: "Börja använda Mindtalk")
        default: return String(localized: "Fortsätt")
        }
    }

    private func primary() {
        switch step {
        case .language:
            // Swedish first if chosen — it becomes the active language.
            let order = SpeechModel.allCases.filter(chosen.contains)
            if let first = order.first { dictation.setEngine(first) }
            for m in order where !m.isInstalled { dictation.downloadModel(m) }
            go(1)
        case .tryIt:
            finish()
        default:
            go(1)
        }
    }

    private func go(_ delta: Int) {
        guard let next = Step(rawValue: step.rawValue + delta) else { return }
        forward = delta > 0
        withAnimation(.spring(duration: 0.55, bounce: 0.12)) { step = next }
    }
}

// MARK: - Step 1: Welcome

private struct WelcomeStep: View {
    var body: some View {
        VStack(spacing: 30) {
            LogoMark(size: 118)
                .staggered(0)
            Typewriter(text: String(localized: "Prata. Vi skriver."),
                       font: .system(size: 54, weight: .regular, design: .serif),
                       delay: 0.7)
            HStack(spacing: 10) {
                PromisePill(icon: "lock.fill", text: "Privat – allt stannar på din Mac")
                PromisePill(icon: "bolt.fill", text: "Text på ett ögonblick")
                PromisePill(icon: "character.book.closed.fill", text: "Lär sig dina ord")
            }
            .staggered(2, after: 1.7)
        }
        .padding(.bottom, 20)
    }
}

private struct PromisePill: View {
    let icon: String
    let text: String

    var body: some View {
        HStack(spacing: 7) {
            Image(systemName: icon)
                .font(.system(size: 11, weight: .semibold))
                .foregroundStyle(DS.Colors.accent)
            Text(LocalizedStringKey(text))
                .font(.system(size: 12.5, weight: .medium))
        }
        .padding(.horizontal, 13)
        .padding(.vertical, 8)
        .background(Capsule().fill(DS.Colors.field).shadow(color: .black.opacity(0.05), radius: 4, y: 1))
        .overlay(Capsule().strokeBorder(DS.Colors.fieldStroke, lineWidth: 1))
    }
}

// MARK: - Step 2: Language

private struct LanguageStep: View {
    @Binding var chosen: Set<SpeechModel>

    var body: some View {
        VStack(spacing: 26) {
            StepHeader(title: "Vilket språk pratar du?",
                       subtitle: "Välj ett eller båda. Modellen laddas ned en gång och fungerar sedan helt offline.")
                .staggered(0)
            HStack(spacing: 16) {
                ForEach(Array(SpeechModel.allCases.enumerated()), id: \.element) { i, m in
                    LanguageCard(model: m, selected: chosen.contains(m)) {
                        withAnimation(.spring(duration: 0.35, bounce: 0.3)) {
                            if chosen.contains(m) { if chosen.count > 1 { chosen.remove(m) } } else { chosen.insert(m) }
                        }
                    }
                    .staggered(i + 1)
                }
            }
            Text("Båda bygger på NVIDIA Parakeet och körs på din Macs Neural Engine. Du byter språk med ett klick i menyraden.")
                .font(.system(size: 12))
                .foregroundStyle(DS.Colors.muted)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 520)
                .staggered(3)
        }
    }
}

private struct LanguageCard: View {
    let model: SpeechModel
    let selected: Bool
    let toggle: () -> Void
    @State private var hovering = false

    private var recommended: Bool {
        model == .swedish && Locale.preferredLanguages.contains { $0.hasPrefix("sv") }
    }

    var body: some View {
        Button(action: toggle) {
            VStack(alignment: .leading, spacing: 0) {
                HStack(alignment: .top) {
                    ModelBadge(model: model, size: 26, serif: true)
                        .foregroundStyle(selected ? DS.Colors.onInk : DS.Colors.accent)
                        .frame(width: 58, height: 58)
                        .background(Circle().fill(selected ? AnyShapeStyle(DS.Gradients.accent) : AnyShapeStyle(DS.Colors.chip)))
                        .shadow(color: DS.Colors.accent.opacity(selected ? 0.35 : 0), radius: 12, y: 5)
                    Spacer()
                    Image(systemName: selected ? "checkmark.circle.fill" : "circle")
                        .font(.system(size: 22))
                        .foregroundStyle(selected ? AnyShapeStyle(DS.Colors.accent) : AnyShapeStyle(DS.Colors.muted))
                        .contentTransition(.symbolEffect(.replace))
                        .symbolEffect(.bounce, value: selected)
                }
                Spacer(minLength: 18)
                HStack(spacing: 8) {
                    Text(model.title).font(.system(size: 19, weight: .semibold))
                    if recommended {
                        Text("Rekommenderas")
                            .font(.system(size: 10.5, weight: .semibold))
                            .foregroundStyle(DS.Colors.accent)
                            .padding(.horizontal, 7).padding(.vertical, 2.5)
                            .background(Capsule().fill(DS.Colors.accent.opacity(0.12)))
                    }
                }
                Text(model.pitch)
                    .font(.system(size: 13))
                    .foregroundStyle(DS.Colors.muted)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.top, 5)
                HStack(spacing: 6) {
                    Image(systemName: model.isInstalled ? "checkmark.seal.fill" : "arrow.down.circle")
                    Text(model.isInstalled ? String(localized: "\(model.modelName) · redan installerad")
                                           : String(localized: "\(model.modelName) · \(model.sizeText)"))
                }
                .font(.system(size: 11.5))
                .foregroundStyle(DS.Colors.muted)
                .padding(.top, 14)
            }
            .padding(22)
            .frame(maxWidth: .infinity, alignment: .topLeading)
            .frame(height: 232)
            .raised(selected: selected)
            .overlay(
                // Hover without choosing: a hint of the ink edge.
                RoundedRectangle(cornerRadius: 20, style: .continuous)
                    .strokeBorder(DS.Colors.ink.opacity(hovering && !selected ? 0.25 : 0), lineWidth: 1)
            )
            .scaleEffect(hovering ? 1.012 : 1)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .onHover { h in withAnimation(.spring(duration: 0.3)) { hovering = h } }
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}

// MARK: - Step 3: Permissions

private struct PermissionsStep: View {
    @ObservedObject var dictation: Dictation

    var body: some View {
        VStack(spacing: 26) {
            StepHeader(title: "Två snabba behörigheter",
                       subtitle: "Så att Mindtalk kan höra dig och skriva där markören står.")
                .staggered(0)
            HStack(spacing: 16) {
                PermissionCard(icon: "mic.fill", title: "Mikrofon",
                               text: "Används bara medan du dikterar. Ljudet lämnar aldrig din Mac.",
                               granted: dictation.micGranted, button: "Tillåt") { dictation.requestMicrophone() }
                    .staggered(1)
                PermissionCard(icon: "hand.point.up.left.fill", title: "Hjälpmedel",
                               text: "Låter Mindtalk märka din tangent och klistra in texten. Den läser aldrig vad du skriver.",
                               granted: dictation.accessibilityGranted, button: "Öppna") { dictation.requestAccessibility() }
                    .staggered(2)
            }
            Group {
                if dictation.micGranted && dictation.accessibilityGranted {
                    GrantedLabel(text: "Allt klart – fortsätt när du vill.")
                        .transition(.scale(scale: 0.9).combined(with: .opacity))
                } else if !dictation.accessibilityGranted {
                    Label("Slå på Mindtalk under Integritet och säkerhet → Hjälpmedel. Kortet blir grönt av sig självt.",
                          systemImage: "info.circle")
                        .foregroundStyle(DS.Colors.muted)
                        .transition(.opacity)
                }
            }
            .font(.system(size: 12.5, weight: .medium))
            .multilineTextAlignment(.center)
            .staggered(3)
        }
        .animation(.spring(duration: 0.45, bounce: 0.3), value: dictation.micGranted)
        .animation(.spring(duration: 0.45, bounce: 0.3), value: dictation.accessibilityGranted)
        .onAppear { dictation.refreshPermissions() }
    }
}

private struct PermissionCard: View {
    let icon: String
    let title: String
    let text: String
    let granted: Bool
    let button: String
    let action: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Image(systemName: granted ? "checkmark" : icon)
                .font(.system(size: 20, weight: .semibold))
                .foregroundStyle(granted ? DS.Colors.onInk : DS.Colors.accent)
                .contentTransition(.symbolEffect(.replace))
                .frame(width: 52, height: 52)
                .background(Circle().fill(granted ? AnyShapeStyle(DS.Colors.ink) : AnyShapeStyle(DS.Colors.chip)))
                .shadow(color: .black.opacity(granted ? 0.18 : 0), radius: 10, y: 4)
                .symbolEffect(.bounce, value: granted)
            Text(LocalizedStringKey(title))
                .font(.system(size: 18, weight: .semibold))
                .padding(.top, 18)
            Text(LocalizedStringKey(text))
                .font(.system(size: 13))
                .foregroundStyle(DS.Colors.muted)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.top, 5)
            Spacer(minLength: 16)
            if granted {
                GrantedLabel()
                    .frame(height: 34)
                    .transition(.opacity)
            } else {
                Button(LocalizedStringKey(button), action: action)
                    .buttonStyle(.ink)
                    .frame(height: 34)
            }
        }
        .padding(22)
        .frame(maxWidth: .infinity, alignment: .topLeading)
        .frame(height: 236)
        .raised()
    }
}

// MARK: - Step 4: Your key

private struct KeyStep: View {
    @ObservedObject var dictation: Dictation

    private var recording: Bool { dictation.phase == .recording }
    private var hearing: Bool { recording && (dictation.levels.suffix(8).max() ?? 0) > 0.08 }

    var body: some View {
        VStack(spacing: 26) {
            StepHeader(title: "Din tangent", subtitle: "Håll in den var som helst, prata och släpp. Prova nu!")
                .staggered(0)
            VStack(spacing: 18) {
                Keycap(text: dictation.pickingKey ? "…" : dictation.hotkey.chip, pressed: recording)
                // At rest the wave is a calm dotted line; it wakes when you hold the key.
                Waveform(levels: dictation.levels, gap: 3, dimmed: dictation.phase == .transcribing)
                    .frame(width: 240, height: 36)
                    .opacity(recording || dictation.phase == .transcribing ? 1 : 0.55)
                Group {
                    if hearing {
                        Label("Vi hör dig!", systemImage: "ear")
                            .foregroundStyle(DS.Colors.accent)
                    } else if recording {
                        Text("Säg något …").foregroundStyle(DS.Colors.muted)
                    } else if dictation.phase == .transcribing {
                        Text("Skriver …").foregroundStyle(DS.Colors.muted)
                    } else {
                        Text("Håll in \(dictation.hotkey.inlineName) och säg något.").foregroundStyle(DS.Colors.muted)
                    }
                }
                .font(.system(size: 13, weight: .medium))
                .frame(height: 18)
                .contentTransition(.opacity)
                .animation(.easeOut(duration: 0.2), value: hearing)
                .animation(.easeOut(duration: 0.2), value: recording)
            }
            .staggered(1)
            HStack(spacing: 12) {
                KeyHint(icon: "hand.tap.fill", title: "Håll in", text: "för något kort")
                KeyHint(icon: "lock.fill", title: "Dubbeltryck", text: "för att prata fritt")
                KeyHint(icon: "escape", title: "Esc", text: "avbryter")
            }
            .staggered(2)
            Button(dictation.pickingKey ? String(localized: "Tryck på en tangent …") : String(localized: "Välj en annan tangent")) {
                dictation.pickingKey ? dictation.stopPickingKey() : dictation.pickKey()
            }
            .buttonStyle(.soft)
            .disabled(!dictation.accessibilityGranted)
            .help(dictation.accessibilityGranted ? "" : String(localized: "Tillåt Hjälpmedel i förra steget för att välja tangent."))
            .staggered(3)
        }
        .onDisappear { dictation.stopPickingKey() }
    }
}

private struct KeyHint: View {
    let icon: String
    let title: String
    let text: String

    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: icon)
                .font(.system(size: 13))
                .foregroundStyle(DS.Colors.accent)
                .frame(width: 30, height: 30)
                .background(Circle().fill(DS.Colors.chip))
            VStack(alignment: .leading, spacing: 1) {
                Text(LocalizedStringKey(title)).font(.system(size: 13, weight: .semibold))
                Text(LocalizedStringKey(text)).font(.system(size: 11.5)).foregroundStyle(DS.Colors.muted)
            }
            Spacer(minLength: 0)
        }
        .padding(12)
        .frame(width: 180)
        .raised(cornerRadius: 14)
    }
}

/// A big physical-looking key that sinks while you hold it.
private struct Keycap: View {
    let text: String
    let pressed: Bool

    var body: some View {
        ZStack {
            // The key's side, visible below the top face.
            RoundedRectangle(cornerRadius: 20, style: .continuous)
                .fill(DS.Colors.fieldStroke)
                .offset(y: 7)
            RoundedRectangle(cornerRadius: 20, style: .continuous)
                .fill(LinearGradient(colors: [DS.Colors.field, DS.Colors.card], startPoint: .top, endPoint: .bottom))
                .overlay(RoundedRectangle(cornerRadius: 20, style: .continuous)
                    .strokeBorder(pressed ? AnyShapeStyle(DS.Gradients.accent) : AnyShapeStyle(DS.Colors.fieldStroke), lineWidth: 1.5))
                .overlay(
                    Text(text)
                        .font(.system(size: 30, weight: .medium, design: .rounded))
                        .foregroundStyle(pressed ? DS.Colors.accent : .primary)
                )
                .offset(y: pressed ? 6 : 0)
        }
        .frame(width: 190, height: 96)
        .shadow(color: pressed ? DS.Colors.accent.opacity(0.35) : .black.opacity(0.12), radius: pressed ? 22 : 14, y: pressed ? 4 : 10)
        .animation(.spring(duration: 0.2, bounce: 0.35), value: pressed)
        .accessibilityLabel(String(localized: "Din tangent: \(text)"))
    }
}

// MARK: - Step 5: Try it

private struct TryStep: View {
    @ObservedObject var dictation: Dictation
    @State private var practice = ""
    @State private var celebrated = false
    @State private var confetti = 0

    var body: some View {
        ZStack {
            VStack(spacing: 22) {
                StepHeader(title: celebrated ? String(localized: "Snyggt. Så enkelt är det.") : String(localized: "Prova på riktigt"),
                           subtitle: celebrated ? String(localized: "Mindtalk fungerar i alla appar – mejl, Slack, dokument, var du vill.")
                                                : String(localized: "Klicka i rutan, håll in \(dictation.hotkey.inlineName) och säg något."))
                    .staggered(0)
                    .animation(.easeOut(duration: 0.3), value: celebrated)
                ZStack(alignment: .topLeading) {
                    TextEditor(text: $practice)
                        .font(.system(size: 19))
                        .scrollContentBackground(.hidden)
                        .padding(16)
                        .frame(height: celebrated ? 110 : 160)
                        .background(RoundedRectangle(cornerRadius: 18, style: .continuous).fill(DS.Colors.field))
                        .overlay(RoundedRectangle(cornerRadius: 18, style: .continuous)
                            .strokeBorder(practice.isEmpty ? AnyShapeStyle(DS.Colors.fieldStroke) : AnyShapeStyle(DS.Colors.ink),
                                          lineWidth: practice.isEmpty ? 1 : 2))
                        .shadow(color: .black.opacity(0.05), radius: 16, y: 8)
                    if practice.isEmpty {
                        Text("”Hej! Det här är min första diktering.”")
                            .font(.system(size: 19))
                            .foregroundStyle(DS.Colors.muted)
                            .padding(.horizontal, 21).padding(.vertical, 16)
                            .allowsHitTesting(false)
                    }
                }
                .staggered(1)
                if celebrated {
                    Summary(dictation: dictation)
                        .transition(.move(edge: .bottom).combined(with: .opacity))
                } else {
                    ModelWait(dictation: dictation).staggered(2)
                }
            }
            Confetti(trigger: confetti)
                .allowsHitTesting(false)
        }
        .animation(.spring(duration: 0.55, bounce: 0.2), value: celebrated)
        .onChange(of: practice) { _, text in
            guard !celebrated, !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
            celebrated = true
            confetti += 1
            Cue.celebrate()
        }
    }
}

/// What you've set up, and where Mindtalk lives.
private struct Summary: View {
    @ObservedObject var dictation: Dictation
    @State private var nudge = false

    var body: some View {
        HStack(spacing: 0) {
            item(label: "Din tangent") { KeyChip(text: dictation.hotkey.chip) }
            divider
            item(label: "Språk") { Text(dictation.engine.title).font(.system(size: 14, weight: .semibold)) }
            divider
            item(label: "Hittas i menyraden") {
                HStack(spacing: 8) {
                    MarkGlyph().frame(width: 20, height: 16)
                    Image(systemName: "arrow.up.right")
                        .font(.system(size: 12, weight: .bold))
                        .foregroundStyle(DS.Colors.accent)
                        .offset(x: nudge ? 3 : 0, y: nudge ? -3 : 0)
                }
            }
        }
        .padding(.vertical, 16)
        .raised(cornerRadius: 18)
        .onAppear {
            withAnimation(.easeInOut(duration: 0.8).repeatForever(autoreverses: true)) { nudge = true }
        }
    }

    private var divider: some View {
        Rectangle().fill(DS.Colors.divider).frame(width: 1, height: 38)
    }

    private func item<V: View>(label: String, @ViewBuilder value: () -> V) -> some View {
        VStack(spacing: 7) {
            Text(LocalizedStringKey(label)).font(.system(size: 11.5, weight: .medium)).foregroundStyle(DS.Colors.muted)
            value()
        }
        .frame(maxWidth: .infinity)
    }
}

private struct ModelWait: View {
    @ObservedObject var dictation: Dictation

    var body: some View {
        Group {
            switch dictation.model {
            case .downloading(let p):
                HStack(spacing: 10) {
                    ProgressRing(progress: p).frame(width: 16, height: 16)
                    Text("\(dictation.engine.title) laddas ned – \(Int(p * 100)) %. Du kan prova när den är klar.")
                }
            case .loading:
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text("Gör modellen redo för din Mac – första gången tar det upp till en minut.")
                }
            case .failed(let message):
                Label(message, systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange)
            default:
                Label("Tips: dubbeltryck på tangenten för att prata fritt, tryck igen när du är klar.", systemImage: "lightbulb")
            }
        }
        .font(.system(size: 12.5))
        .foregroundStyle(DS.Colors.muted)
    }
}

// MARK: - Shared pieces

private struct StepHeader: View {
    let title: String
    let subtitle: String

    var body: some View {
        VStack(spacing: 10) {
            Text(LocalizedStringKey(title))
                .font(.system(size: 38, weight: .regular, design: .serif))
                .contentTransition(.opacity)
            Text(LocalizedStringKey(subtitle))
                .font(.system(size: 15.5))
                .foregroundStyle(DS.Colors.muted)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
                .contentTransition(.opacity)
        }
        .frame(maxWidth: 560)
    }
}

/// The ink primary button, with an arrow that leans forward on hover.
private struct GlowButton: View {
    let title: String
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 8) {
                Text(title)
                Image(systemName: "arrow.right")
                    .font(.system(size: 12, weight: .bold))
                    .offset(x: hovering ? 3 : 0)
            }
            .font(.system(size: 14, weight: .semibold))
            .foregroundStyle(DS.Colors.onInk)
            .padding(.horizontal, 22)
            .frame(height: 42)
            .background(Capsule().fill(DS.Colors.ink))
            .shadow(color: DS.Colors.accent.opacity(hovering ? 0.45 : 0.2), radius: hovering ? 18 : 10, y: 6)
            .scaleEffect(hovering ? 1.02 : 1)
            .contentShape(Capsule())
        }
        .buttonStyle(PressableStyle())
        .onHover { h in withAnimation(.spring(duration: 0.3)) { hovering = h } }
    }
}

private struct PressableStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .scaleEffect(configuration.isPressed ? 0.97 : 1)
            .animation(.spring(duration: 0.2), value: configuration.isPressed)
    }
}

/// Thin segments across the top: done, current, still to come.
private struct ProgressSegments: View {
    let count: Int
    let current: Int

    var body: some View {
        HStack(spacing: 6) {
            ForEach(0..<count, id: \.self) { i in
                // Done: half ink. Now: full ink, wider. Still to come: faint but visible.
                Capsule()
                    .fill(DS.Colors.ink.opacity(i == current ? 1 : i < current ? 0.45 : 0.16))
                    .frame(width: i == current ? 34 : 18, height: 4)
            }
        }
        .animation(.spring(duration: 0.5, bounce: 0.2), value: current)
        .accessibilityElement()
        .accessibilityLabel(String(localized: "Steg \(current + 1) av \(count)"))
    }
}

/// Downloads in progress, bottom-left in the footer.
private struct DownloadStatus: View {
    @ObservedObject var dictation: Dictation

    var body: some View {
        let active = SpeechModel.allCases.compactMap { m in dictation.downloads[m].map { (m, $0) } }
        if !active.isEmpty {
            HStack(spacing: 14) {
                ForEach(active, id: \.0) { m, p in
                    HStack(spacing: 7) {
                        ProgressRing(progress: p).frame(width: 16, height: 16)
                        Text("\(m.title) \(Int(p * 100)) %")
                            .font(.system(size: 11.5, weight: .medium))
                            .monospacedDigit()
                            .foregroundStyle(DS.Colors.muted)
                    }
                }
            }
            .padding(.leading, 8)
            .transition(.opacity)
        }
    }
}

private struct ProgressRing: View {
    let progress: Double

    var body: some View {
        ZStack {
            Circle().stroke(DS.Colors.divider, lineWidth: 2.5)
            Circle()
                .trim(from: 0, to: max(0.02, progress))
                .stroke(DS.Gradients.accent, style: StrokeStyle(lineWidth: 2.5, lineCap: .round))
                .rotationEffect(.degrees(-90))
                .animation(.easeOut(duration: 0.3), value: progress)
        }
    }
}

// MARK: - The living logo

/// The app icon, alive — the wave rolls and the cursor blinks. It floats gently.
private struct LogoMark: View {
    let size: CGFloat
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        TimelineView(.animation(paused: reduceMotion)) { context in
            let t = reduceMotion ? 0 : context.date.timeIntervalSinceReferenceDate
            LogoTile(size: size, t: t, animated: !reduceMotion)
                .shadow(color: .black.opacity(0.35), radius: 30, y: 14)
                .offset(y: reduceMotion ? 0 : CGFloat(sin(t * 1.2)) * 4)
        }
        .accessibilityHidden(true)
    }
}

/// Types its text one character at a time, with a blinking cursor at the end.
private struct Typewriter: View {
    let text: String
    let font: Font
    var delay: Double = 0
    @State private var shown = 0
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        ZStack(alignment: .leading) {
            // The full line, invisible, keeps the layout still while typing.
            Text(text).font(font).opacity(0)
            HStack(spacing: 3) {
                Text(String(text.prefix(shown))).font(font)
                TimelineView(.periodic(from: .now, by: 0.5)) { context in
                    let on = shown < text.count || Int(context.date.timeIntervalSinceReferenceDate * 2) % 2 == 0
                    RoundedRectangle(cornerRadius: 1.5)
                        .fill(DS.Gradients.accent)
                        .frame(width: 3, height: 46)
                        .opacity(on ? 1 : 0)
                }
            }
        }
        .accessibilityElement()
        .accessibilityLabel(text)
        .task {
            if reduceMotion { shown = text.count; return }
            try? await Task.sleep(for: .seconds(delay))
            let chars = Array(text)
            for i in chars.indices {
                shown = i + 1
                // A small pause after a full stop, like someone speaking.
                try? await Task.sleep(for: .milliseconds(chars[i] == "." ? 260 : Int.random(in: 45...85)))
            }
        }
    }
}

// MARK: - Step transitions & entrances

/// Steps slide a little and come into focus.
private struct StepShift: ViewModifier {
    let offset: CGFloat
    func body(content: Content) -> some View {
        content
            .offset(x: offset)
            .opacity(offset == 0 ? 1 : 0)
            .blur(radius: offset == 0 ? 0 : 8)
    }
}

// MARK: - Confetti

/// A short burst of paper in black, white and greys, falling with a little spin.
private struct Confetti: View {
    let trigger: Int
    @State private var start: Date?
    @State private var pieces: [Piece] = []
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    struct Piece {
        let x: Double, vx: Double, vy: Double, spin: Double, size: Double, color: Color, round: Bool, delay: Double
    }

    var body: some View {
        TimelineView(.animation(paused: start == nil)) { context in
            Canvas { ctx, size in
                guard let start else { return }
                let now = context.date.timeIntervalSince(start)
                for p in pieces {
                    let t = now - p.delay
                    guard t > 0 else { continue }
                    // A little air resistance on the sideways drift, gravity downwards.
                    let x = p.x * size.width + p.vx * (1 - exp(-1.6 * t)) / 1.6
                    let y = size.height + 10 + p.vy * t + 620 * t * t
                    guard y < size.height + 40 else { continue }
                    var c = ctx
                    c.opacity = max(0, min(1, 3.4 - now))
                    c.translateBy(x: x, y: y)
                    c.rotate(by: .radians(p.spin * t))
                    let r = CGRect(x: -p.size / 2, y: -p.size / 4, width: p.size, height: p.round ? p.size : p.size / 2)
                    c.fill(p.round ? Path(ellipseIn: r) : Path(roundedRect: r, cornerRadius: 1.5), with: .color(p.color))
                }
            }
        }
        .onChange(of: trigger) { _, _ in
            guard !reduceMotion else { return }
            let colors: [Color] = [DS.Colors.ink, Color(white: 0.55), Color(white: 0.75), Color(white: 0.35), Color(white: 0.9)]
            // Two cannons in the bottom corners, aimed up and inwards.
            pieces = (0..<160).map { i in
                let left = i % 2 == 0
                return Piece(x: left ? 0.04 : 0.96,
                             vx: (left ? 1 : -1) * Double.random(in: 180...620),
                             vy: Double.random(in: -1060 ... -620),
                             spin: Double.random(in: -10...10), size: Double.random(in: 6...12),
                             color: colors.randomElement()!, round: Bool.random() && Bool.random(),
                             delay: Double.random(in: 0...0.25))
            }
            start = Date()
            DispatchQueue.main.asyncAfter(deadline: .now() + 3.4) { start = nil }
        }
    }
}
