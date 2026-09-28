import AppKit
import ApplicationServices
import os

/// Is there somewhere for the text to go? Asked of the app you're in when you
/// stop talking. Only a clear "no" counts — a button, a link, a web page with
/// no field in focus, Finder's desktop or file list. Apps that don't say (many
/// built on Electron) and anything that might take text (a spreadsheet's cell)
/// get the paste as usual.
enum FocusedField {
    enum Kind: String { case text, none, unknown }

    private static let textRoles: Set<String> = [kAXTextFieldRole, kAXTextAreaRole, kAXComboBoxRole, "AXSearchField"]
    /// Things you click, not type into.
    private static let controlRoles: Set<String> = [kAXButtonRole, kAXCheckBoxRole, kAXRadioButtonRole, kAXPopUpButtonRole,
                                                    kAXMenuButtonRole, kAXSliderRole, kAXImageRole, "AXLink"]
    private static let finder = "com.apple.finder"

    private static let log = Logger(subsystem: "ai.mindact.mindtalk", category: "FocusedField")

    /// Logs the answer and the field's role (never its text) — to see why in Console.
    @MainActor static func kind(in pid: pid_t) -> Kind {
        let (kind, role) = decide(pid)
        let app = NSRunningApplication(processIdentifier: pid)?.bundleIdentifier ?? "?"
        log.info("\(app, privacy: .public): \(role, privacy: .public) → \(kind.rawValue, privacy: .public)")
        return kind
    }

    @MainActor private static func decide(_ pid: pid_t) -> (Kind, String) {
        // Our own window: ask AppKit — an accessibility call to ourselves would
        // wait on the thread that's asking.
        if pid == ProcessInfo.processInfo.processIdentifier {
            return ((NSApp.keyWindow?.firstResponder as? NSTextView)?.isEditable == true ? .text : .none, "Mindtalk")
        }
        let isFinder = NSRunningApplication(processIdentifier: pid)?.bundleIdentifier == finder
        let app = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(app, 0.2)
        var focused: CFTypeRef?
        switch AXUIElementCopyAttributeValue(app, kAXFocusedUIElementAttribute as CFString, &focused) {
        case .success: break
        case .noValue: return (isFinder ? .none : .unknown, "no focus")
        case let error: return (.unknown, "error \(error.rawValue)")
        }
        guard let focused, CFGetTypeID(focused) == AXUIElementGetTypeID() else { return (.unknown, "?") }
        let element = focused as! AXUIElement
        AXUIElementSetMessagingTimeout(element, 0.2)

        let role = string(element, kAXRoleAttribute) ?? ""
        if controlRoles.contains(role) { return (.none, role) }
        // The page itself in focus: text only if it's editable (a page that is all
        // editor). A page always reports a selection, so that says nothing here.
        // And only if its content is really there: a browser or Electron app that
        // hasn't built its accessibility tree shows an empty page even with the
        // cursor in a field.
        if role == "AXWebArea" {
            if isEditable(element) { return (.text, role + " editable") }
            return hasChildren(element) ? (.none, role) : (.unknown, role + " empty")
        }
        if textRoles.contains(role) || takesText(element) { return (.text, role) }
        if isFinder { return (.none, role) }
        return (.unknown, role)
    }

    /// Editable, or has a text cursor: a field that doesn't call itself one,
    /// or text you edit on a web page.
    private static func takesText(_ element: AXUIElement) -> Bool {
        var settable = DarwinBoolean(false)
        if AXUIElementIsAttributeSettable(element, kAXValueAttribute as CFString, &settable) == .success, settable.boolValue {
            return true
        }
        if isEditable(element) { return true }
        var value: CFTypeRef?
        return AXUIElementCopyAttributeValue(element, kAXSelectedTextRangeAttribute as CFString, &value) == .success
            && string(element, kAXRoleAttribute) != kAXStaticTextRole
    }

    /// Inside something editable on a web page (browsers and Electron say so).
    private static func isEditable(_ element: AXUIElement) -> Bool {
        var value: CFTypeRef?
        return AXUIElementCopyAttributeValue(element, "AXEditableAncestor" as CFString, &value) == .success && value != nil
    }

    private static func hasChildren(_ element: AXUIElement) -> Bool {
        var count: CFIndex = 0
        return AXUIElementGetAttributeValueCount(element, kAXChildrenAttribute as CFString, &count) == .success && count > 0
    }

    private static func string(_ element: AXUIElement, _ attribute: String) -> String? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, attribute as CFString, &value) == .success else { return nil }
        return value as? String
    }
}
