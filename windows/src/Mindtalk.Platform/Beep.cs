using System.Runtime.InteropServices;

namespace Mindtalk.Platform;

/// <summary>The system's "can't do that" sound (NSSound.beep on the Mac).</summary>
public static partial class Beep
{
    public static void Play() => MessageBeep(0xFFFFFFFF);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MessageBeep(uint type);
}
