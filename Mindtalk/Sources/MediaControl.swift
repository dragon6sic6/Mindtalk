import AppKit
import AudioToolbox
import CoreAudio
import os

// MARK: - Music while you dictate
//
// macOS no longer tells other apps what's playing, so Mindtalk looks at what it
// can see: which apps are sending sound to the speakers right now (Core Audio's
// process list, with each app's bundle ID).
//
// - A media app (Spotify, Music, a browser…) is paused with the system's
//   media command, and played again afterwards — but only once its sound has
//   actually stopped, which proves the pause was ours. So music can never
//   start by itself: nothing playing, nothing sent.
// - Anything else making sound (a call, a game) can't be paused, so the
//   volume is faded down while you speak and back up after.
// - "Tona ner" fades everything down; "Låt vara" leaves the sound alone.

enum MediaMode: String, CaseIterable, Identifiable {
    case pause, duck, off
    var id: String { rawValue }

    var title: String {
        switch self {
        case .pause: return String(localized: "Pausa")
        case .duck: return String(localized: "Tona ner")
        case .off: return String(localized: "Låt vara")
        }
    }

    var explanation: String {
        switch self {
        case .pause: return String(localized: "Musik och video pausas medan du pratar och fortsätter sen. Annat ljud tonas ner.")
        case .duck: return String(localized: "Allt ljud tonas ner medan du pratar och upp igen efteråt.")
        case .off: return String(localized: "Ljudet på datorn lämnas som det är.")
        }
    }
}

@MainActor
final class MediaControl {
    static let shared = MediaControl()
    private let log = Logger(subsystem: "ai.mindact.mindtalk", category: "media")

    /// Media apps we paused, and when.
    private var paused: (processes: Set<AudioObjectID>, at: ContinuousClock.Instant)?
    private var resolving: Task<Void, Never>?
    /// The volume we faded from, on which device, and the level we left it at.
    private var ducked: (device: AudioObjectID, volume: Float32, level: Float32)?
    private var fading: Task<Void, Never>?
    /// The last volume our own fade set — so "did you change it yourself?" can
    /// be told apart from a fade that simply hadn't finished yet.
    private var lastWritten: Float32?
    /// A restore still fading back up — a new dictation ducks from its target.
    private var restoringTo: (device: AudioObjectID, volume: Float32)?

    private static let duckLevel: Float32 = 0.0
    private static let savedKey = "duckedVolume"

    /// Recording started.
    func begin() {
        let mode = Settings.mediaMode
        guard mode != .off else { return }
        let playing = Self.playingProcesses()
        // A pause from the last dictation still settling carries over.
        let carried = paused
        resolving?.cancel()
        resolving = nil

        let media = mode == .pause ? playing.filter { Self.isMediaApp($0.bundleID) } : []
        let other = playing.filter { p in !media.contains { $0.id == p.id } }

        if !media.isEmpty || carried != nil {
            MediaRemote.send(.pause)
            let ids = Set(media.map(\.id)).union(carried?.processes ?? [])
            paused = (ids, carried?.at ?? .now)
            log.info("paused \(media.map(\.bundleID), privacy: .public)")
        }
        if !other.isEmpty { duck() }
    }

    /// Recording stopped (or was cancelled).
    func end() {
        restoreVolume()
        guard let paused, resolving == nil else { return }
        // Play again once the paused apps have gone quiet — Spotify takes about
        // two seconds to let go of the speakers — or leave it be.
        resolving = Task { [weak self] in
            let deadline = paused.at + .seconds(4)
            while !Task.isCancelled {
                let stillPlaying = Set(Self.playingProcesses().map(\.id))
                if paused.processes.isDisjoint(with: stillPlaying) {
                    MediaRemote.send(.play)
                    self?.log.info("resumed")
                    break
                }
                if ContinuousClock.now >= deadline {
                    self?.log.info("not resumed: the sound never stopped")
                    break
                }
                try? await Task.sleep(for: .milliseconds(200))
            }
            guard !Task.isCancelled else { return }
            self?.paused = nil
            self?.resolving = nil
        }
    }

    /// Quitting: the volume back at once, no fade.
    func restoreNow() {
        fading?.cancel()
        guard let ducked else { return }
        self.ducked = nil
        Self.setVolume(ducked.volume, on: ducked.device)
        clearSaved()
    }

