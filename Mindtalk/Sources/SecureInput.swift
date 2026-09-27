import AppKit
import ApplicationServices
import Carbon

/// Is the cursor in a password field right now? Then a dictation is typed but
/// never kept: not in Senaste, not in the statistics, not polished by AI.
enum SecureInput {
    @MainActor static var isActive: Bool {
        if IsSecureEventInputEnabled() { return true }
        let system = AXUIElementCreateSystemWide()
        var focused: CFTypeRef?
        guard AXUIElementCopyAttributeValue(system, kAXFocusedUIElementAttribute as CFString, &focused) == .success,
              let element = focused, CFGetTypeID(element) == AXUIElementGetTypeID() else { return false }
        var subrole: CFTypeRef?
        AXUIElementCopyAttributeValue(element as! AXUIElement, kAXSubroleAttribute as CFString, &subrole)
        return (subrole as? String) == (kAXSecureTextFieldSubrole as String)
    }
}
