#if DEBUG
import AppKit
import Carbon.HIToolbox

/// Drives the listener with local CGEvents. Nothing is posted to the keyboard,
/// no event tap is installed, and settings are never changed.
@MainActor
enum HotkeySelfTest {
    private static let option = UInt16(kVK_RightOption)
    private static let command = UInt16(kVK_RightCommand)
    private static let comboMask: UInt64 = 0x50
    private static let optionFlags = flags(0x40, [.maskAlternate])
    private static let commandFlags = flags(0x10, [.maskCommand])
    private static let comboFlags = flags(comboMask, [.maskAlternate, .maskCommand])

    static func run() async -> Int32 {
        do {
            try modelAndFlags()
            try await modifierTransitions()
            try await shortcutsAndRollover()
            try await lockAndCancel()
            try await regularKeys()
            try await capture()
            try await reset()
            print("Hotkey self-test passed (model, modifier combinations, shortcuts, capture and reset).")
            return 0
        } catch {
            print("Hotkey self-test failed: \(error.localizedDescription)")
            return 1
        }
    }

    private static func modelAndFlags() throws {
        let legacy = Data(#"{"keyCode":61,"modifierMask":64}"#.utf8)
        let decoded = try JSONDecoder().decode(Hotkey.self, from: legacy)
        try require(decoded == .default, "Legacy right Option setting changed")
        let reencoded = try JSONEncoder().encode(decoded)
        let roundTrip = try JSONDecoder().decode(Hotkey.self, from: reencoded)
        try require(roundTrip == decoded, "Legacy setting did not round-trip")

        let combo = try combination(comboMask)
        let combinedRoundTrip = try JSONDecoder().decode(Hotkey.self, from: JSONEncoder().encode(combo))
        try require(combinedRoundTrip == combo, "Modifier combination did not round-trip")
        try require(combo.keyCode == option && combo.isModifier, "Combination has no canonical modifier key")
        try require(combo.modifierKeys.map(\.keyCode) == [option, command], "Combination omitted or reordered members")
        try require(combo.name.contains("⌥") && combo.name.contains("⌘"), "Combination name omitted a modifier")
        try require(combo.chip.contains("⌥") && combo.chip.contains("⌘"), "Combination chip omitted a modifier")
        try require(!combo.inlineName.isEmpty && !combo.note.isEmpty, "Combination explanatory labels are empty")
        try require(Hotkey.modifierCombination(mask: 0) == nil, "Empty modifier combination was accepted")
        try require(Hotkey.modifierCombination(mask: 0x8000) == nil, "Unknown modifier bit was accepted")
        for (keyCode, mask) in Hotkey.modifierMasks {
            try require(Hotkey.modifierCombination(mask: mask) == Hotkey(keyCode: keyCode, modifierMask: mask),
                        "Single modifier \(keyCode) changed representation")
        }

        try require(combo.modifiersAreDown(in: comboFlags), "Complete combination was not down")
        try require(!combo.modifiersAreDown(in: optionFlags), "Option alone activated the combination")
        try require(!combo.modifiersAreDown(in: commandFlags), "Command alone activated the combination")
        let wrongOption = flags(0x30, [.maskAlternate, .maskCommand])
        let wrongCommand = flags(0x48, [.maskAlternate, .maskCommand])
        try require(!combo.modifiersAreDown(in: wrongOption), "Left Option substituted for right Option")
        try require(!combo.modifiersAreDown(in: wrongCommand), "Left Command substituted for right Command")
        let sideless: CGEventFlags = [.maskAlternate, .maskCommand]
        try require(!combo.modifiersAreDown(in: sideless), "Side-less event flags activated a sided hotkey")
        try require(combo.modifiersPhysicallyDown(in: comboFlags), "Physical complete combination was not down")
        try require(combo.modifiersPhysicallyDown(in: sideless), "Side-less hardware fallback lost the combination")
        try require(combo.modifiersPhysicallyDown(in: flags(0x40, [.maskAlternate, .maskCommand])),
                    "Hardware fallback did not operate per modifier family")
        try require(!combo.modifiersPhysicallyDown(in: wrongOption), "Hardware fallback accepted known wrong Option side")
        try require(!combo.modifiersPhysicallyDown(in: wrongCommand), "Hardware fallback accepted known wrong Command side")
        try require(!combo.modifiersPhysicallyDown(in: optionFlags), "Hardware fallback accepted a missing family")
        print("Passed hotkey model and sided flags")
    }

    private static func modifierTransitions() async throws {
        let combo = try combination(comboMask)
        for firstIsOption in [true, false] {
            let probe = Probe(hotkey: combo)
            let first = firstIsOption ? option : command
            let second = firstIsOption ? command : option
            let firstFlags = firstIsOption ? optionFlags : commandFlags
            let remainingFlags = firstIsOption ? commandFlags : optionFlags
            try require(probe.send(.flagsChanged, first, firstFlags), "First modifier was swallowed")
            await drainCallbacks()
            try require(probe.calls.isEmpty, "First modifier activated the combination")
            try require(probe.send(.flagsChanged, second, comboFlags), "Completed combination was swallowed")
            _ = try probe.send(.flagsChanged, second, comboFlags)
            await drainCallbacks()
            try require(probe.calls == ["press"], "Completion did not press exactly once")
            _ = try probe.send(.flagsChanged, first, remainingFlags)
            await drainCallbacks()
            try require(probe.calls == ["press", "release"], "First member release did not end exactly once")
            // Re-completing while the other member remains down is another tap.
            _ = try probe.send(.flagsChanged, first, comboFlags)
            _ = try probe.send(.flagsChanged, second, firstFlags)
            _ = try probe.send(.flagsChanged, first, [])
            await drainCallbacks()
            try require(probe.calls == ["press", "release", "press", "release"],
                        "Re-completion or member releases produced extra callbacks")
        }

        let single = Probe(hotkey: .default)
        _ = try single.send(.flagsChanged, UInt16(kVK_Option), flags(0x20, [.maskAlternate]))
        _ = try single.send(.flagsChanged, option, flags(0x60, [.maskAlternate]))
        _ = try single.send(.flagsChanged, option, flags(0x20, [.maskAlternate]))
        await drainCallbacks()
        try require(single.calls == ["press", "release"], "Single right Option lost sided behavior")
        let singleChord = Probe(hotkey: .default)
        _ = try singleChord.send(.flagsChanged, option, optionFlags)
        _ = try singleChord.send(.flagsChanged, UInt16(kVK_Shift), flags(0x42, [.maskAlternate, .maskShift]))
        _ = try singleChord.send(.flagsChanged, UInt16(kVK_Shift), optionFlags)
        _ = try singleChord.send(.flagsChanged, option, [])
        await drainCallbacks()
        try require(singleChord.calls == ["press", "chord", "chord", "release"],
                    "Single modifier no longer chords on unrelated modifier releases")
        print("Passed modifier press orders, release and re-completion")
    }

    private static func shortcutsAndRollover() async throws {
        let combo = try combination(comboMask)
        let probe = Probe(hotkey: combo)
        _ = try probe.send(.flagsChanged, option, optionFlags)
        _ = try probe.send(.flagsChanged, command, comboFlags)
        try require(probe.send(.keyDown, UInt16(kVK_ANSI_C), comboFlags), "Command shortcut was swallowed")
        _ = try probe.send(.keyUp, UInt16(kVK_ANSI_C), comboFlags)
        _ = try probe.send(.flagsChanged, UInt16(kVK_Shift), flags(0x52, [.maskAlternate, .maskCommand, .maskShift]))
        _ = try probe.send(.flagsChanged, UInt16(kVK_Shift), comboFlags)
        _ = try probe.send(.flagsChanged, option, commandFlags)
        _ = try probe.send(.flagsChanged, command, [])
        await drainCallbacks()
        try require(probe.calls == ["press", "chord", "chord", "release"],
                    "Extra key/modifier presses or modifier releases did not chord correctly")

        for useModifier in [false, true] {
            let rollover = Probe(hotkey: combo)
            _ = try rollover.send(.flagsChanged, option, optionFlags)
            if useModifier {
                _ = try rollover.send(.flagsChanged, UInt16(kVK_Shift), flags(0x42, [.maskAlternate, .maskShift]))
                _ = try rollover.send(.flagsChanged, UInt16(kVK_Shift), optionFlags)
            } else {
                try require(rollover.send(.keyDown, UInt16(kVK_ANSI_2), optionFlags), "Option typing shortcut was swallowed")
                _ = try rollover.send(.keyUp, UInt16(kVK_ANSI_2), optionFlags)
            }
            _ = try rollover.send(.flagsChanged, command, comboFlags)
            _ = try rollover.send(.flagsChanged, command, optionFlags)
            _ = try rollover.send(.flagsChanged, command, comboFlags)
            await drainCallbacks()
            try require(rollover.calls.isEmpty, "Typing/modifier rollover activated a combination")
            _ = try rollover.send(.flagsChanged, option, commandFlags)
            _ = try rollover.send(.flagsChanged, command, [])
            _ = try rollover.send(.flagsChanged, command, commandFlags)
            _ = try rollover.send(.flagsChanged, option, comboFlags)
            _ = try rollover.send(.flagsChanged, command, optionFlags)
            _ = try rollover.send(.flagsChanged, option, [])
            await drainCallbacks()
            try require(rollover.calls == ["press", "release"], "Rollover gate did not clear after all members released")
        }
        let paste = Probe(hotkey: combo)
        _ = try paste.send(.flagsChanged, UInt16(kVK_Control), flags(0x01, [.maskControl]))
        _ = try paste.send(.flagsChanged, option, flags(0x41, [.maskControl, .maskAlternate]))
        try require(!paste.send(.keyDown, UInt16(kVK_ANSI_V), flags(0x41, [.maskControl, .maskAlternate])),
                    "Paste-last shortcut was not swallowed")
        _ = try paste.send(.keyUp, UInt16(kVK_ANSI_V), flags(0x41, [.maskControl, .maskAlternate]))
        _ = try paste.send(.flagsChanged, UInt16(kVK_Control), optionFlags)
        _ = try paste.send(.flagsChanged, command, comboFlags)
        _ = try paste.send(.flagsChanged, command, optionFlags)
        _ = try paste.send(.flagsChanged, option, [])
        await drainCallbacks()
        try require(paste.calls == ["paste"], "Paste-last shortcut rollover also started dictation")
        print("Passed shortcut passthrough and home-row rollover")
    }

    private static func lockAndCancel() async throws {
        let probe = Probe(hotkey: try combination(comboMask))
        probe.listener.onSpaceWhileHeld = { [weak probe] in probe?.calls.append("space"); return true }
        probe.listener.onEscape = { [weak probe] in probe?.calls.append("escape"); return true }
        _ = try probe.send(.flagsChanged, option, optionFlags)
        _ = try probe.send(.flagsChanged, command, comboFlags)
        await drainCallbacks()
        try require(!probe.send(.keyDown, UInt16(kVK_Space), comboFlags), "Space lock was not swallowed")
        _ = try probe.send(.keyUp, UInt16(kVK_Space), comboFlags)
        await drainCallbacks()
        try require(probe.calls == ["press", "space"], "Space lock did not run once without chording")
        try require(!probe.send(.keyDown, UInt16(kVK_Escape), comboFlags), "Escape cancellation was not swallowed")
        _ = try probe.send(.keyUp, UInt16(kVK_Escape), comboFlags)
        await drainCallbacks()
        try require(probe.calls.filter { $0 == "escape" }.count == 1, "Escape cancellation did not run exactly once")
        _ = try probe.send(.flagsChanged, option, commandFlags)
        _ = try probe.send(.flagsChanged, command, [])
        await drainCallbacks()
        try require(probe.calls.last == "release", "Lock/cancel prevented the modifier release callback")
        print("Passed Space lock and Escape cancellation")
    }

    private static func regularKeys() async throws {
        let key = UInt16(kVK_F5)
        let probe = Probe(hotkey: Hotkey(keyCode: key, modifierMask: 0))
        try require(!probe.send(.keyDown, key), "Regular dictation key-down was not swallowed")
        try require(!probe.send(.keyDown, key, repeatKey: true), "Regular key repeat was not swallowed")
        try require(!probe.send(.keyUp, key), "Regular dictation key-up was not swallowed")
        await drainCallbacks()
        try require(probe.calls == ["press", "release"], "Regular key repeat produced extra callbacks")
        try require(probe.send(.keyDown, key, [.maskCommand]), "Command + regular dictation key was swallowed")
        _ = try probe.send(.keyUp, key, [.maskCommand])
        await drainCallbacks()
        try require(probe.calls == ["press", "release"], "Regular-key shortcut activated dictation")
        print("Passed regular dictation keys and shortcuts")
    }

    private static func capture() async throws {
        let combo = try combination(comboMask)
        let probe = Probe(hotkey: .default)
        var captured: [Hotkey] = []
        probe.listener.capture = { captured.append($0) }
        try require(probe.send(.flagsChanged, option, optionFlags), "Captured modifier was swallowed")
        _ = try probe.send(.flagsChanged, command, comboFlags)
        try require(!probe.send(.keyDown, UInt16(kVK_ANSI_C), comboFlags), "Key during modifier capture was not swallowed")
        _ = try probe.send(.keyUp, UInt16(kVK_ANSI_C), comboFlags)
        await drainCallbacks()
        try require(captured.isEmpty, "Modifier capture committed before release")
        _ = try probe.send(.flagsChanged, option, commandFlags)
        try require(captured.isEmpty, "Modifier capture committed on first member release")
        _ = try probe.send(.flagsChanged, command, [])
        _ = try probe.send(.flagsChanged, option, [])
        try require(captured == [combo] && probe.listener.capture == nil, "Modifier capture did not commit once at full release")
        await drainCallbacks()
        try require(probe.calls.isEmpty, "Capture triggered normal dictation callbacks")

        captured = []
        probe.listener.capture = { captured.append($0) }
        _ = try probe.send(.flagsChanged, option, optionFlags)
        try require(captured.isEmpty, "Single modifier capture committed before release")
        _ = try probe.send(.flagsChanged, option, [])
        try require(captured == [.default], "Single modifier capture changed representation")
        captured = []
        probe.listener.capture = { captured.append($0) }
        _ = try probe.send(.flagsChanged, UInt16(kVK_Function), [.maskSecondaryFn])
        _ = try probe.send(.flagsChanged, option, flags(0x40, [.maskAlternate, .maskSecondaryFn]))
        _ = try probe.send(.flagsChanged, UInt16(kVK_Function), optionFlags)
        try require(captured.isEmpty, "Fn combination capture committed before all members released")
        _ = try probe.send(.flagsChanged, option, [])
        try require(captured == [try combination(0x40 | CGEventFlags.maskSecondaryFn.rawValue)],
                    "Fn combination capture omitted a modifier")

        captured = []
        probe.listener.capture = { captured.append($0) }
        _ = try probe.send(.flagsChanged, UInt16(kVK_Option), flags(0x20, [.maskAlternate]))
        _ = try probe.send(.flagsChanged, command, flags(0x30, [.maskAlternate, .maskCommand]))
        _ = try probe.send(.flagsChanged, command, flags(0x20, [.maskAlternate]))
        _ = try probe.send(.flagsChanged, UInt16(kVK_Shift), flags(0x22, [.maskAlternate, .maskShift]))
        _ = try probe.send(.flagsChanged, UInt16(kVK_Shift), flags(0x20, [.maskAlternate]))
        _ = try probe.send(.flagsChanged, UInt16(kVK_Option), [])
        try require(captured == [try combination(0x30)], "Capture lost sides or combined non-simultaneous modifiers")

        captured = []
        probe.listener.capture = { captured.append($0) }
        _ = try probe.send(.flagsChanged, option, optionFlags)
        try require(!probe.send(.keyDown, UInt16(kVK_Escape), optionFlags), "Capture Escape was not swallowed")
        _ = try probe.send(.flagsChanged, option, [])
        try require(captured == [.default] && probe.listener.capture == nil, "Escape did not preserve the old hotkey")
        captured = []
        probe.listener.capture = { captured.append($0) }
        let regular = Hotkey(keyCode: UInt16(kVK_F5), modifierMask: 0)
        try require(!probe.send(.keyDown, regular.keyCode), "Captured regular key was not swallowed")
        try require(captured == [regular], "Regular-key capture was changed")
        print("Passed modifier capture, side preservation and Escape")
    }

    private static func reset() async throws {
        let combo = try combination(comboMask)
        let probe = Probe(hotkey: combo)
        _ = try probe.send(.flagsChanged, option, optionFlags)
        _ = try probe.send(.flagsChanged, command, comboFlags)
        await drainCallbacks()
        probe.listener.reset()
        _ = try probe.send(.flagsChanged, option, commandFlags)
        _ = try probe.send(.flagsChanged, command, [])
        await drainCallbacks()
        try require(probe.calls == ["press"], "Reset retained a stale logical press")
        var captured: [Hotkey] = []
        probe.listener.capture = { captured.append($0) }
        _ = try probe.send(.flagsChanged, option, optionFlags)
        probe.listener.reset()
        _ = try probe.send(.flagsChanged, command, commandFlags)
        _ = try probe.send(.flagsChanged, command, [])
        try require(captured == [try combination(0x10)], "Reset retained modifiers from an unfinished capture")
        captured = []
        probe.listener.capture = { captured.append($0) }
        _ = try probe.send(.flagsChanged, option, optionFlags)
        probe.listener.capture = nil
        probe.listener.capture = { captured.append($0) }
        _ = try probe.send(.flagsChanged, command, commandFlags)
        _ = try probe.send(.flagsChanged, command, [])
        try require(captured == [try combination(0x10)], "New capture retained cancelled modifiers")
        print("Passed listener and capture reset")
    }

    private static func combination(_ mask: UInt64) throws -> Hotkey {
        guard let key = Hotkey.modifierCombination(mask: mask) else { throw Failure("Invalid test modifier mask \(mask)") }
        return key
    }

    private static func flags(_ sided: UInt64, _ families: CGEventFlags) -> CGEventFlags {
        CGEventFlags(rawValue: sided | families.rawValue)
    }

    private static func require(_ condition: Bool, _ message: String) throws {
        if !condition { throw Failure(message) }
    }

    /// The listener schedules callbacks after returning the event. A main-queue
    /// barrier observes those callbacks without sleeping or running an app UI.
    private static func drainCallbacks() async {
        await withCheckedContinuation { continuation in
            DispatchQueue.main.async { continuation.resume() }
        }
    }

    private struct Failure: LocalizedError {
        let message: String
        init(_ message: String) { self.message = message }
        var errorDescription: String? { message }
    }

    @MainActor
    private final class Probe {
        let listener = KeyListener()
        var calls: [String] = []

        init(hotkey: Hotkey) {
            listener.hotkey = hotkey
            listener.onPress = { [weak self] in self?.calls.append("press") }
            listener.onRelease = { [weak self] in self?.calls.append("release") }
            listener.onChord = { [weak self] in self?.calls.append("chord") }
            listener.onPasteLast = { [weak self] in self?.calls.append("paste") }
        }

        /// Returns true when the synthetic event is passed through.
        @discardableResult
        func send(_ type: CGEventType, _ keyCode: UInt16, _ flags: CGEventFlags = [],
                  repeatKey: Bool = false) throws -> Bool {
            guard let event = CGEvent(keyboardEventSource: nil, virtualKey: CGKeyCode(keyCode), keyDown: type != .keyUp) else {
                throw Failure("Could not create a synthetic keyboard event")
            }
            event.type = type
            event.flags = flags
            event.setIntegerValueField(.keyboardEventAutorepeat, value: repeatKey ? 1 : 0)
            return listener.handleForTesting(type, event) != nil
        }
    }
}
#endif
