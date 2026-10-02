import AppKit
import ApplicationServices
import Combine
import os

// MARK: - Dictation
//
// Hold the dictation key, speak, let go — the text is pasted where the cursor is.
// A quick tap instead starts hands-free dictation: tap again to finish. Esc cancels.
// Audio stays in memory and is thrown away right after transcription.
//
// Modifier keys double as shortcut keys (right ⌥ is "Alt Gr" on Swedish keyboards:
// @ £ $ [ ] { } |), so a press where another key joins in counts as a shortcut and
// is dropped silently.

@MainActor
final class Dictation: ObservableObject {
    static let shared = Dictation()

    enum Phase: Equatable {
        case idle
        case recording
        case transcribing
        case failed(String)
        /// Nowhere to type it: it's in the clipboard instead.
        case copied
    }

    enum ModelState: Equatable {
        case missing
        case downloading(Double)
        case loading
        case ready
        case failed(String)
    }

    private static let log = Logger(subsystem: "ai.mindact.mindtalk", category: "Dictation")

    @Published private(set) var phase: Phase = .idle {
        didSet { if phase != oldValue { Self.log.info("phase \(String(describing: self.phase), privacy: .public)") } }
    }
    @Published private(set) var handsFree = false {
        didSet { if handsFree && !oldValue { Self.log.info("locked (hands-free)") } }
    }
    @Published private(set) var level: Float = 0
    /// The last second or two of mic levels, oldest first — the HUD's waveform.
    @Published private(set) var levels = [Float](repeating: 0, count: Dictation.levelCount)
    static let levelCount = 24
    private var lastLevelPush = Date.distantPast

    #if DEBUG
    /// The HUD mid-sentence, for screenshots: a recording with a speaking waveform.
    func demoRecording() {
        phase = .recording
        recordingStarted = Date().addingTimeInterval(-7)
        let voice: [Float] = [0.05, 0.12, 0.30, 0.52, 0.41, 0.66, 0.35, 0.22, 0.48, 0.71, 0.58, 0.33,
                              0.18, 0.40, 0.62, 0.50, 0.28, 0.45, 0.69, 0.54, 0.31, 0.47, 0.60, 0.38]
        levels = voice
        handsFree = CommandLine.arguments.contains("--demo-locked")
        showsLanguage = CommandLine.arguments.contains("--demo-language")
        if CommandLine.arguments.contains("--demo-transcribing") { phase = .transcribing }
        hud.show()
    }
    #endif

    private func push(level value: Float) {
        // Late buffers after stop() mustn't refill the wave.
        guard phase == .recording else { return }
        peakLevel = max(peakLevel, value)
        level = value
        guard Date().timeIntervalSince(lastLevelPush) > 0.05 else { return }
        lastLevelPush = Date()
        levels.removeFirst()
        levels.append(value)
    }
    @Published private(set) var engine = Settings.engine
    @Published private(set) var model: ModelState = Settings.engine.isInstalled ? .loading : .missing
    @Published private(set) var installed = Set(SpeechModel.allCases.filter(\.isInstalled))
    /// Models downloading right now, with progress 0…1.
    @Published private(set) var downloads: [SpeechModel: Double] = [:]
    /// When each running download started — for "about 2 min left".
    private(set) var downloadStarted: [SpeechModel: Date] = [:]
    @Published private(set) var downloadErrors: [SpeechModel: String] = [:]
    private var downloadTasks: [SpeechModel: Task<Void, Never>] = [:]
    private var loadGeneration = 0
    @Published private(set) var hotkey = Settings.hotkey
    @Published private(set) var pickingKey = false
    @Published private(set) var accessibilityGranted = AXIsProcessTrusted()
    @Published private(set) var micGranted = Recorder.micAuthorized
    /// Recent dictations — in memory only, so a paste that landed nowhere can be copied.
    @Published private(set) var recent: [Entry] = Dictation.loadRecent() {
        didSet { saveRecent() }
    }
    /// When the current recording started (for the HUD's timer).
    @Published private(set) var recordingStarted: Date?

    struct Entry: Identifiable, Equatable, Codable {
        var id = UUID()
        let text: String
        let date: Date
    }

    // MARK: Recent, kept on this Mac

