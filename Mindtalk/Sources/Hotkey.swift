import AppKit
import Carbon.HIToolbox

// MARK: - The dictation key
//
// Any single key can be the dictation key. Modifiers (⌥ ⌘ ⌃ ⇧ fn) type nothing on
// their own, so they pass through untouched; a regular key (F5, §, …) is swallowed
// so it doesn't also type into the app — except with ⌘/⌃/⌥ held, so shortcuts
// that use it keep working.

struct Hotkey: Codable, Equatable {
    var keyCode: UInt16
    /// Device-specific flag bit for modifier keys (tells right ⌥ from left ⌥); 0 for regular keys.
    var modifierMask: UInt64
    var isModifier: Bool { modifierMask != 0 }

    static let `default` = Hotkey(keyCode: UInt16(kVK_RightOption), modifierMask: 0x40)

    /// Modifier key codes → their device-specific bit in the event flags.
    static let modifierMasks: [UInt16: UInt64] = [
        UInt16(kVK_RightOption): 0x40,       // NX_DEVICERALTKEYMASK
        UInt16(kVK_Option): 0x20,            // NX_DEVICELALTKEYMASK
        UInt16(kVK_RightCommand): 0x10,      // NX_DEVICERCMDKEYMASK
        UInt16(kVK_Command): 0x08,           // NX_DEVICELCMDKEYMASK
        UInt16(kVK_RightControl): 0x2000,    // NX_DEVICERCTLKEYMASK
        UInt16(kVK_Control): 0x01,           // NX_DEVICELCTLKEYMASK
        UInt16(kVK_RightShift): 0x04,        // NX_DEVICERSHIFTKEYMASK
        UInt16(kVK_Shift): 0x02,             // NX_DEVICELSHIFTKEYMASK
        UInt16(kVK_Function): CGEventFlags.maskSecondaryFn.rawValue,
    ]

    var name: String {
        switch Int(keyCode) {
        case kVK_RightOption: return String(localized: "Höger ⌥ Option")
        case kVK_Option: return String(localized: "Vänster ⌥ Option")
        case kVK_RightCommand: return String(localized: "Höger ⌘ Command")
        case kVK_Command: return String(localized: "Vänster ⌘ Command")
        case kVK_RightControl: return String(localized: "Höger ⌃ Control")
        case kVK_Control: return String(localized: "Vänster ⌃ Control")
        case kVK_RightShift: return String(localized: "Höger ⇧ Shift")
        case kVK_Shift: return String(localized: "Vänster ⇧ Shift")
        case kVK_Function: return "fn / 🌐"
        case kVK_Space: return String(localized: "Mellanslag")
        case kVK_Return: return String(localized: "Retur")
        case kVK_Tab: return String(localized: "Tabb")
        case kVK_Delete: return String(localized: "Backsteg")
        case kVK_ForwardDelete: return "Delete"
        case kVK_Home: return "Home"
        case kVK_End: return "End"
        case kVK_PageUp: return "Page Up"
        case kVK_PageDown: return "Page Down"
        case kVK_LeftArrow: return String(localized: "←")
        case kVK_RightArrow: return String(localized: "→")
        case kVK_UpArrow: return String(localized: "↑")
        case kVK_DownArrow: return String(localized: "↓")
        case kVK_CapsLock: return "Caps Lock"
        default: break
        }
        if let f = Self.functionKeys[Int(keyCode)] { return "F\(f)" }
        return Self.character(for: keyCode).map { $0.uppercased() } ?? "Tangent \(keyCode)"
    }

    /// Short form for a key chip: "⌥ Opt →" (arrow = which side).
    var chip: String {
        switch Int(keyCode) {
        case kVK_RightOption: return "⌥ Opt →"
        case kVK_Option: return "⌥ Opt ←"
        case kVK_RightCommand: return "⌘ Cmd →"
        case kVK_Command: return "⌘ Cmd ←"
        case kVK_RightControl: return "⌃ Ctrl →"
        case kVK_Control: return "⌃ Ctrl ←"
        case kVK_RightShift: return "⇧ Shift →"
        case kVK_Shift: return "⇧ Shift ←"
        case kVK_Function: return "fn"
        default: return name
        }
    }

    /// For use mid-sentence ("Håll in höger ⌥ Option …").
    var inlineName: String {
        // Modifier names and Space are ordinary words mid-sentence: "höger ⌥ Option", "right ⌥ Option".
        let n = name
        return (isModifier && Int(keyCode) != kVK_Function) || Int(keyCode) == kVK_Space
            ? n.prefix(1).lowercased() + n.dropFirst() : n
    }

    /// Something worth knowing about this key.
    var note: String {
        if Int(keyCode) == kVK_Function {
            return String(localized: "Ställ in ”Tryck på 🌐 för att” till ”Gör ingenting” under Tangentbord i Systeminställningar.")
        }
        if isModifier {
            return String(localized: "Kortkommandon som ⌥2 för @ fungerar som vanligt.")
        }
        return String(localized: "Skriver inget eget medan Mindtalk körs.")
    }

    private static let functionKeys: [Int: Int] = [
        kVK_F1: 1, kVK_F2: 2, kVK_F3: 3, kVK_F4: 4, kVK_F5: 5, kVK_F6: 6, kVK_F7: 7, kVK_F8: 8,
        kVK_F9: 9, kVK_F10: 10, kVK_F11: 11, kVK_F12: 12, kVK_F13: 13, kVK_F14: 14, kVK_F15: 15,
        kVK_F16: 16, kVK_F17: 17, kVK_F18: 18, kVK_F19: 19, kVK_F20: 20,
    ]

