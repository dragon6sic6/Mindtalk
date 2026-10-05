import AppKit
import Carbon.HIToolbox
import os

// MARK: - The dictation key
//
// A single key or a combination of modifiers can be the dictation shortcut.
// Modifiers (⌥ ⌘ ⌃ ⇧ fn) pass through untouched; a regular key (F5, §,…) is swallowed
// so it doesn't also type into the app — except with ⌘/⌃/⌥ held, so shortcuts
// that use it keep working.

struct Hotkey: Codable, Equatable {
    var keyCode: UInt16
    /// Required device-specific modifier bits (tells right ⌥ from left ⌥); 0 for regular keys.
    /// Keeping the original fields also preserves saved single-key shortcuts.
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

    private static let modifierOrder = [
        kVK_Control, kVK_RightControl, kVK_Option, kVK_RightOption,
        kVK_Shift, kVK_RightShift, kVK_Command, kVK_RightCommand, kVK_Function,
    ].map { UInt16($0) }

    static let allModifierMasks = modifierMasks.values.reduce(UInt64(0), |)

    var modifierKeys: [Hotkey] {
        Self.modifierOrder.compactMap { code in
            guard let mask = Self.modifierMasks[code], modifierMask & mask != 0 else { return nil }
            return Hotkey(keyCode: code, modifierMask: mask)
        }
    }

    static func modifierCombination(mask: UInt64) -> Hotkey? {
        let mask = mask & allModifierMasks
        guard let code = modifierOrder.first(where: { mask & modifierMasks[$0]! != 0 }) else { return nil }
        return Hotkey(keyCode: code, modifierMask: mask)
    }

    func containsModifier(_ code: UInt16) -> Bool {
        guard let mask = Self.modifierMasks[code] else { return false }
        return modifierMask & mask != 0
    }

    func modifiersAreDown(in flags: CGEventFlags) -> Bool {
        isModifier && flags.rawValue & modifierMask == modifierMask
    }

    /// Some hardware sources omit sided bits. Fall back only if neither side of
    /// that modifier family is reported, so left ⌘ cannot satisfy right ⌘.
    func modifiersPhysicallyDown(in flags: CGEventFlags) -> Bool {
        let keys = modifierKeys
        guard isModifier, !keys.isEmpty else { return false }
        return keys.allSatisfy { key in
            if flags.rawValue & key.modifierMask != 0 { return true }
            let family: CGEventFlags
            let sides: UInt64
            switch Int(key.keyCode) {
            case kVK_Option, kVK_RightOption: family = .maskAlternate; sides = 0x60
            case kVK_Command, kVK_RightCommand: family = .maskCommand; sides = 0x18
            case kVK_Control, kVK_RightControl: family = .maskControl; sides = 0x2001
            case kVK_Shift, kVK_RightShift: family = .maskShift; sides = 0x06
            default: return false // fn has no separate sided bits.
            }
            return flags.contains(family) && flags.rawValue & sides == 0
        }
    }

    var name: String {
        if modifierKeys.count > 1 { return modifierKeys.map(\.name).joined(separator: " + ") }
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
        return Self.character(for: keyCode).map { $0.uppercased() } ?? String(localized: "Tangent \(Int(keyCode))")
    }

