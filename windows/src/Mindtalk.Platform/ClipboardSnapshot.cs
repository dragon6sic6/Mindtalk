using System.Runtime.InteropServices;

namespace Mindtalk.Platform;

/// <summary>
/// The Windows clipboard, raw: a copy of everything on it (to put back after pasting), and
/// setting text that clipboard history and cloud sync leave alone.
/// </summary>
public sealed class ClipboardSnapshot
{
    // Formats that hold a GDI handle, not memory — can't be copied byte for byte. (A
    // bitmap survives anyway: Windows also offers it as CF_DIB, which is copied.)
    private static readonly HashSet<uint> HandleFormats = [2 /* BITMAP */, 3 /* METAFILEPICT */, 9 /* PALETTE */,
        14 /* ENHMETAFILE */, 0x80 /* OWNERDISPLAY */, 0x82 /* DSPBITMAP */, 0x83 /* DSPMETAFILEPICT */, 0x8E /* DSPENHMETAFILE */];

    /// Marks set by password managers ("don't keep this") — and by us.
    private static readonly uint ExcludeFromMonitoring = Native.RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint CanIncludeInHistory = Native.RegisterClipboardFormatW("CanIncludeInClipboardHistory");
    private static readonly uint CanUploadToCloud = Native.RegisterClipboardFormatW("CanUploadToCloudClipboard");
    private static readonly uint ClipboardViewerIgnore = Native.RegisterClipboardFormatW("Clipboard Viewer Ignore");

    private readonly List<(uint Format, byte[] Data)> _items;

    private ClipboardSnapshot(List<(uint, byte[])> items) => _items = items;

    private static readonly Lazy<nint> Owner = new(() =>
        Native.CreateWindowExW(0, "STATIC", "Mindtalk clipboard", 0, 0, 0, 0, 0, Native.HWND_MESSAGE, 0, 0, 0));

    /// <summary>A copy of the clipboard to put back — null (put nothing back) when it holds a secret.</summary>
    public static ClipboardSnapshot? Take()
    {
        using var open = Open();
        if (open is null) return null;
        var items = new List<(uint, byte[])>();
        uint format = 0;
        while ((format = Native.EnumClipboardFormats(format)) != 0)
        {
            // A password from a password manager had its moment; it isn't put back.
            if (format == ExcludeFromMonitoring || format == ClipboardViewerIgnore) return null;
            if (HandleFormats.Contains(format)) continue;
            var handle = Native.GetClipboardData(format);
            if (handle == 0) continue;
            var size = (int)Native.GlobalSize(handle);
            if (size == 0) continue;
            var pointer = Native.GlobalLock(handle);
            if (pointer == 0) continue;
            try
            {
                var data = new byte[size];
                Marshal.Copy(pointer, data, 0, size);
                items.Add((format, data));
            }
            finally { Native.GlobalUnlock(handle); }
        }
        return new ClipboardSnapshot(items);
    }

    /// <summary>Puts the copied clipboard back, kept out of clipboard history (it's already there).</summary>
    public void Restore()
    {
        using var open = Open();
        if (open is null) return;
        Native.EmptyClipboard();
        foreach (var (format, data) in _items) Set(format, data);
        KeepOutOfHistory();
    }

    /// <summary>Sets the clipboard to <paramref name="text"/>.</summary>
    /// <param name="private">Keep it out of clipboard history and cloud sync, and tell
    /// clipboard managers to ignore it (it's only there to be pasted).</param>
    /// <returns>The clipboard's sequence number after writing, or 0 if it couldn't be opened.</returns>
    public static uint SetText(string text, bool @private)
    {
        using (var open = Open())
        {
            if (open is null) return 0;
            Native.EmptyClipboard();
            var bytes = new byte[(text.Length + 1) * 2];
            System.Text.Encoding.Unicode.GetBytes(text, 0, text.Length, bytes, 0);
            Set(Native.CF_UNICODETEXT, bytes);
            if (@private)
            {
                Set(ExcludeFromMonitoring, [0]);
                KeepOutOfHistory();
            }
        }
        return Native.GetClipboardSequenceNumber();
    }

    private static void KeepOutOfHistory()
    {
        Set(CanIncludeInHistory, BitConverter.GetBytes(0));
        Set(CanUploadToCloud, BitConverter.GetBytes(0));
    }

    private static void Set(uint format, byte[] data)
    {
        var handle = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (nuint)data.Length);
        if (handle == 0) return;
        var pointer = Native.GlobalLock(handle);
        Marshal.Copy(data, 0, pointer, data.Length);
        Native.GlobalUnlock(handle);
        // On success the clipboard owns the memory.
        if (Native.SetClipboardData(format, handle) == 0) Native.GlobalFree(handle);
    }

    /// Another app may have the clipboard open for a moment; try for up to ~0.5 s.
    private static Opened? Open()
    {
        for (var attempt = 0; attempt < 25; attempt++)
        {
            if (Native.OpenClipboard(Owner.Value)) return new Opened();
            Thread.Sleep(20);
        }
        return null;
    }

    private sealed class Opened : IDisposable
    {
        public void Dispose() => Native.CloseClipboard();
    }
}
