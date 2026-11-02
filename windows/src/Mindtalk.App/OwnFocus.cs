using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Mindtalk.Core.Dictation;

namespace Mindtalk.App;

/// <summary>
/// "Is there somewhere for the text to go?" — asked of Mindtalk itself when it's the app in
/// front (the "Try it" box, the vocabulary fields): XAML knows what has focus, and asking
/// through UI Automation would find the HUD's own buttons. Every other app is asked through
/// UI Automation. (The Mac asks AppKit for its own windows in the same way.)
/// </summary>
public sealed class OwnFocus(IFocusInspector others, Func<Microsoft.UI.Xaml.XamlRoot?> mainWindow) : IFocusInspector
{
    public Task<FieldKind> FocusedFieldAsync(nint target)
    {
        if (!IsOurs(target)) return others.FocusedFieldAsync(target);
        return Task.FromResult(Focused() is TextBox or RichEditBox or PasswordBox ? FieldKind.Text : FieldKind.None);
    }

    public Task<bool> IsSecureFieldFocusedAsync() =>
        IsOurs(GetForegroundWindow()) ? Task.FromResult(Focused() is PasswordBox) : others.IsSecureFieldFocusedAsync();

    private object? Focused() => mainWindow() is { } root ? FocusManager.GetFocusedElement(root) : null;

    private static bool IsOurs(nint window)
    {
        if (window == 0) return false;
        GetWindowThreadProcessId(window, out var pid);
        return pid == Environment.ProcessId;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
