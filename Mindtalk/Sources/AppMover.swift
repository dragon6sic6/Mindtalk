import AppKit

// MARK: - Move to Applications
//
// Run from the disk image, from Downloads, or from macOS's hidden "translocated"
// copy, Mindtalk can't update itself (Sparkle refuses) and won't reliably open
// at login. So at launch it offers — once — to move itself into Applications:
// copy there (replacing an older copy), drop the quarantine flag on the copy so
// macOS runs it in place, open it, and tidy up behind itself: eject the disk
// image, or move the original from Downloads to the Trash.

@MainActor
enum AppMover {
    private static let suppressKey = "moveToApplicationsSuppressed"

    /// Call first thing at launch. Returns true if Mindtalk is being moved — the
    /// caller then does nothing more; this instance quits.
    static func offerIfNeeded() -> Bool {
        #if DEBUG
        guard CommandLine.arguments.contains("--test-move") else { return false }
        #endif
        guard let source = originalBundleURL(), !isInApplications(source),
              !UserDefaults.standard.bool(forKey: suppressKey) else { return false }

        NSApp.activate()
        let alert = NSAlert()
        alert.messageText = String(localized: "Flytta Mindtalk till Program?")
        alert.informativeText = String(localized: "Mindtalk behöver ligga i Program för att kunna uppdatera sig själv och starta vid inloggning.")
        alert.addButton(withTitle: String(localized: "Flytta till Program"))
        alert.addButton(withTitle: String(localized: "Inte nu"))
        alert.showsSuppressionButton = true
        alert.suppressionButton?.title = String(localized: "Fråga inte igen")
        #if DEBUG
        let answer: NSApplication.ModalResponse = CommandLine.arguments.contains("--move-auto") ? .alertFirstButtonReturn : alert.runModal()
        #else
        let answer = alert.runModal()
        #endif
        if alert.suppressionButton?.state == .on { UserDefaults.standard.set(true, forKey: suppressKey) }
        guard answer == .alertFirstButtonReturn else { return false }

        do {
            let destination = try moveIntoApplications(from: source)
            relaunch(destination, cleaningUp: source)
            return true
        } catch {
            let failed = NSAlert()
            failed.messageText = String(localized: "Mindtalk kunde inte flyttas")
            failed.informativeText = String(localized: "Dra Mindtalk till Program i Finder i stället. (\(error.localizedDescription))")
            failed.runModal()
            return false
        }
    }

    // MARK: Where we are

    static var applicationsFolder: URL {
        #if DEBUG
        let args = CommandLine.arguments
        if let i = args.firstIndex(of: "--move-destination"), i + 1 < args.count { return URL(fileURLWithPath: args[i + 1]) }
        #endif
        return URL(fileURLWithPath: "/Applications")
    }

    private static func isInApplications(_ url: URL) -> Bool {
        let path = url.resolvingSymlinksInPath().deletingLastPathComponent().path
        let userApps = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Applications").path
        return path == applicationsFolder.resolvingSymlinksInPath().path || path == userApps
    }

    /// Where the app really is — not the hidden translocated copy macOS runs.
    private static func originalBundleURL() -> URL? {
        let running = Bundle.main.bundleURL
        guard running.path.contains("/AppTranslocation/") else { return running }
        // Security.framework's SecTranslocateCreateOriginalPathForURL, looked up at run time.
        guard let handle = dlopen("/System/Library/Frameworks/Security.framework/Security", RTLD_LAZY),
              let symbol = dlsym(handle, "SecTranslocateCreateOriginalPathForURL") else { return nil }
        typealias Original = @convention(c) (CFURL, UnsafeMutablePointer<Unmanaged<CFError>?>?) -> Unmanaged<CFURL>?
        let original = unsafeBitCast(symbol, to: Original.self)
        return original(running as CFURL, nil)?.takeRetainedValue() as URL?
    }

    private static func isOnDiskImage(_ url: URL) -> Bool {
        (try? url.resourceValues(forKeys: [.volumeIsReadOnlyKey]))?.volumeIsReadOnly == true
            || url.path.hasPrefix("/Volumes/")
    }

    // MARK: Moving

    private static func moveIntoApplications(from source: URL) throws -> URL {
        let fm = FileManager.default
        let destination = applicationsFolder.appendingPathComponent(source.lastPathComponent)
        // An older copy already there goes to the Trash (recoverable), not gone.
        if fm.fileExists(atPath: destination.path) {
            try fm.trashItem(at: destination, resultingItemURL: nil)
        }
        let staging = applicationsFolder.appendingPathComponent(".\(source.lastPathComponent)-\(UUID().uuidString)")
        try fm.copyItem(at: source, to: staging)
        // Not "downloaded" any more: without this macOS would translocate the copy too.
        removeQuarantine(staging)
        try fm.moveItem(at: staging, to: destination)
        return destination
    }

    private static func removeQuarantine(_ url: URL) {
        guard let enumerator = FileManager.default.enumerator(at: url, includingPropertiesForKeys: nil) else { return }
        removexattr(url.path, "com.apple.quarantine", XATTR_NOFOLLOW)
        for case let item as URL in enumerator { removexattr(item.path, "com.apple.quarantine", XATTR_NOFOLLOW) }
    }

    /// Opens the moved copy once this one has quit, then tidies up.
    private static func relaunch(_ destination: URL, cleaningUp source: URL) {
        let q = { (s: String) in "'" + s.replacingOccurrences(of: "'", with: "'\\''") + "'" }
        let pid = ProcessInfo.processInfo.processIdentifier
        var script = "while kill -0 \(pid) 2>/dev/null; do sleep 0.2; done; /usr/bin/open \(q(destination.path))"
        if isOnDiskImage(source) {
            // Eject the disk image it came from.
            let volume = source.deletingLastPathComponent().path
            if volume.hasPrefix("/Volumes/") { script += "; sleep 1; /usr/bin/hdiutil detach \(q(volume)) -quiet" }
        } else if FileManager.default.isDeletableFile(atPath: source.path) {
            // A copy in Downloads would only confuse: to the Trash with it.
            try? FileManager.default.trashItem(at: source, resultingItemURL: nil)
        }
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/bin/sh")
        task.arguments = ["-c", script]
        try? task.run()
        NSApp.terminate(nil)
    }
}