    /// The last dictations, in Application Support — so a paste that landed in the
    /// wrong place can be copied again, even after a restart. Never leaves the Mac.
    private static var recentFile: URL {
        SpeechModel.rootDirectory.deletingLastPathComponent().appendingPathComponent("Recent.json")
    }
    private static let recentLimit = 50
    private static let saveQueue = DispatchQueue(label: "ai.mindact.mindtalk.recent", qos: .utility)

    private static func loadRecent() -> [Entry] {
        #if DEBUG
        if Demo.on { return Demo.recent }
        #endif
        guard let data = try? Data(contentsOf: recentFile),
              let entries = try? JSONDecoder().decode([Entry].self, from: data) else { return [] }
        return Array(entries.prefix(recentLimit))
    }

    private func saveRecent() {
        #if DEBUG
        if Demo.on { return }
        #endif
        let file = Self.recentFile
        let entries = recent
        // One write at a time, in order — a "Rensa" right after a dictation stays cleared.
        Self.saveQueue.async {
            let fm = FileManager.default
            if entries.isEmpty { try? fm.removeItem(at: file); return }
            let folder = file.deletingLastPathComponent()
            try? fm.createDirectory(at: folder, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            try? fm.setAttributes([.posixPermissions: 0o700], ofItemAtPath: folder.path)
            try? JSONEncoder().encode(entries).write(to: file, options: [.atomic])
            try? fm.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)   // only you
        }
    }

    var isReady: Bool {
        #if DEBUG
        // Screenshots of the panel from an unsigned build, which has no permissions.
        if Demo.on { return true }
        #endif
        return model == .ready && accessibilityGranted && micGranted
    }

    /// Something the user has to do or wait out: a permission, a download, a
    /// failure. A model that's merely starting (e.g. after switching language)
    /// isn't — it's ready in a moment.
    var needsSetup: Bool {
        #if DEBUG
        if Demo.on { return false }
        #endif
        guard accessibilityGranted && micGranted else { return true }
        switch model {
        case .ready, .loading: return false
        case .missing, .downloading, .failed: return true
        }
    }

    /// What stands between you and dictating right now, most important first —
    /// said plainly, with one action that fixes it.
    enum SetupIssue: Equatable {
        case accessibility, microphone, modelMissing, modelDownloading(Double), modelFailed

        @MainActor var title: String {
            let model = Dictation.shared.engine.title.lowercased()
            switch self {
            case .accessibility: return String(localized: "Slå på Hjälpmedel för Mindtalk")
            case .microphone: return String(localized: "Tillåt mikrofonen")
            case .modelMissing: return String(localized: "Ladda ned språkmodellen (\(model))")
            case .modelDownloading(let p): return String(localized: "Laddar ned språkmodellen – \(Int(p * 100)) %")
            case .modelFailed: return String(localized: "Språkmodellen startade inte – försök igen")
            }
        }

        /// Why it's needed, for a tooltip.
        var why: String {
            switch self {
            case .accessibility:
                return String(localized: "Behövs för att känna av tangenten och skriva in texten. Står Mindtalk redan i listan i Systeminställningar: slå av och på den.")
            case .microphone: return String(localized: "Behövs för att höra vad du säger.")
            case .modelMissing, .modelDownloading, .modelFailed:
                return String(localized: "Taligenkänningen körs på din Mac och behöver modellen en gång.")
            }
        }
    }

    var setupIssue: SetupIssue? {
        if !accessibilityGranted { return .accessibility }
        if !micGranted { return .microphone }
        switch model {
        case .missing: return .modelMissing
        case .downloading(let p): return .modelDownloading(p)
        case .failed: return .modelFailed
        case .ready, .loading: return nil
        }
    }

    /// Does what the issue says.
    func fix(_ issue: SetupIssue) {
        switch issue {
        case .accessibility: requestAccessibility()
        case .microphone: requestMicrophone()
        case .modelMissing, .modelFailed: downloadModel()
        case .modelDownloading: AppDelegate.showWindow(page: .settings)
        }
    }

    @Published private(set) var mode = Settings.mode

