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
        hud.show()
    }
    #endif

    private func push(level value: Float) {
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
        Task.detached(priority: .utility) {
            if entries.isEmpty { try? FileManager.default.removeItem(at: file); return }
            try? FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
            try? JSONEncoder().encode(entries).write(to: file, options: [.atomic, .completeFileProtection])
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

    private init() {
        recorder.onLevel = { [weak self] value in
            Task { @MainActor in self?.push(level: value) }
        }
        let keys = KeyListener.shared
        keys.onPress = { [weak self] in self?.keyPressed() }
        keys.onRelease = { [weak self] in self?.keyReleased() }
        keys.onChord = { [weak self] in
            guard let self, self.phase == .recording, !self.handsFree else { return }
            self.cancel()
        }
        keys.onSpaceWhileHeld = { [weak self] in self?.spaceWhileHeld() ?? false }
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
        if accessibilityGranted {
            KeyListener.shared.start()
            if micGranted { permissionPoll?.invalidate(); permissionPoll = nil; return }
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

    func downloadModel(_ which: SpeechModel? = nil) {
        let m = which ?? engine
        guard downloadTasks[m] == nil, !m.isInstalled else { return }
        downloads[m] = 0
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
                if m == engine { loadModel() } else { refreshModels() }
            } catch {
                downloadTasks[m] = nil
                downloads[m] = nil
                let cancelled = error is CancellationError || (error as? URLError)?.code == .cancelled
                if !cancelled { downloadErrors[m] = error.localizedDescription }
                refreshModels()
            }
        }
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
        case .idle, .failed:
            begin()
        default:
            break
        }
    }

    private func keyReleased() {
        keyIsDown = false
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

    private func lock() {
        handsFree = true
        confirmStart()
    }

    /// The press is a real dictation (held, or locked) — show the HUD, play the cue.
    private func confirmStart() {
        hud.show()
        if !startCuePlayed { startCuePlayed = true; Cue.start() }
    }

    private func begin() {
        switch model {
        case .ready: break
        case .loading: return fail(String(localized: "Språkmodellen startar – ett ögonblick…"))
        case .downloading: return fail(String(localized: "Språkmodellen laddas fortfarande ned…"))
        case .missing, .failed:
            if AppDelegate.isOnboarding { return fail(String(localized: "\(engine.title) är inte nedladdad än.")) }
            AppDelegate.showWindow(); return
        }
        guard micGranted else {
            if AppDelegate.isOnboarding { return fail(String(localized: "Mindtalk behöver tillgång till mikrofonen.")) }
            AppDelegate.showWindow(); return
        }
        do {
            try recorder.start(device: Microphones.shared.selectedDeviceID)
        } catch {
            return fail(error.localizedDescription)
        }
        phase = .recording
        if !TextInserter.dryRun { MediaControl.shared.begin() }
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

    private func finish() {
        let samples = recorder.stop()
        MediaControl.shared.end()
        handsFree = false
        awaitingSecondTap = false
        secondTapDown = false
        doubleTapTimer?.cancel()
        pressedAt = nil
        // Under a quarter second holds no words.
        guard samples.count > 4_000 else { return reset() }
        phase = .transcribing
        hud.show()
        Task {
            do {
                let raw = try await SpeechEngine.shared.transcribe(samples: samples, with: engine)
                // The debug self-test leaves your vocabulary counts, stats and history alone.
                let selfTest = TextInserter.dryRun
                // Rules, then your vocabulary, then (if on) the on-device polish —
                // with the vocabulary once more so your spellings win.
                let cleaned = TextCleanup.apply(raw, fillers: Settings.removeFillers, commands: Settings.voiceCommands)
                var text = selfTest ? cleaned : Vocabulary.shared.apply(cleaned)
                if Settings.aiPolish, let polished = await Polisher.shared.polish(text) {
                    text = Vocabulary.shared.reapply(polished)
                }
                if !text.isEmpty {
                    if !selfTest {
                        Stats.shared.record(text: text, seconds: Double(samples.count) / 16_000)
                        var updated = recent
                        updated.insert(Entry(text: text, date: Date()), at: 0)
                        recent = Array(updated.prefix(Self.recentLimit))
                    }
                    // A spoken line break at the end is the separator; otherwise a space.
                    TextInserter.insert(text.hasSuffix("\n") ? text : text + " ")
                    Cue.done()
                }
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
        phase = .idle
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

    func copy(_ entry: Entry) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(entry.text, forType: .string)
    }

    func clearRecent() { recent = [] }
}
