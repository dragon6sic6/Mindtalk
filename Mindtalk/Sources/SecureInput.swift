import AppKit
import ApplicationServices
import Carbon

/// Is the cursor in a password field right now? Then a dictation is typed but
/// never kept: not in Senaste, not in the statistics, not polished by AI.
enum SecureInput {
    @MainActor static var isActive: Bool {
        // Ask the focused field. A hung app mustn't hang us (the key listener runs
        // on this thread), so at most 0.2 s.
        let system = AXUIElementCreateSystemWide()
        AXUIElementSetMessagingTimeout(system, 0.2)
        var focused: CFTypeRef?
        if AXUIElementCopyAttributeValue(system, kAXFocusedUIElementAttribute as CFString, &focused) == .success,
           let element = focused, CFGetTypeID(element) == AXUIElementGetTypeID() {
            let field = element as! AXUIElement
            AXUIElementSetMessagingTimeout(field, 0.2)
            var subrole: CFTypeRef?
            AXUIElementCopyAttributeValue(field, kAXSubroleAttribute as CFString, &subrole)
            return (subrole as? String) == (kAXSecureTextFieldSubrole as String)
        }
        // No answer: fall back to macOS's secure-input flag. (Not first — Terminal's
        // "Secure Keyboard Entry" keeps it on everywhere, and every dictation would
        // then go unsaved.)
        return IsSecureEventInputEnabled()
    }
}
