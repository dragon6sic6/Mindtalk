import AVFoundation
import Combine
import CoreAudio

// MARK: - Choosing the microphone
//
// The built-in mic is the default: a Bluetooth headset (AirPods …) drops to a
// low-quality call profile the moment its mic opens, which hurts recognition
// and your music. "Automatiskt" follows the system input instead, or pick any
// other device. A chosen device that's gone falls back to the system input.

struct InputDevice: Identifiable, Equatable {
    let id: AudioDeviceID
    let uid: String
    let name: String
    let transport: UInt32

    var isBuiltIn: Bool { transport == kAudioDeviceTransportTypeBuiltIn }
    var isBluetooth: Bool {
        transport == kAudioDeviceTransportTypeBluetooth || transport == kAudioDeviceTransportTypeBluetoothLE
    }

    /// SF Symbol for what kind of mic this is.
    var icon: String {
        switch transport {
        case kAudioDeviceTransportTypeBuiltIn: return "laptopcomputer"
        case kAudioDeviceTransportTypeBluetooth, kAudioDeviceTransportTypeBluetoothLE: return "headphones"
        case kAudioDeviceTransportTypeContinuityCaptureWired, kAudioDeviceTransportTypeContinuityCaptureWireless: return "iphone"
        case kAudioDeviceTransportTypeUSB: return "mic"
        default: return isVirtual ? "square.stack.3d.up" : "mic"
        }
    }
    /// Aggregate/virtual devices (Zoom, BlackHole, Teams …) — hidden by default.
    var isVirtual: Bool {
        transport == kAudioDeviceTransportTypeVirtual || transport == kAudioDeviceTransportTypeAggregate
            || transport == kAudioDeviceTransportTypeUnknown
    }
}

enum MicChoice: Equatable {
    case builtIn
    case system
    case device(uid: String)

    init(raw: String?) {
        switch raw {
        case nil, "builtin": self = .builtIn
        case "system": self = .system
        case let uid?: self = .device(uid: uid)
        }
    }

    var raw: String {
        switch self {
        case .builtIn: return "builtin"
        case .system: return "system"
        case .device(let uid): return uid
        }
    }
}

@MainActor
final class Microphones: ObservableObject {
    static let shared = Microphones()

    @Published private(set) var devices: [InputDevice] = []
    @Published private(set) var systemDefault: InputDevice?
    @Published var choice = MicChoice(raw: UserDefaults.standard.string(forKey: "microphone")) {
        didSet {
            UserDefaults.standard.set(choice.raw, forKey: "microphone")
            if monitoring { startMonitor() }
        }
    }
    /// Live level of the chosen mic while the picker is open (0…1).
    @Published private(set) var level: Float = 0
    /// The last few seconds of levels, oldest first — drawn as a waveform.
    @Published private(set) var history = [Float](repeating: 0, count: Microphones.historyLength)
    static let historyLength = 48

    private var monitor: Recorder?
    private var monitoring = false

