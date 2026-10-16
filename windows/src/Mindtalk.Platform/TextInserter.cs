using System.Runtime.InteropServices;
using Mindtalk.Core.Dictation;
using Mindtalk.Core.Input;

namespace Mindtalk.Platform;

/// <summary>
/// Pastes text where the cursor is with Ctrl+V, then puts the user's clipboard back —
/// or, when asked, leaves the text there instead. Call on the UI thread.
/// </summary>
public sealed class TextInserter : ITextOutput
{
    /// Time for the target app to read the paste before the clipboard is put back.
    private static readonly TimeSpan RestoreDelay = TimeSpan.FromSeconds(1);

    private CancellationTokenSource? _pendingRestore;
    private ClipboardSnapshot? _pendingSnapshot;

    public nint CaptureTarget() => Native.GetForegroundWindow();

    public async Task<InsertOutcome> InsertAsync(string text, nint target, bool keepInClipboard)
    {
        // Windows text on the clipboard uses CRLF; many controls drop a bare LF on paste.
        text = text.ReplaceLineEndings("\r\n");
        // Ctrl+V with the dictation key (Alt, Win…) still held would be another shortcut.
        for (var i = 0; i < 50 && AnyModifierDown(); i++) await Task.Delay(20);

        // You switched window while it was being written: don't type into the wrong one.
        if (target != 0 && Native.GetForegroundWindow() != target)
        {
            CancelPendingRestore();
            ClipboardSnapshot.SetText(text.TrimEnd(' '), @private: false);
            return InsertOutcome.TargetChanged;
        }

        // Two pastes close together: keep the clipboard from before the first one, not our own text.
        var saved = _pendingSnapshot ?? (keepInClipboard ? null : ClipboardSnapshot.Take());
        CancelPendingRestore();

        var ours = ClipboardSnapshot.SetText(text, @private: !keepInClipboard);
        SendCtrlV();

        if (keepInClipboard || saved is null) return InsertOutcome.Inserted;
        var cts = new CancellationTokenSource();
        _pendingRestore = cts;
        _pendingSnapshot = saved;
        try { await Task.Delay(RestoreDelay, cts.Token); }
        catch (TaskCanceledException) { return InsertOutcome.Inserted; }
        _pendingRestore = null;
        _pendingSnapshot = null;
        // Unless something else has written to the clipboard meanwhile.
        if (Native.GetClipboardSequenceNumber() == ours) saved.Restore();
        return InsertOutcome.Inserted;
    }

    private void CancelPendingRestore()
    {
        _pendingRestore?.Cancel();
        _pendingRestore = null;
        _pendingSnapshot = null;
    }

    private static bool AnyModifierDown() =>
        new[] { VirtualKey.LeftAlt, VirtualKey.RightAlt, VirtualKey.LeftWin, VirtualKey.RightWin,
                VirtualKey.LeftShift, VirtualKey.RightShift, VirtualKey.LeftCtrl, VirtualKey.RightCtrl }
            .Any(KeyboardHook.IsPhysicallyDown);

    private static void SendCtrlV()
    {
        Native.INPUT[] inputs =
        [
            Native.Key(VirtualKey.Control, up: false), Native.Key('V', up: false),
            Native.Key('V', up: true), Native.Key(VirtualKey.Control, up: true),
        ];
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
    }
}
