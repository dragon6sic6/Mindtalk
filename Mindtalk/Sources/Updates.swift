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

    /// "Sök efter uppdateringar …"
    @objc func checkNow() {
        NSApp.activate()
        controller.checkForUpdates(nil)
    }

    // A menu bar app is rarely in front: bring it forward when Sparkle has news.
    nonisolated var supportsGentleScheduledUpdateReminders: Bool { true }

    nonisolated func standardUserDriverWillHandleShowingUpdate(_ handleShowingUpdate: Bool,
                                                               forUpdate update: SUAppcastItem,
                                                               state: SPUUserUpdateState) {
        Task { @MainActor in NSApp.activate() }
    }
}