    private init() {
        refresh()
        // Plugging in or unplugging a mic, or changing the system input.
        var addresses = [
            AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDevices,
                                       mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain),
            AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultInputDevice,
                                       mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain),
        ]
        for i in addresses.indices {
            AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &addresses[i], .main) { _, _ in
                MainActor.assumeIsolated { Microphones.shared.refresh() }
            }
        }
    }

    func refresh() {
        devices = Self.inputDevices()
        let defaultID = Self.defaultInputID()
        systemDefault = devices.first { $0.id == defaultID }
    }

    var builtIn: InputDevice? { devices.first(where: \.isBuiltIn) }

    /// The device to record from; nil means the system input.
    var selectedDeviceID: AudioDeviceID? {
        switch choice {
        case .builtIn: return builtIn?.id
        case .system: return nil
        case .device(let uid): return devices.first { $0.uid == uid }?.id
        }
    }

    /// "Inbyggd mikrofon", "Automatiskt (AirPods)", "iPhone-mikrofon" …
    var selectionName: String {
        switch choice {
        case .builtIn: return builtIn == nil ? systemName : String(localized: "Inbyggd mikrofon")
        case .system: return systemName
        case .device(let uid): return devices.first { $0.uid == uid }?.name ?? String(localized: "\(systemName) – vald mikrofon saknas")
        }
    }

    var systemName: String {
        systemDefault.map { String(localized: "Automatiskt (\($0.name))") } ?? String(localized: "Automatiskt")
    }

    // MARK: Level preview

    func startMonitor() {
        monitoring = true
        _ = monitor?.stop()
        let recorder = Recorder(keepSamples: false)
        recorder.onLevel = { value in Task { @MainActor in Microphones.shared.push(value) } }
        try? recorder.start(device: selectedDeviceID)
        monitor = recorder
    }

    private var lastPush = Date.distantPast

    private func push(_ value: Float) {
        level = value
        // ~16 samples a second, so the waveform scrolls at a calm pace.
        guard Date().timeIntervalSince(lastPush) > 0.06 else { return }
        lastPush = Date()
        history.removeFirst()
        history.append(value)
    }

    /// How well the chosen mic hears you, from the last second of levels.
    var hearing: (text: String, good: Bool?) {
        let peak = history.suffix(16).max() ?? 0
        if peak > 0.3 { return (String(localized: "Vi hör dig tydligt"), true) }
        if peak > 0.07 { return (String(localized: "Lite svagt – prata närmare mikrofonen"), false) }
        return (String(localized: "Säg något för att testa"), nil)
    }

    func stopMonitor() {
        monitoring = false
        _ = monitor?.stop()
        monitor = nil
        level = 0
        history = [Float](repeating: 0, count: Self.historyLength)
    }

    // MARK: Core Audio

    private static func inputDevices() -> [InputDevice] {
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDevices,
                                                 mScope: kAudioObjectPropertyScopeGlobal,
                                                 mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size) == noErr
        else { return [] }
        var ids = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &ids) == noErr
        else { return [] }
        return ids.compactMap { id in
            guard hasInput(id), let uid = string(id, kAudioDevicePropertyDeviceUID),
                  let name = string(id, kAudioObjectPropertyName) else { return nil }
            return InputDevice(id: id, uid: uid, name: name, transport: uint32(id, kAudioDevicePropertyTransportType) ?? 0)
        }
    }

    /// The system's current input device.
    nonisolated static func defaultInputID() -> AudioDeviceID? {
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultInputDevice,
                                                 mScope: kAudioObjectPropertyScopeGlobal,
                                                 mElement: kAudioObjectPropertyElementMain)
        var id = AudioDeviceID(0)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &id) == noErr,
              id != 0 else { return nil }
        return id
    }

    private static func hasInput(_ id: AudioDeviceID) -> Bool {
        var address = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyStreams,
                                                 mScope: kAudioDevicePropertyScopeInput,
                                                 mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        return AudioObjectGetPropertyDataSize(id, &address, 0, nil, &size) == noErr && size > 0
    }

    private static func string(_ id: AudioDeviceID, _ selector: AudioObjectPropertySelector) -> String? {
        var address = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal,
                                                 mElement: kAudioObjectPropertyElementMain)
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(id, &address, 0, nil, &size, &value) == noErr, let value else { return nil }
        return value.takeRetainedValue() as String
    }

    private static func uint32(_ id: AudioDeviceID, _ selector: AudioObjectPropertySelector) -> UInt32? {
        var address = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal,
                                                 mElement: kAudioObjectPropertyElementMain)
        var value: UInt32 = 0
        var size = UInt32(MemoryLayout<UInt32>.size)
        guard AudioObjectGetPropertyData(id, &address, 0, nil, &size, &value) == noErr else { return nil }
        return value
    }
}
