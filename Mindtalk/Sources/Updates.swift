import AppKit
import Combine
import Sparkle

// MARK: - Updates
//
// Sparkle checks the appcast on GitHub (SUFeedURL in Info.plist) once a day.
// Every update is signed with Mindtalk's own EdDSA key; the app only installs
// one whose signature matches SUPublicEDKey — and, as always, Apple's
// notarization. The private key lives in the developer's keychain (account
// "mindtalk") and signs each release in scripts/release.sh.

@MainActor
final class Updates: NSObject, ObservableObject, SPUStandardUserDriverDelegate {
    static let shared = Updates()

    private lazy var controller = SPUStandardUpdaterController(
        startingUpdater: false, updaterDelegate: nil, userDriverDelegate: self)

    /// Look for updates on its own, once a day.
    @Published var automatic = false {
        didSet { if controller.updater.automaticallyChecksForUpdates != automatic { controller.updater.automaticallyChecksForUpdates = automatic } }
    }

    func start() {
        #if DEBUG
        return   // debug builds never update themselves
        #else
        do {
            try controller.updater.start()
            automatic = controller.updater.automaticallyChecksForUpdates
        } catch {
            NSLog("Mindtalk: updates unavailable: \(error.localizedDescription)")
        }
        #endif
    }

    /// "Sök efter uppdateringar…"
    @objc func checkNow() {
        #if DEBUG
        NSSound.beep()   // debug builds don't update
        #else
        NSApp.activate()
        controller.checkForUpdates(nil)
        #endif
    }

    // A menu bar app: a scheduled update is shown without taking focus — you may be
    // dictating into another app. Only a check you asked for brings Mindtalk forward.
    nonisolated var supportsGentleScheduledUpdateReminders: Bool { true }

    nonisolated func standardUserDriverShouldHandleShowingScheduledUpdate(_ update: SUAppcastItem,
                                                                          andInImmediateFocus immediateFocus: Bool) -> Bool {
        true
    }

    nonisolated func standardUserDriverWillHandleShowingUpdate(_ handleShowingUpdate: Bool,
                                                               forUpdate update: SUAppcastItem,
                                                               state: SPUUserUpdateState) {
        guard state.userInitiated else { return }
        Task { @MainActor in NSApp.activate() }
    }
}