    /// At launch: if Mindtalk quit while the volume was down, put it back.
    func restoreAfterCrash() {
        guard let saved = UserDefaults.standard.dictionary(forKey: Self.savedKey),
              let uid = saved["device"] as? String, let volume = saved["volume"] as? Double else { return }
        UserDefaults.standard.removeObject(forKey: Self.savedKey)
        if let device = Self.device(uid: uid) { Self.setVolume(Float32(volume), on: device) }
    }

    // MARK: Volume

    private func duck() {
        guard ducked == nil, let device = Self.defaultOutput(), let current = Self.volume(of: device) else { return }
        let volume = restoringTo.flatMap { $0.device == device ? $0.volume : nil } ?? current
        restoringTo = nil
        guard volume > Self.duckLevel + 0.01 else { return }
        ducked = (device, volume, Self.duckLevel)
        if let uid = Self.uid(of: device) {
            UserDefaults.standard.set(["device": uid, "volume": Double(volume)], forKey: Self.savedKey)
        }
        fade(device, from: volume, to: Self.duckLevel, over: .milliseconds(250))
    }

    private func restoreVolume() {
        guard let ducked else { return }
        self.ducked = nil
        fading?.cancel()
        // Changed the volume yourself meanwhile? Then it's yours. Otherwise — even
        // mid-fade, after a quick tap — it goes back to where it was.
        guard let now = Self.volume(of: ducked.device) else { return clearSaved() }
        let ours = lastWritten ?? ducked.level
        guard abs(now - ours) < 0.03 else { return clearSaved() }
        restoringTo = (ducked.device, ducked.volume)
        fade(ducked.device, from: now, to: ducked.volume, over: .milliseconds(400)) { [weak self] in
            self?.restoringTo = nil
            self?.clearSaved()
        }
    }

    /// The crash-restore note goes only once the volume is really back.
    private func clearSaved() {
        UserDefaults.standard.removeObject(forKey: Self.savedKey)
        lastWritten = nil
    }

    private func fade(_ device: AudioObjectID, from: Float32, to: Float32, over duration: Duration,
                      then done: (@MainActor () -> Void)? = nil) {
        fading?.cancel()
        let steps = 10
        fading = Task { [weak self] in
            for i in 1...steps {
                guard !Task.isCancelled else { return }
                let t = Float32(i) / Float32(steps)
                let value = from + (to - from) * t * t * (3 - 2 * t)
                Self.setVolume(value, on: device)
                self?.lastWritten = value
                try? await Task.sleep(for: duration / steps)
            }
            done?()
        }
    }

    // MARK: Core Audio

    struct Player { let id: AudioObjectID; let bundleID: String }

