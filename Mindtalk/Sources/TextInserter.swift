import AppKit
import Carbon.HIToolbox

enum TextInserter {
    /// Debug self-test: log instead of pasting.
    nonisolated(unsafe) static var dryRun = false

    /// Pastes `text` into the focused app via ⌘V, then puts the user's clipboard
    /// back. Marked transient so clipboard managers don't record it.
    @MainActor
    static func insert(_ text: String) {
        if dryRun { print("[klistra in] \(text)"); return }
        // Our own window (the "Prova" box): type straight into the text view — a
        // posted ⌘V would come back to us and depends on the Edit menu.
        if NSApp.isActive, let textView = NSApp.keyWindow?.firstResponder as? NSTextView, textView.isEditable {
            textView.insertText(text, replacementRange: textView.selectedRange())
            return
        }
        let pb = NSPasteboard.general
        let saved: [NSPasteboardItem] = (pb.pasteboardItems ?? []).map { item in
            let copy = NSPasteboardItem()
            for type in item.types {
                if let data = item.data(forType: type) { copy.setData(data, forType: type) }
            }
            return copy
        }
        pb.clearContents()
        pb.setString(text, forType: .string)
        pb.setData(Data(), forType: NSPasteboard.PasteboardType("org.nspasteboard.TransientType"))
        let ourChange = pb.changeCount

        let source = CGEventSource(stateID: .combinedSessionState)
        let vKey = CGKeyCode(kVK_ANSI_V)
        let down = CGEvent(keyboardEventSource: source, virtualKey: vKey, keyDown: true)
        let up = CGEvent(keyboardEventSource: source, virtualKey: vKey, keyDown: false)
        down?.flags = .maskCommand
        up?.flags = .maskCommand
        down?.post(tap: .cghidEventTap)
        up?.post(tap: .cghidEventTap)

        // Give the target app time to read the paste, then restore — unless
        // something else has written to the clipboard in the meantime.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.6) {
            guard pb.changeCount == ourChange else { return }
            pb.clearContents()
            if !saved.isEmpty { pb.writeObjects(saved) }
        }
    }
}
