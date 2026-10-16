using System.Diagnostics;
using Microsoft.Win32;

namespace Mindtalk.Platform;

/// <summary>
/// Windows' microphone privacy switches. Desktop apps are never asked: they're simply
/// blocked when one of these is off, and the mic then delivers silence or fails to open.
/// </summary>
public static class MicrophoneAccess
{
    private const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// <summary>
    /// Not turned off: "Microphone access" for the PC (HKLM), "Let apps access your
    /// microphone" (HKCU) and "Let desktop apps access your microphone" (HKCU\NonPackaged).
    /// </summary>
    public static bool IsAllowed =>
        !IsDenied(Registry.LocalMachine, Store)
        && !IsDenied(Registry.CurrentUser, Store)
        && !IsDenied(Registry.CurrentUser, Store + @"\NonPackaged");

    /// Opens Settings → Privacy & security → Microphone.
    public static void OpenSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true }); }
        catch (Exception ex) { Trace.WriteLine($"MicrophoneAccess: couldn't open Settings ({ex.GetType().Name})"); }
    }

    private static bool IsDenied(RegistryKey root, string path)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return string.Equals(key?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }   // can't tell: don't claim it's blocked
    }
}