    /// Other apps sending sound to the speakers right now.
    nonisolated static func playingProcesses() -> [Player] {
        let me = ProcessInfo.processInfo.processIdentifier
        let list: [AudioObjectID] = array(AudioObjectID(kAudioObjectSystemObject), kAudioHardwarePropertyProcessObjectList)
        return list.compactMap { id in
            guard let running: UInt32 = value(id, kAudioProcessPropertyIsRunningOutput), running != 0,
                  let pid: pid_t = value(id, kAudioProcessPropertyPID), pid != me else { return nil }
            var bundle: CFString = "" as CFString
            var size = UInt32(MemoryLayout<CFString>.size)
            var address = AudioObjectPropertyAddress(mSelector: kAudioProcessPropertyBundleID, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
            _ = withUnsafeMutablePointer(to: &bundle) { AudioObjectGetPropertyData(id, &address, 0, nil, &size, $0) }
            return Player(id: id, bundleID: bundle as String)
        }
    }

    /// Apps whose sound is music, podcasts or video — the ones a pause is for.
    /// Browsers play through helper processes (Chrome) or WebKit's GPU process (Safari).
    nonisolated static func isMediaApp(_ bundleID: String) -> Bool {
        let prefixes = [
            "com.spotify.client", "com.apple.Music", "com.apple.podcasts", "com.apple.TV", "com.apple.iBooksX",
            "com.apple.QuickTimePlayerX", "org.videolan.vlc", "com.colliderli.iina", "com.tidal.desktop",
            "com.deezer", "com.audible", "tv.plex", "com.plexapp", "com.soundcloud",
            "com.apple.Safari", "com.apple.WebKit.GPU", "com.google.Chrome", "company.thebrowser",
            "org.mozilla.firefox", "com.microsoft.edgemac", "com.brave.Browser", "com.operasoftware.Opera",
            "com.vivaldi.Vivaldi", "app.zen-browser",
        ]
        return prefixes.contains { bundleID.hasPrefix($0) }
    }

    nonisolated private static func defaultOutput() -> AudioObjectID? {
        value(AudioObjectID(kAudioObjectSystemObject), kAudioHardwarePropertyDefaultOutputDevice)
    }

    nonisolated private static func volumeAddress() -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: kAudioHardwareServiceDeviceProperty_VirtualMainVolume,
                                   mScope: kAudioDevicePropertyScopeOutput, mElement: kAudioObjectPropertyElementMain)
    }

    nonisolated private static func volume(of device: AudioObjectID) -> Float32? {
        var address = volumeAddress()
        var settable: DarwinBoolean = false
        guard AudioObjectHasProperty(device, &address),
              AudioObjectIsPropertySettable(device, &address, &settable) == noErr, settable.boolValue else { return nil }
        var volume: Float32 = 0
        var size = UInt32(MemoryLayout<Float32>.size)
        return AudioObjectGetPropertyData(device, &address, 0, nil, &size, &volume) == noErr ? volume : nil
    }

    nonisolated private static func setVolume(_ volume: Float32, on device: AudioObjectID) {
        var address = volumeAddress()
        var volume = min(max(volume, 0), 1)
        AudioObjectSetPropertyData(device, &address, 0, nil, UInt32(MemoryLayout<Float32>.size), &volume)
    }

    nonisolated private static func uid(of device: AudioObjectID) -> String? {
        var address = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyDeviceUID, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var uid: CFString = "" as CFString
        var size = UInt32(MemoryLayout<CFString>.size)
        let status = withUnsafeMutablePointer(to: &uid) { AudioObjectGetPropertyData(device, &address, 0, nil, &size, $0) }
        return status == noErr ? uid as String : nil
    }

    nonisolated private static func device(uid: String) -> AudioObjectID? {
        let devices: [AudioObjectID] = array(AudioObjectID(kAudioObjectSystemObject), kAudioHardwarePropertyDevices)
        return devices.first { self.uid(of: $0) == uid }
    }

    nonisolated private static func value<T>(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector) -> T? {
        var address = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var size = UInt32(MemoryLayout<T>.size)
        let pointer = UnsafeMutablePointer<T>.allocate(capacity: 1)
        defer { pointer.deallocate() }
        return AudioObjectGetPropertyData(object, &address, 0, nil, &size, pointer) == noErr ? pointer.pointee : nil
    }

    nonisolated private static func array<T>(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector) -> [T] {
        var address = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(object, &address, 0, nil, &size) == noErr, size > 0 else { return [] }
        let count = Int(size) / MemoryLayout<T>.stride
        let pointer = UnsafeMutablePointer<T>.allocate(capacity: count)
        defer { pointer.deallocate() }
        guard AudioObjectGetPropertyData(object, &address, 0, nil, &size, pointer) == noErr else { return [] }
        return Array(UnsafeBufferPointer(start: pointer, count: Int(size) / MemoryLayout<T>.stride))
    }
}

/// The system's media commands (the same as the ⏯ key, but pause and play
/// separately, so a pause can never start anything). Private framework: if
/// it's ever gone, Mindtalk simply doesn't pause.
enum MediaRemote {
    enum Command: Int { case play = 0, pause = 1 }

    private typealias Send = @convention(c) (Int, CFDictionary?) -> Bool
    private static let send: Send? = {
        guard let handle = dlopen("/System/Library/PrivateFrameworks/MediaRemote.framework/MediaRemote", RTLD_LAZY),
              let symbol = dlsym(handle, "MRMediaRemoteSendCommand") else { return nil }
        return unsafeBitCast(symbol, to: Send.self)
    }()

    static func send(_ command: Command) {
        _ = send?(command.rawValue, nil)
    }
}