    /// Short form for a key chip: "⌥ Opt →" (arrow = which side).
    var chip: String {
        if modifierKeys.count > 1 { return modifierKeys.map(\.chip).joined(separator: " + ") }
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

    /// For use mid-sentence ("Håll in höger ⌥ Option…").
    var inlineName: String {
        if modifierKeys.count > 1 { return modifierKeys.map(\.inlineName).joined(separator: " + ") }
        // Modifier names and Space are ordinary words mid-sentence: "höger ⌥ Option", "right ⌥ Option".
        let n = name
        return (isModifier && Int(keyCode) != kVK_Function) || Int(keyCode) == kVK_Space
            ? n.prefix(1).lowercased() + n.dropFirst() : n
    }

    /// Something worth knowing about this key.
    var note: String {
        if modifierKeys.count > 1 {
            let instructions = String(localized: "Håll in alla valda modifierartangenter samtidigt. Kortkommandon fungerar som vanligt.")
            if containsModifier(UInt16(kVK_Function)) {
                return instructions + " " + String(localized: "Ställ in ”Tryck på 🌐 för att” till ”Gör ingenting” under Tangentbord i Systeminställningar.")
            }
            return instructions
        }
        if Int(keyCode) == kVK_Function {
            return String(localized: "Ställ in ”Tryck på 🌐 för att” till ”Gör ingenting” under Tangentbord i Systeminställningar.")
        }
        switch Int(keyCode) {
        case kVK_Option, kVK_RightOption:
            return String(localized: "Kortkommandon som ⌥2 för @ fungerar som vanligt.")
        case kVK_Command, kVK_RightCommand:
            return String(localized: "Kortkommandon som ⌘C fungerar som vanligt.")
        case kVK_Control, kVK_RightControl, kVK_Shift, kVK_RightShift:
            return String(localized: "Kortkommandon och versaler fungerar som vanligt.")
        default: break
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
    /// Regular keys are captured on press; modifier combinations on final release.
    var capture: ((Hotkey) -> Void)? {
        didSet {
            capturedModifierMask = 0
            // A modifier already down when picking starts (⇧ held while clicking the
            // field) isn't part of the choice.
            heldOver = capture == nil ? 0 : modifiersHeldNow() & Hotkey.allModifierMasks
        }
    }
    /// The modifiers down right now, as the system reports them.
    var modifiersHeldNow: () -> UInt64 = { CGEventSource.flagsState(.combinedSessionState).rawValue }

    private(set) var isRunning = false
    private var tap: CFMachPort?
    private var source: CFRunLoopSource?
    private var isDown = false
    private var capturedModifierMask: UInt64 = 0
    /// Modifiers that were down before picking began; each stops counting once let go.
    private var heldOver: UInt64 = 0
    /// A typed shortcut during a partial modifier combination must not turn into
    /// dictation when a home-row modifier arrives a moment later.
    private var modifierShortcutUsed = false
    /// fn is held — known only from the fn key's own events: arrow, function and
    /// navigation keys carry the fn flag whether or not it is.
    private var fnDown = false

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
            // Events went missing while the tap was off — maybe the release.
            resync()
            return pass
        }
        let keyCode = UInt16(event.getIntegerValueField(.keyboardEventKeycode))
        let isRepeat = event.getIntegerValueField(.keyboardEventAutorepeat) != 0
        if type == .flagsChanged, Int(keyCode) == kVK_Function { fnDown = event.flags.contains(.maskSecondaryFn) }

        // Picking a new key or a simultaneously held group of modifiers. Wait
        // until all modifiers are up before saving, so the capture cannot also
        // begin a dictation. Keep the largest snapshot, not a union of presses.
        if let capture {
            if type == .flagsChanged, Hotkey.modifierMasks[keyCode] != nil {
                heldOver &= event.flags.rawValue
                let held = event.flags.rawValue & Hotkey.allModifierMasks & ~heldOver
                if held.nonzeroBitCount > capturedModifierMask.nonzeroBitCount {
                    capturedModifierMask = held
                }
                if held == 0, let key = Hotkey.modifierCombination(mask: capturedModifierMask) {
                    self.capture = nil
                    capture(key)
                }
                return pass
            }
            guard type == .keyDown else { return type == .keyUp ? nil : pass }
            if Int(keyCode) != kVK_Escape, capturedModifierMask != 0 { return nil }
            self.capture = nil
            if Int(keyCode) != kVK_Escape { capture(Hotkey(keyCode: keyCode, modifierMask: 0)) }
            else { capture(hotkey) }                                          // Esc = keep the old one
            return nil
        }

        // ⌃⌥V — "paste the last dictation again". Holding right ⌥ for it also
        // started a dictation; drop that first.
        if type == .keyDown, Int(keyCode) == kVK_ANSI_V, !isRepeat,
           event.flags.contains([.maskControl, .maskAlternate]), !event.flags.contains(.maskCommand) {
            if hotkey.modifierKeys.count > 1, event.flags.rawValue & hotkey.modifierMask != 0 {
                modifierShortcutUsed = true
            }
            if isDown { later { $0.onChord?() } }
            later { $0.onPasteLast?() }
            return nil
        }
        if type == .keyUp, Int(keyCode) == kVK_ANSI_V, event.flags.contains([.maskControl, .maskAlternate]) { return nil }

        if isDown, type == .keyDown, Int(keyCode) == kVK_Space, keyCode != hotkey.keyCode {
            if onSpaceWhileHeld?() == true { return nil }
        }

        if hotkey.isModifier {
            if type == .flagsChanged && hotkey.containsModifier(keyCode) {
                if event.flags.rawValue & hotkey.modifierMask == 0 { modifierShortcutUsed = false }
                if !hotkey.modifiersAreDown(in: event.flags) { setDown(false) }
                else if !modifierShortcutUsed { setDown(true) }
                return pass
            }
            let modifierPressed = type == .flagsChanged
                && Hotkey.modifierMasks[keyCode].map { event.flags.rawValue & $0 != 0 } == true
            // Which of the combination is held while this key goes down. An arrow key
            // on its own says "fn" too — that isn't a shortcut typed with fn held, and
            // must not block the next dictation.
            var members = event.flags.rawValue & hotkey.modifierMask
            if type == .keyDown, !fnDown { members &= ~CGEventFlags.maskSecondaryFn.rawValue }
            if hotkey.modifierKeys.count > 1, members != 0, type == .keyDown || modifierPressed {
                modifierShortcutUsed = true
            }
            if isDown, type == .keyDown || modifierPressed
                || (hotkey.modifierKeys.count == 1 && type == .flagsChanged) {
                later { $0.onChord?() }
            }
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
        later { down ? $0.onPress?() : $0.onRelease?() }
    }

    /// Runs work after the tap has handed the event back. Starting the mic can take
    /// a moment (longer with Bluetooth), and every key on the Mac waits while the
    /// tap callback runs — so the callback only records what happened. The main
    /// queue keeps the order.
    private func later(_ work: @escaping @MainActor (KeyListener) -> Void) {
        DispatchQueue.main.async { work(self) }
    }

    /// Is the dictation key held down right now, according to the hardware? Asks the
    /// keyboard and the session both, and says "up" only if both do — ending a
    /// dictation you're holding is worse than one missed release.
    var keyPhysicallyDown: Bool {
        isDown(in: .hidSystemState) || isDown(in: .combinedSessionState)
    }

    private func isDown(in state: CGEventSourceStateID) -> Bool {
        if hotkey.isModifier {
            return hotkey.modifiersPhysicallyDown(in: CGEventSource.flagsState(state))
        }
        return CGEventSource.keyState(state, key: CGKeyCode(hotkey.keyCode))
    }

    /// Brings `isDown` in line with the hardware — after the tap was switched off,
    /// after sleep, or whenever a release may have been missed.
    func resync() {
        if isDown && !keyPhysicallyDown {
            let flags = CGEventSource.flagsState(.combinedSessionState).rawValue
            let hid = CGEventSource.flagsState(.hidSystemState).rawValue
            os_log("resync: key reported up (combined %{public}llx, hid %{public}llx, mask %{public}llx)",
                   log: Self.diagnostics, type: .info, flags, hid, UInt64(hotkey.modifierMask))
            setDown(false)
        }
        // A release that went missing (the tap was off) must not leave a combination
        // blocked, or fn counted as held: the hardware has the last word.
        let hid = CGEventSource.flagsState(.hidSystemState)
        if hid.rawValue & hotkey.modifierMask == 0 { modifierShortcutUsed = false }
        if !hid.contains(.maskSecondaryFn) { fnDown = false }
    }

    private static let diagnostics = OSLog(subsystem: "ai.mindact.mindtalk", category: "Keys")

    /// Forget a half-finished press (e.g. after changing the key).
    func reset() {
        isDown = false
        modifierShortcutUsed = false
        fnDown = false
        capturedModifierMask = 0
        heldOver = 0
    }

    #if DEBUG
    /// Feed synthetic events through the real routing without installing a tap.
    func handleForTesting(_ type: CGEventType, _ event: CGEvent) -> Unmanaged<CGEvent>? {
        handle(type, event)
    }
    #endif
}