    /// A press shorter than this is a tap, not a hold.
    private let tapThreshold: TimeInterval = 0.3
    /// How long after a tap a second one counts as a double-tap.
    private let doubleTapWindow: TimeInterval = 0.35
    private var awaitingSecondTap = false
    private var doubleTapTimer: DispatchWorkItem?
    private var keyIsDown = false
    private var secondTapDown = false
    private var startCuePlayed = false
    private let recorder = Recorder()
    private let hud = HUD()
    private var pressedAt: Date?
    private var ignoreNextRelease = false
    private var permissionPoll: Timer?
    /// The language a recording was started in — it's transcribed in that one,
    /// even if you switch meanwhile (the switch waits until it's done).
    private var recordingEngine: SpeechModel?
    private var engineSwitchPending = false
    /// Loudest level heard this recording, to tell "silence" from "nothing understood".
    private var peakLevel: Float = 0
    /// A problem to report (model missing, no mic…) once the press is clearly
    /// meant — never for ⌥2 → @ or a stray tap.
    private var pendingProblem: (() -> Void)?
    private var watchdog: Timer?
    private var lastReport: Date?
    /// A forgotten hands-free dictation finishes on its own.
    private static let maxRecording: TimeInterval = 10 * 60
    /// No audio at all for this long means the mic is gone — AirPods take about a
    /// second to switch to their headset mode, so not sooner.
    private static let deadMic: TimeInterval = 2.5

    private init() {
        recorder.onLevel = { [weak self] value in
            Task { @MainActor in self?.push(level: value) }
        }
        let keys = KeyListener.shared
        keys.onPress = { [weak self] in self?.keyPressed() }
        keys.onRelease = { [weak self] in self?.keyReleased() }
        keys.onChord = { [weak self] in
            guard let self else { return }
            self.pendingProblem = nil
            guard self.phase == .recording, !self.handsFree else { return }
            self.cancel()
        }
        recorder.onInterrupted = { [weak self] in
            // The mic went away (AirPods off, device switched): keep what was said.
            Task { @MainActor in if self?.phase == .recording { self?.finish() } }
        }
        let workspace = NSWorkspace.shared.notificationCenter
        workspace.addObserver(forName: NSWorkspace.willSleepNotification, object: nil, queue: .main) { _ in
            MainActor.assumeIsolated { if Dictation.shared.phase == .recording { Dictation.shared.cancel() } }
        }
        workspace.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { _ in
            MainActor.assumeIsolated { KeyListener.shared.resync() }
        }
        keys.onSpaceWhileHeld = { [weak self] in self?.spaceWhileHeld() ?? false }
        keys.onPasteLast = { [weak self] in self?.pasteLast() }
        keys.onEscape = { [weak self] in
            guard let self else { return false }
            if self.phase == .recording {
                self.cancel()
                return true
            }
            // Esc also closes the menu bar panel.
            return AppDelegate.closeStatusPanelIfShown()
        }
    }

    // MARK: Startup & permissions

    func start() {
        refreshPermissions()
        loadModel()
    }

    func refreshPermissions() {
        micGranted = Recorder.micAuthorized
        accessibilityGranted = AXIsProcessTrusted()
        if accessibilityGranted, KeyListener.shared.start(), micGranted {
            permissionPoll?.invalidate(); permissionPoll = nil; return
        }
        // macOS doesn't announce newly granted permissions — check once a second until it does.
        guard permissionPoll == nil else { return }
        permissionPoll = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { _ in
            Task { @MainActor in Dictation.shared.refreshPermissions() }
        }
    }

    func requestMicrophone() {
        Task {
            _ = await Recorder.requestMicrophone()
            refreshPermissions()
            if !micGranted { openPrivacySettings("Privacy_Microphone") }
        }
    }

