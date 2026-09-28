import AppKit
import Combine
import ServiceManagement
import SwiftUI

@main
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate {
    static func main() {
        // Self-test: `Mindtalk --transcribe file.wav [--model swedish|multilingual] [--clean]`
        // prints what the model hears (and, with --clean, the cleaned and polished text).
        // `Mindtalk --clean "text"` runs only the cleanup.
        let args = CommandLine.arguments
        if let i = args.firstIndex(of: "--clean"), i + 1 < args.count, !args.contains("--transcribe") {
            Task {
                await printCleanup(of: args[i + 1])
                exit(0)
            }
            RunLoop.main.run()
        }
        if let i = args.firstIndex(of: "--transcribe"), i + 1 < args.count {
            let model = args.firstIndex(of: "--model").flatMap { j in
                j + 1 < args.count ? SpeechModel(rawValue: args[j + 1]) : nil
            } ?? .swedish
            Task {
                do {
                    let samples = try Recorder.load(URL(fileURLWithPath: args[i + 1]))
                    let t0 = Date()
                    let raw = try await SpeechEngine.shared.transcribe(samples: samples, with: model)
                    print(raw)
                    print(String(format: "(%.1f s ljud, %.2f s)", Double(samples.count) / 16_000, Date().timeIntervalSince(t0)))
                    if args.contains("--clean") { await printCleanup(of: raw) }
                    exit(0)
                } catch {
                    print("Fel: \(error.localizedDescription)")
                    exit(1)
                }
            }
            RunLoop.main.run()
        }

        // Self-test: `Mindtalk --install multilingual` downloads, verifies and installs a model.
        if let i = args.firstIndex(of: "--install"), i + 1 < args.count, let model = SpeechModel(rawValue: args[i + 1]) {
            Task {
                do {
                    let t0 = Date()
                    var last = -1
                    try await model.install { p in
                        let pct = Int(p * 100)
                        if pct / 10 != last / 10 { last = pct; print("\(pct) %") }
                    }
                    print(String(format: "Installerad i %@ (%.0f s)", model.ownDirectory.path, Date().timeIntervalSince(t0)))
                    exit(0)
                } catch {
                    print("Fel: \(error.localizedDescription)")
                    exit(1)
                }
            }
            RunLoop.main.run()
        }

        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        app.setActivationPolicy(.accessory)
        app.run()
    }

    private var statusItem: NSStatusItem!
    private let statusPanel = StatusPanel()
    private var window: NSWindow?
    private var onboardingWindow: NSWindow?
    private var cancellables = Set<AnyCancellable>()
    private let dictation = Dictation.shared

    func applicationWillTerminate(_ notification: Notification) {
        MediaControl.shared.restoreNow()
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        // From the disk image or Downloads: offer to move to Applications first.
        if AppMover.offerIfNeeded() { return }
        MediaControl.shared.restoreAfterCrash()
        SpeechModel.removeLeftoverDownloads()
        Updates.shared.start()
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        // Left or right click: the panel — one menu, nothing hidden.
        statusItem.button?.target = self
        statusItem.button?.action = #selector(statusItemClicked(_:))
        statusItem.button?.sendAction(on: [.leftMouseUp, .rightMouseUp])
        NSApp.mainMenu = Self.mainMenu()
        AppPrefs.shared.apply()
        Self.updateDockPolicy()

        dictation.$phase.combineLatest(dictation.$model)
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in self?.updateIcon() }
            .store(in: &cancellables)

        // Keep the panel's size in step with what's in it.
        dictation.$recent.combineLatest(dictation.$phase, dictation.$model)
            .receive(on: RunLoop.main)
            .sink { [weak self] _ in DispatchQueue.main.async { self?.statusPanel.refit() } }
            .store(in: &cancellables)

        dictation.start()
        #if DEBUG
        if CommandLine.arguments.contains("--simulate-keys") { return simulateKeys() }
        // Simulates a 3-second dictation against whatever is playing.
        if CommandLine.arguments.contains("--media-test") {
            Task { @MainActor in
                func show(_ label: String) { print(label, MediaControl.playingProcesses().map(\.bundleID)) }
                show("före:")
                MediaControl.shared.begin()
                for _ in 0..<6 { try? await Task.sleep(for: .milliseconds(500)); show("  dikterar:") }
                MediaControl.shared.end()
                for _ in 0..<10 { try? await Task.sleep(for: .milliseconds(500)); show("  efter:") }
                exit(0)
            }
            return
        }
        // Download test: log every two seconds what the introduction shows.
        if Demo.autoDownload {
            Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [self] _ in
                MainActor.assumeIsolated {
                    let parts = SpeechModel.allCases.map { m -> String in
                        if let p = dictation.downloads[m] { return "\(m.rawValue) \(Int(p * 100))%" }
                        if let e = dictation.downloadErrors[m] { return "\(m.rawValue) FEL: \(e)" }
                        return "\(m.rawValue) \(m.isInstalled ? "klar" : "–")"
                    }
                    print("\(Int(Date().timeIntervalSince1970) % 100000) \(parts.joined(separator: " · ")) · aktiv: \(dictation.engine.rawValue) \(dictation.model)")
                }
            }
        }
        // "Visa introduktionen" from Settings, as the button does it.
        if CommandLine.arguments.contains("--test-intro") {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.8) { [self] in
                showWindow()
                if CommandLine.arguments.contains("--maximized"), let screen = window?.screen {
                    window?.setFrame(screen.visibleFrame, display: true)
                }
                if CommandLine.arguments.contains("--fullscreen") { window?.toggleFullScreen(nil) }
                NotificationCenter.default.post(name: .showPage, object: Page.settings)
                DispatchQueue.main.asyncAfter(deadline: .now() + (CommandLine.arguments.contains("--fullscreen") ? 2.5 : 1.2)) {
                    AppDelegate.showIntroduction()
                    print("Under introduktionen – huvudfönstret synligt: \(self.window?.isVisible == true)")
                    if CommandLine.arguments.contains("--then-close") {
                        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) {
                            self.onboardingWindow?.performClose(nil)
                            DispatchQueue.main.asyncAfter(deadline: .now() + 1) {
                                print("Efter stängning – huvudfönstret synligt: \(self.window?.isVisible == true)")
                                exit(0)
                            }
                        }
                    }
                }
            }
            return
        }
        if CommandLine.arguments.contains("--show-about") {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { AppDelegate.showAbout() }
            return
        }
        // README screenshots: an onboarding step, or the dictation HUD.
        if Demo.onboardingStep != nil { showOnboarding(); return }
        if Demo.hud {
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) { self.dictation.demoRecording() }
            return
        }
        // Opens the panel on launch, for screenshots.
        if CommandLine.arguments.contains("--show-panel") {
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [self] in
                if let button = statusItem.button { statusPanel.show(from: button) }
            }
            return
        }
        #endif

        if !Settings.didOnboard {
            // Start at login from the first launch — installed in /Applications, that is.
            if Bundle.main.bundlePath.hasPrefix("/Applications") { try? SMAppService.mainApp.register() }
            showOnboarding()
        } else if let raw = UserDefaults.standard.string(forKey: "reopenPage"), let page = Page(rawValue: raw) {
            UserDefaults.standard.removeObject(forKey: "reopenPage")
            MainView.startPage = page
            showWindow()
        } else if dictation.needsSetup {
            showWindow()
        }
    }

    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        if onboardingWindow?.isVisible == true { showOnboarding() } else { showWindow() }
        return false
    }

    // MARK: Status item

    private func updateIcon() {
        let symbol: String
        switch dictation.phase {
        case .recording: symbol = "waveform.circle.fill"
        case .transcribing: symbol = "ellipsis.circle"
        case .failed: symbol = "exclamationmark.circle"
        case .idle:
            if case .downloading = dictation.model { symbol = "arrow.down.circle" }
            else { statusItem.button?.image = Mark.menuBarImage; return }
        }
        let image = NSImage(systemSymbolName: symbol, accessibilityDescription: "Mindtalk")
        image?.isTemplate = true
        statusItem.button?.image = image
    }

    #if DEBUG
    /// Drives the key logic with scripted presses (no event tap, no paste) and
    /// prints every state change — for checking hold / tap / double-tap / space.
    private func simulateKeys() {
        TextInserter.dryRun = true
        let keys = KeyListener.shared
        var cancellables = Set<AnyCancellable>()
        dictation.$phase.removeDuplicates().sink { print("  fas: \($0)") }.store(in: &cancellables)
        dictation.$handsFree.removeDuplicates().filter { $0 }.sink { _ in print("  låst") }.store(in: &cancellables)
        let originalMode = dictation.mode
        Task { @MainActor in
            while dictation.model != .ready { try? await Task.sleep(for: .milliseconds(200)) }
            func step(_ title: String) async { print("== \(title)"); try? await Task.sleep(for: .milliseconds(300)) }
            @MainActor func press(_ s: Double) async { keys.onPress?(); try? await Task.sleep(for: .seconds(s)); keys.onRelease?() }
            func pause(_ s: Double) async { try? await Task.sleep(for: .seconds(s)) }
            for mode in DictationMode.allCases {
                dictation.setMode(mode)
                print("##### Läge: \(mode.title)")
                await step("A: håll in 1,2 s"); await press(1.2); await pause(1.5)
                await step("B: enkeltryck"); await press(0.08); await pause(1.2)
                await step("C: dubbeltryck, vänta 1,5 s, tryck"); await press(0.08); await pause(0.12); await press(0.08)
                await pause(1.5); await press(0.08); await pause(1.5)
                await step("D: håll in + mellanslag, släpp, vänta, tryck")
                keys.onPress?(); await pause(0.5)
                print("  mellanslag slukat: \(keys.onSpaceWhileHeld?() ?? false)")
                await pause(0.3); keys.onRelease?(); await pause(1.0); await press(0.08); await pause(1.5)
                if dictation.phase != .idle { print("  (städar)"); _ = keys.onEscape?() }
            }
            _ = cancellables
            dictation.setMode(originalMode)
            print("== klart"); exit(0)
        }
    }
    #endif

    // MARK: Menu bar (while the window is open)

    /// The standard menus — without an Edit menu, ⌘C/⌘V/⌘A do nothing in our own text fields.
    private static func mainMenu() -> NSMenu {
        func item(_ title: String, _ action: Selector?, _ key: String = "",
                  _ modifiers: NSEvent.ModifierFlags = .command) -> NSMenuItem {
            let item = NSMenuItem(title: String(localized: String.LocalizationValue(title)), action: action, keyEquivalent: key)
            item.keyEquivalentModifierMask = modifiers
            return item
        }
        func submenu(_ title: String, _ items: [NSMenuItem]) -> NSMenuItem {
            let title = String(localized: String.LocalizationValue(title))
            let menu = NSMenu(title: title)
            items.forEach(menu.addItem)
            let holder = NSMenuItem(title: title, action: nil, keyEquivalent: "")
            holder.submenu = menu
            return holder
        }
        let main = NSMenu()
        main.addItem(submenu("Mindtalk", [
            item("Om Mindtalk", #selector(AppDelegate.menuAbout)),
            {
                let check = item("Sök efter uppdateringar…", #selector(Updates.checkNow))
                check.target = Updates.shared
                return check
            }(),
            .separator(),
            item("Inställningar…", #selector(AppDelegate.menuSettings), ","),
            .separator(),
            item("Göm Mindtalk", #selector(NSApplication.hide(_:)), "h"),
            .separator(),
            item("Avsluta Mindtalk", #selector(NSApplication.terminate(_:)), "q"),
        ]))
        main.addItem(submenu("Redigera", [
            item("Ångra", Selector(("undo:")), "z"),
            item("Gör om", Selector(("redo:")), "z", [.command, .shift]),
            .separator(),
            item("Klipp ut", #selector(NSText.cut(_:)), "x"),
            item("Kopiera", #selector(NSText.copy(_:)), "c"),
            item("Klistra in", #selector(NSText.paste(_:)), "v"),
            item("Markera allt", #selector(NSText.selectAll(_:)), "a"),
        ]))
        main.addItem(submenu("Visa", [
            item("Diktering", #selector(AppDelegate.menuDictation), "1"),
            item("Senaste", #selector(AppDelegate.menuRecent), "2"),
            item("Ordlista", #selector(AppDelegate.menuVocabulary), "3"),
        ]))
        let window = submenu("Fönster", [
            item("Minimera", #selector(NSWindow.performMiniaturize(_:)), "m"),
            item("Zooma", #selector(NSWindow.performZoom(_:))),
            item("Stäng", #selector(NSWindow.performClose(_:)), "w"),
        ])
        main.addItem(window)
        NSApp.windowsMenu = window.submenu
        return main
    }

    // MARK: Menu actions

    @objc func menuAbout() { Self.showAbout() }
    @objc func menuSettings() { Self.showWindow(page: .settings) }
    @objc func menuDictation() { Self.showWindow(page: .dictation) }
    @objc func menuRecent() { Self.showWindow(page: .recent) }
    @objc func menuVocabulary() { Self.showWindow(page: .vocabulary) }

    // MARK: Relaunch

    /// Quits and starts again (to switch the app's language), reopening the window on `page`.
    static func relaunch(showing page: Page) {
        UserDefaults.standard.set(page.rawValue, forKey: "reopenPage")
        let path = Bundle.main.bundlePath.replacingOccurrences(of: "'", with: "'\\''")
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/bin/sh")
        task.arguments = ["-c", "sleep 0.5; /usr/bin/open '\(path)'"]
        try? task.run()
        NSApp.terminate(nil)
    }

    // MARK: Onboarding

    static var isOnboarding: Bool {
        (NSApp.delegate as? AppDelegate)?.onboardingWindow?.isVisible == true
    }

    func showOnboarding() {
        if onboardingWindow == nil {
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 760, height: 640),
                                  styleMask: [.titled, .closable, .miniaturizable, .fullSizeContentView],
                                  backing: .buffered, defer: false)
            window.titlebarAppearsTransparent = true
            window.titleVisibility = .hidden
            window.isMovableByWindowBackground = true
            window.isReleasedWhenClosed = false
            window.delegate = self
            window.contentViewController = NSHostingController(rootView: OnboardingView(dictation: dictation) { [weak self] in
                self?.onboardingWindow?.close()
            })
            window.setContentSize(NSSize(width: 760, height: 640))
            window.center()
            // No zoom-in from macOS: it would play on top of the introduction's
            // own entrance. The window simply appears, solid and still.
            window.animationBehavior = .none
            // Its own window, never a tab of the main one, and shown in the space
            // you're in — also a full-screen one — so macOS doesn't slide between spaces.
            window.tabbingMode = .disallowed
            window.collectionBehavior = [.moveToActiveSpace, .fullScreenAuxiliary, .fullScreenDisallowsTiling]
            onboardingWindow = window
        }
        windowOpen = true
        Self.updateDockPolicy()
        bringForward(onboardingWindow)
    }


    // MARK: Main window

    private static func printCleanup(of raw: String) async {
        let rules = TextCleanup.apply(raw, fillers: true, commands: true)
        let cleaned = await Vocabulary.shared.reapply(rules)       // your list, nothing counted
        print("Regler: \(rules.debugDescription)")
        if cleaned != rules { print("Ordlista: \(cleaned.debugDescription)") }
        let t0 = Date()
        let polished = await Polisher.shared.polish(cleaned)
        print(String(format: "Putsad: %@ (%.2f s)", polished?.debugDescription ?? "– (behåller reglernas text)", Date().timeIntervalSince(t0)))
    }

    static func showWindow(page: Page? = nil) {
        if let page { MainView.startPage = page }
        (NSApp.delegate as? AppDelegate)?.showWindow()
        if let page { NotificationCenter.default.post(name: .showPage, object: page) }
    }

    // MARK: Status item

    @objc private func statusItemClicked(_ sender: NSStatusBarButton) {
        statusPanel.toggle(from: sender)
    }

    /// Settings, scrolled to "Om Mindtalk" (credits and licences).
    static func showAbout() {
        SettingsPage.jumpToAbout = true
        showWindow(page: .settings)
        NotificationCenter.default.post(name: .showAbout, object: nil)
    }

    /// The introduction again. With the main window open it plays inside that
    /// window — no second window, so nothing for macOS to animate or rearrange;
    /// otherwise in its own window, as on first launch.
    static func showIntroduction() {
        guard let delegate = NSApp.delegate as? AppDelegate else { return }
        if let window = delegate.window, window.isVisible {
            NotificationCenter.default.post(name: .showIntroduction, object: nil)
        } else {
            delegate.showOnboarding()
        }
    }

    func closeStatusPanel() { statusPanel.close() }

    /// Closes the menu bar panel if it's open; true if it was.
    static func closeStatusPanelIfShown() -> Bool {
        guard let delegate = NSApp.delegate as? AppDelegate, delegate.statusPanel.isShown else { return false }
        delegate.statusPanel.close()
        return true
    }

    /// While the window is open Mindtalk is a regular app (Dock, ⌘-Tab); closed,
    /// it lives in the menu bar only.
    func showWindow() {
        if window == nil {
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 960, height: 680),
                                  styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
                                  backing: .buffered, defer: false)
            window.titlebarAppearsTransparent = true
            window.titleVisibility = .hidden
            window.isMovableByWindowBackground = true
            window.tabbingMode = .disallowed
            window.contentViewController = NSHostingController(rootView: MainView(dictation: dictation, prefs: .shared))
            window.isReleasedWhenClosed = false
            window.delegate = self
            window.setContentSize(NSSize(width: 960, height: 680))
            #if DEBUG
            // Screenshots: the same size every time, and your own window size left alone.
            if Demo.on { window.setContentSize(NSSize(width: 1040, height: 700)); window.center() }
            else { window.setFrameAutosaveName("MindtalkMain") }
            #else
            window.setFrameAutosaveName("MindtalkMain")
            #endif
            if !window.setFrameUsingName("MindtalkMain") { window.center() }
            self.window = window
        }
        windowOpen = true
        Self.updateDockPolicy()
        bringForward(window)
        // Nothing focused on open — otherwise the "Prova" box grabs focus and scrolls the page.
        window?.makeFirstResponder(nil)
    }

    /// Shows a window in front and makes Mindtalk the active app, so the window
    /// is key — coloured traffic lights, keyboard focus. Switching from menu bar
    /// app to Dock app has to settle first, or macOS turns the activation down.
    private func bringForward(_ window: NSWindow?) {
        guard let window else { return }
        window.orderFrontRegardless()
        DispatchQueue.main.async {
            NSApp.activate()
            window.makeKeyAndOrderFront(nil)
            // Once more a moment later if macOS hadn't let us in yet.
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) {
                guard !NSApp.isActive || !window.isKeyWindow else { return }
                NSApp.activate()
                window.makeKeyAndOrderFront(nil)
            }
        }
    }

    func windowWillClose(_ notification: Notification) {
        dictation.stopPickingKey()
        if (notification.object as? NSWindow) === onboardingWindow {
            // Closing the introduction — however it's closed — means it's done.
            Settings.didOnboard = true
            onboardingWindow = nil
        }
        windowOpen = [window, onboardingWindow].contains { w in
            w != nil && w !== (notification.object as? NSWindow) && w?.isVisible == true
        }
        Self.updateDockPolicy()
    }

    private var windowOpen = false

    /// In the Dock while the window is open, or always if the user wants it there.
    static func updateDockPolicy() {
        let open = (NSApp.delegate as? AppDelegate)?.windowOpen ?? false
        NSApp.setActivationPolicy(open || Settings.showInDock ? .regular : .accessory)
    }
}