    /// What the key types on the current keyboard layout (so "§" shows as §, not a code).
    private static func character(for keyCode: UInt16) -> String? {
        guard let source = TISCopyCurrentKeyboardLayoutInputSource()?.takeRetainedValue(),
              let ptr = TISGetInputSourceProperty(source, kTISPropertyUnicodeKeyLayoutData) else { return nil }
        let data = Unmanaged<CFData>.fromOpaque(ptr).takeUnretainedValue() as Data
        return data.withUnsafeBytes { raw -> String? in
            guard let layout = raw.bindMemory(to: UCKeyboardLayout.self).baseAddress else { return nil }
            var dead: UInt32 = 0
            var chars = [UniChar](repeating: 0, count: 4)
            var length = 0
            let status = UCKeyTranslate(layout, keyCode, UInt16(kUCKeyActionDisplay), 0,
                                        UInt32(LMGetKbdType()), OptionBits(kUCKeyTranslateNoDeadKeysBit),
                                        &dead, chars.count, &length, &chars)
            guard status == noErr, length > 0 else { return nil }
            let s = String(utf16CodeUnits: chars, count: length).trimmingCharacters(in: .whitespacesAndNewlines)
            return s.isEmpty ? nil : s
        }
    }
}

// MARK: - Global key listener

/// Listens to every key press system-wide through a CGEvent tap (needs Accessibility).
@MainActor
final class KeyListener {
    static let shared = KeyListener()

    var hotkey: Hotkey = Settings.hotkey
    var onPress: (() -> Void)?
    var onRelease: (() -> Void)?
    /// Another key was pressed while the dictation key is down (e.g. ⌥2 for @).
    var onChord: (() -> Void)?
    /// Escape. Return true to swallow it (we're recording and it means "cancel").
    var onEscape: (() -> Bool)?
    /// Space while the dictation key is held. Return true to swallow it (it locks the dictation).
    var onSpaceWhileHeld: (() -> Bool)?
    /// ⌃⌥V: type the last dictation again. Always swallowed.
    var onPasteLast: (() -> Void)?
    /// Set while the user is picking a new key; the next key press is handed here.
    var capture: ((Hotkey) -> Void)?

    private(set) var isRunning = false
    private var tap: CFMachPort?
    private var source: CFRunLoopSource?
    private var isDown = false

    @discardableResult
    func start() -> Bool {
        guard !isRunning else { return true }
        let mask: CGEventMask = (1 << CGEventType.flagsChanged.rawValue)
            | (1 << CGEventType.keyDown.rawValue) | (1 << CGEventType.keyUp.rawValue)
        guard let tap = CGEvent.tapCreate(
            tap: .cgSessionEventTap, place: .headInsertEventTap, options: .defaultTap,
            eventsOfInterest: mask,
            callback: { _, type, event, _ in
                // The tap's run-loop source lives on the main run loop.
                MainActor.assumeIsolated { KeyListener.shared.handle(type, event) }
            },
            userInfo: nil
        ) else { return false }
        self.tap = tap
        let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        self.source = source
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        isRunning = true
        return true
    }

    private func handle(_ type: CGEventType, _ event: CGEvent) -> Unmanaged<CGEvent>? {
        let pass = Unmanaged.passUnretained(event)
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            if let tap { CGEvent.tapEnable(tap: tap, enable: true) }
            return pass
        }
        let keyCode = UInt16(event.getIntegerValueField(.keyboardEventKeycode))
        let isRepeat = event.getIntegerValueField(.keyboardEventAutorepeat) != 0

        // Picking a new key.
        if let capture {
            if type == .flagsChanged, let mask = Hotkey.modifierMasks[keyCode] {
                guard event.flags.rawValue & mask != 0 else { return pass }   // wait for the press
                self.capture = nil
                capture(Hotkey(keyCode: keyCode, modifierMask: mask))
                return pass
            }
            guard type == .keyDown else { return type == .keyUp ? nil : pass }
            self.capture = nil
            if Int(keyCode) != kVK_Escape { capture(Hotkey(keyCode: keyCode, modifierMask: 0)) }
            else { capture(hotkey) }                                          // Esc = keep the old one
            return nil
        }

        // ⌃⌥V — "paste the last dictation again". Holding right ⌥ for it also
        // started a dictation; drop that first.
        if type == .keyDown, Int(keyCode) == kVK_ANSI_V, !isRepeat,
           event.flags.contains([.maskControl, .maskAlternate]), !event.flags.contains(.maskCommand) {
            if isDown { onChord?() }
            onPasteLast?()
            return nil
        }
        if type == .keyUp, Int(keyCode) == kVK_ANSI_V, event.flags.contains([.maskControl, .maskAlternate]) { return nil }

        if isDown, type == .keyDown, Int(keyCode) == kVK_Space, keyCode != hotkey.keyCode {
            if onSpaceWhileHeld?() == true { return nil }
        }

        if hotkey.isModifier {
            if type == .flagsChanged && keyCode == hotkey.keyCode {
                setDown(event.flags.rawValue & hotkey.modifierMask != 0)
                return pass
            }
            if isDown, type == .keyDown || type == .flagsChanged { onChord?() }
        } else if keyCode == hotkey.keyCode, type == .keyDown || type == .keyUp {
            let shortcut = event.flags.intersection([.maskCommand, .maskControl, .maskAlternate])
            if type == .keyDown, !isDown, !shortcut.isEmpty { return pass }   // ⌘/⌃/⌥ + key: not ours
            if !isRepeat { setDown(type == .keyDown) }
            return nil
        }

        if type == .keyDown, Int(keyCode) == kVK_Escape, !isRepeat, onEscape?() == true { return nil }
        return pass
    }

    private func setDown(_ down: Bool) {
        guard down != isDown else { return }
        isDown = down
        down ? onPress?() : onRelease?()
    }

    /// Forget a half-finished press (e.g. after changing the key).
    func reset() { isDown = false }
}