    func requestAccessibility() {
        // Value of kAXTrustedCheckOptionPrompt.
        _ = AXIsProcessTrustedWithOptions(["AXTrustedCheckOptionPrompt": true] as CFDictionary)
        refreshPermissions()
        // macOS only asks the first time. If Mindtalk is already in the list (e.g. after
        // an update with a new signature) nothing appears — so open the list itself.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.8) { [weak self] in
            guard let self, !self.accessibilityGranted else { return }
            self.openPrivacySettings("Privacy_Accessibility")
        }
    }

    func openPrivacySettings(_ pane: String) {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?\(pane)") {
            NSWorkspace.shared.open(url)
        }
    }

    // MARK: Models
    //
    // `engine` is the language you dictate in; `model` is how that engine is doing.
    // Any model can be downloading in the background while you use another.

    func setEngine(_ newEngine: SpeechModel) {
        guard newEngine != engine else { return }
        engine = newEngine
        Settings.engine = newEngine
        // Mid-dictation the old model finishes the job first.
        if phase == .recording || phase == .transcribing { engineSwitchPending = true; return }
        if newEngine.isInstalled { loadModel() } else { refreshModels() }
    }

    /// Recomputes what's installed and the active engine's state.
    func refreshModels() {
        installed = Set(SpeechModel.allCases.filter(\.isInstalled))
        if let p = downloads[engine] { model = .downloading(p); return }
        if !installed.contains(engine) {
            model = downloadErrors[engine].map { .failed($0) } ?? .missing
        } else if case .missing = model {
            loadModel()
        } else if case .downloading = model {
            loadModel()
        }
    }

    private func loadModel() {
        installed = Set(SpeechModel.allCases.filter(\.isInstalled))
        #if DEBUG
        if Demo.on { model = .ready; return }   // screenshots: no model needed
        #endif
        guard engine.isInstalled else { return refreshModels() }
        model = .loading
        loadGeneration += 1
        let generation = loadGeneration
        let target = engine
        Task {
            do {
                try await SpeechEngine.shared.load(target)
                if generation == loadGeneration { model = .ready }
            } catch {
                if generation == loadGeneration { model = .failed(error.localizedDescription) }
            }
        }
    }

    /// Languages to switch to once their download is done (asked for from the menu bar).
    private var useWhenReady: Set<SpeechModel> = []

    func downloadModel(_ which: SpeechModel? = nil, thenUse: Bool = false) {
        let m = which ?? engine
        if thenUse { useWhenReady.insert(m) }
        guard downloadTasks[m] == nil, !m.isInstalled else { return }
        downloads[m] = 0
        downloadStarted[m] = Date()
        downloadErrors[m] = nil
        refreshModels()
        downloadTasks[m] = Task {
            do {
                try await m.install { p in
                    Task { @MainActor in
                        let d = Dictation.shared
                        guard d.downloads[m] != nil else { return }
                        d.downloads[m] = p
                        if d.engine == m { d.model = .downloading(p) }
                    }
                }
                downloadTasks[m] = nil
                downloads[m] = nil
                downloadStarted[m] = nil
                if useWhenReady.remove(m) != nil, m != engine { setEngine(m) }
                else if m == engine { loadModel() } else { refreshModels() }
            } catch {
                downloadTasks[m] = nil
                downloads[m] = nil
                downloadStarted[m] = nil
                useWhenReady.remove(m)
                let cancelled = error is CancellationError || (error as? URLError)?.code == .cancelled
                if !cancelled { downloadErrors[m] = Self.plainWords(for: error) }
                refreshModels()
            }
        }
    }

    /// A download error in words anyone understands.
    static func plainWords(for error: Error) -> String {
        if let url = error as? URLError {
            switch url.code {
            case .notConnectedToInternet, .networkConnectionLost, .dataNotAllowed, .internationalRoamingOff:
                return String(localized: "Internetanslutningen bröts. Kontrollera nätverket och försök igen.")
            case .timedOut, .cannotConnectToHost, .cannotFindHost, .dnsLookupFailed:
                return String(localized: "Kunde inte nå servern just nu. Försök igen om en stund.")
            default: break
            }
        }
        let ns = error as NSError
        if ns.domain == NSCocoaErrorDomain, ns.code == NSFileWriteOutOfSpaceError {
            return String(localized: "Det finns inte tillräckligt med ledigt utrymme på din Mac.")
        }
        return String(localized: "Något gick fel under nedladdningen. Försök igen.")
    }

    func cancelDownload(_ which: SpeechModel? = nil) {
        downloadTasks[which ?? engine]?.cancel()
    }

    /// Deletes Mindtalk's copy of a model to free up space.
    func removeModel(_ m: SpeechModel) {
        guard m.isOwnInstall else { return }
        Task {
            await SpeechEngine.shared.unload(m)
            try? m.uninstall()
            refreshModels()
        }
    }

    // MARK: Picking the key

    func pickKey() {
        pickingKey = true
        KeyListener.shared.capture = { [weak self] key in
            guard let self else { return }
            self.pickingKey = false
            self.hotkey = key
            Settings.hotkey = key
            KeyListener.shared.hotkey = key
            KeyListener.shared.reset()
        }
    }

    func stopPickingKey() {
        pickingKey = false
        KeyListener.shared.capture = nil
    }

    // MARK: Key handling
    //
    // Hold mode: press starts the mic at once (so the first word isn't clipped),
    // release finishes. A quick tap is either the first half of a double-tap
    // (→ locked, hands-free) or, if no second tap follows, dropped silently.

    func setMode(_ newMode: DictationMode) {
        mode = newMode
        Settings.mode = newMode
    }

    private func keyPressed() {
        keyIsDown = true
        switch phase {
        case .recording where handsFree:
            ignoreNextRelease = true
            finish()
        case .recording where awaitingSecondTap:
            // Second tap of a double-tap — it locks on release, so ⌥ then ⌥2 (a
            // shortcut) still cancels instead.
            doubleTapTimer?.cancel()
            awaitingSecondTap = false
            secondTapDown = true
        case .idle, .failed, .copied:
            begin()
        default:
            break
        }
    }

    private func keyReleased() {
        keyIsDown = false
        if let problem = pendingProblem {
            pendingProblem = nil
            if mode == .toggle { problem() }   // a tap is a real press in this mode
            return
        }
        if ignoreNextRelease { ignoreNextRelease = false; return }
        if secondTapDown { secondTapDown = false; return lock() }
        guard phase == .recording, !handsFree, let pressedAt else { return }
        let wasTap = Date().timeIntervalSince(pressedAt) < tapThreshold
        switch mode {
        case .toggle:
            lock()
        case .hold:
            wasTap ? cancel() : finish()
        case .holdOrDoubleTap:
            guard wasTap else { return finish() }
            // Keep listening a moment in case this is a double-tap.
            awaitingSecondTap = true
            let timer = DispatchWorkItem { [weak self] in
                guard let self, self.awaitingSecondTap else { return }
                self.cancel()
            }
            doubleTapTimer = timer
            DispatchQueue.main.asyncAfter(deadline: .now() + doubleTapWindow, execute: timer)
        }
    }

    /// Space while holding the key locks the dictation (like Wispr Flow).
    private func spaceWhileHeld() -> Bool {
        guard phase == .recording else { return false }
        if !handsFree, mode != .hold {
            ignoreNextRelease = true
            lock()
        }
        return handsFree
    }

    // MARK: The HUD's buttons

    /// ✕ on the locked HUD: stop without typing anything.
    func cancelFromHUD() {
        guard phase == .recording else { return }
        cancel()
    }

    /// ✓ on the locked HUD: type what you said.
    func finishFromHUD() {
        guard phase == .recording else { return }
        finish()
    }

    /// The language, shown for a moment in the HUD when it differs from last time.
    @Published private(set) var showsLanguage = false
    private var lastLanguage: SpeechModel?

    private func lock() {
        handsFree = true
        confirmStart()
    }

    /// The press is a real dictation (held, or locked) — show the HUD, play the cue.
    private func confirmStart() {
        if !startCuePlayed, installed.count > 1, let last = lastLanguage, last != engine {
            showsLanguage = true
            DispatchQueue.main.asyncAfter(deadline: .now() + 1.6) { [weak self] in self?.showsLanguage = false }
        }
        if !startCuePlayed {
            lastLanguage = engine
            // Only now — a quick tap or a shortcut like ⌥2 → @ never touches your music.
            if !TextInserter.dryRun { MediaControl.shared.begin() }
        }
        hud.show()
        if !startCuePlayed { startCuePlayed = true; Cue.start() }
    }

    private func begin() {
        switch model {
        case .ready: break
        case .loading: return report { $0.fail(String(localized: "Språkmodellen startar – ett ögonblick…")) }
        case .downloading: return report { $0.fail(String(localized: "Språkmodellen laddas fortfarande ned…")) }
        case .missing, .failed:
            return report {
                if AppDelegate.isOnboarding { $0.fail(String(localized: "\($0.engine.title) är inte nedladdad än.")) }
                else { AppDelegate.showWindow() }
            }
        }
        guard micGranted else {
            return report {
                if AppDelegate.isOnboarding { $0.fail(String(localized: "Mindtalk behöver tillgång till mikrofonen.")) }
                else { AppDelegate.showWindow() }
            }
        }
        let chosen = Microphones.shared.selectedDeviceID
        do {
            do {
                try recorder.start(device: chosen)
            } catch where chosen != nil {
                // The chosen mic won't open (just unplugged, reconnecting) — the
                // system input will do rather than nothing.
                Self.log.error("chosen microphone failed, using the system input")
                try recorder.start(device: nil)
            }
        } catch {
            return report { $0.fail(error.localizedDescription) }
        }
        phase = .recording
        recordingEngine = engine
        peakLevel = 0
        startWatchdog()
        if Settings.aiPolish { Task { await Polisher.shared.prepare() } }
        handsFree = false
        pressedAt = Date()
        recordingStarted = pressedAt
        // Show the HUD once the press is clearly a hold (or it got locked) — a
        // quick tap or a shortcut like ⌥2 → @ shouldn't flash it.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) { [weak self] in
            guard let self, self.phase == .recording, self.keyIsDown || self.handsFree || self.mode == .toggle else { return }
            self.confirmStart()
        }
    }

    /// Reports a problem only if the press is held a moment (or in toggle mode, on
    /// release) — a shortcut like ⌥2 → @ or a stray tap stays silent.
    private func report(_ problem: @escaping (Dictation) -> Void) {
        // Two presses in quick succession (a double-tap) are clearly meant too.
        if let last = lastReport, Date().timeIntervalSince(last) < 0.6 {
            lastReport = nil
            pendingProblem = nil
            return problem(self)
        }
        lastReport = Date()
        pendingProblem = { [weak self] in if let self { problem(self) } }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { [weak self] in
            guard let self, self.keyIsDown, let problem = self.pendingProblem else { return }
            self.pendingProblem = nil
            problem()
        }
    }

    /// While recording: catch a missed key release, and end a forgotten dictation.
    private func startWatchdog() {
        watchdog?.invalidate()
        watchdog = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { _ in
            MainActor.assumeIsolated {
                let d = Dictation.shared
                guard d.phase == .recording else { d.watchdog?.invalidate(); d.watchdog = nil; return }
                if let start = d.recordingStarted, Date().timeIntervalSince(start) > Self.maxRecording { return d.finish() }
                // A mic that went quiet without saying so (unplugged, Bluetooth dropped):
                // finish with what was heard instead of recording nothing.
                if d.recorder.silentFor > Self.deadMic { return d.finish() }
                if d.keyIsDown, !d.handsFree, d.mode != .toggle { KeyListener.shared.resync() }
            }
        }
    }

    private func finish() {
        let samples = recorder.stop()
        let lasted = recordingStarted.map { Date().timeIntervalSince($0) } ?? 0
        watchdog?.invalidate(); watchdog = nil
        MediaControl.shared.end()
        handsFree = false
        awaitingSecondTap = false
        secondTapDown = false
        doubleTapTimer?.cancel()
        pressedAt = nil
        // Held a while and not a sound: the mic never delivered — say so.
        if samples.isEmpty, lasted > 1 {
            return fail(String(localized: "Hittar ingen fungerande mikrofon."))
        }
        // Under a quarter second holds no words.
        guard samples.count > 4_000 else { return reset() }
        phase = .transcribing
        hud.show()
        let language = recordingEngine ?? engine
        let heard = peakLevel
        // Where you are when you stop talking is where the text goes — or nowhere,
        // if you switch app while it's being written.
        let target = NSWorkspace.shared.frontmostApplication?.processIdentifier
        Task {
            do {
                let raw = try await SpeechEngine.shared.transcribe(samples: samples, with: language)
                // The debug self-test leaves your vocabulary counts, stats and history alone.
                let selfTest = TextInserter.dryRun
                // A password field: typed, never kept or polished.
                let secret = !selfTest && SecureInput.isActive
                // Rules, then your vocabulary, then (if on) the on-device polish —
                // with the vocabulary once more so your spellings win.
                let cleaned = TextCleanup.apply(raw, fillers: Settings.removeFillers, commands: Settings.voiceCommands)
                var text = selfTest ? cleaned : Vocabulary.shared.apply(cleaned)
                if Settings.aiPolish, !secret, let polished = await Polisher.shared.polish(text) {
                    text = Vocabulary.shared.reapply(polished)
                }
                guard !text.isEmpty else {
                    return heard < 0.03 ? fail(String(localized: "Hörde inget – kontrollera mikrofonen.")) : reset()
                }
                if !selfTest && !secret {
                    Stats.shared.record(text: text, seconds: Double(samples.count) / 16_000)
                    var updated = recent
                    updated.insert(Entry(text: text, date: Date()), at: 0)
                    recent = Array(updated.prefix(Self.recentLimit))
                }
                // You switched app while it was being written: don't type into the
                // wrong one. It's in Senaste, one ⌃⌥V away.
                if !selfTest, let target, NSWorkspace.shared.frontmostApplication?.processIdentifier != target {
                    return fail(secret ? String(localized: "Du bytte app – texten skrevs inte in.")
                                       : String(localized: "Du bytte app – tryck ⌃⌥V för att klistra in texten."))
                }
                if !selfTest, !secret, let target, FocusedField.kind(in: target) == .none {
                    return copied(text)
                }
                // A spoken line break at the end is the separator; otherwise a space.
                // Never a password in the clipboard, whatever the setting.
                TextInserter.insert(text.hasSuffix("\n") ? text : text + " ",
                                    keeping: Settings.keepInClipboard && !secret ? text : nil)
                Cue.done()
                reset()
            } catch {
                fail(error.localizedDescription)
            }
        }
    }

    private func cancel() {
        _ = recorder.stop()
        ignoreNextRelease = false
        reset()
    }

    private func reset() {
        MediaControl.shared.end()
        watchdog?.invalidate(); watchdog = nil
        recordingEngine = nil
        phase = .idle
        if engineSwitchPending {
            engineSwitchPending = false
            if engine.isInstalled { loadModel() } else { refreshModels() }
        }
        handsFree = false
        startCuePlayed = false
        awaitingSecondTap = false
        secondTapDown = false
        doubleTapTimer?.cancel()
        pressedAt = nil
        recordingStarted = nil
        level = 0
        levels = [Float](repeating: 0, count: Self.levelCount)
        hud.hide()
    }

    private func fail(_ message: String) {
        phase = .failed(message)
        hud.show()
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) { [weak self] in
            guard let self, case .failed = self.phase else { return }
            self.reset()
        }
    }

    /// No text field where you are (the desktop, a web page): the text goes to the
    /// clipboard, and the HUD says so.
    private func copied(_ text: String) {
        TextInserter.copy(text)
        Cue.done()
        phase = .copied
        hud.show()
        DispatchQueue.main.asyncAfter(deadline: .now() + 2.5) { [weak self] in
            guard let self, self.phase == .copied else { return }
            self.reset()
        }
    }

    /// ⌃⌥V: the last dictation, typed again where the cursor is now — for when
    /// it landed in the wrong place. Waits for ⌃ and ⌥ to be let go, so the
    /// paste isn't read as ⌃⌥⌘V.
    func pasteLast() {
        let failed: Bool = { if case .failed = phase { return true }; return false }()
        guard phase == .idle || phase == .recording || phase == .copied || failed, let last = recent.first else { NSSound.beep(); return }
        if failed || phase == .copied { reset() }
        if phase == .recording { cancel() }
        Task { @MainActor in
            for _ in 0..<40 {
                let held = CGEventSource.flagsState(.combinedSessionState).intersection([.maskControl, .maskAlternate])
                if held.isEmpty { break }
                try? await Task.sleep(for: .milliseconds(25))
            }
            if let app = NSWorkspace.shared.frontmostApplication, FocusedField.kind(in: app.processIdentifier) == .none {
                return copied(last.text)
            }
            TextInserter.insert(last.text + " ", keeping: Settings.keepInClipboard ? last.text : nil)
        }
    }

    func copy(_ entry: Entry) {
        TextInserter.copy(entry.text)
    }

    func clearRecent() { recent = [] }

    func removeRecent(_ entry: Entry) { recent.removeAll { $0.id == entry.id } }
}
