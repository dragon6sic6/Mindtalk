using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Mindtalk.Core.Dictation;

namespace Mindtalk.Platform;

/// <summary>
/// Is there somewhere for the text to go? Asked over UI Automation of the app you're in
/// when you stop talking. Only a clear "no" counts — a button, a link, a web page with no
/// field in focus, Explorer's desktop or file list. Apps that don't say and anything that
/// might take text (a spreadsheet's cell) get the paste as usual.
/// </summary>
/// <remarks>
/// The questions go to one background MTA thread (UI Automation must not be asked from a
/// UI thread — the app in front may be Mindtalk itself). A hung app mustn't hang a
/// dictation: each answer is given up after ~200 ms. The field's text is never read.
/// </remarks>
public sealed class FocusInspector : IFocusInspector, IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(200);

    // UIA control type IDs.
    private const int Button = 50000, CheckBox = 50002, ComboBox = 50003, Edit = 50004, Hyperlink = 50005,
        Image = 50006, MenuItem = 50011, RadioButton = 50013, Slider = 50015, TabItem = 50019, Text = 50020,
        Document = 50030, SplitButton = 50031;
    // UIA property IDs.
    private const int ProcessIdProperty = 30002, ControlTypeProperty = 30003, IsPasswordProperty = 30019,
        FrameworkIdProperty = 30024, IsTextPatternAvailableProperty = 30040, IsValuePatternAvailableProperty = 30043,
        ValueIsReadOnlyProperty = 30046;
    private const int TreeScopeChildren = 2;

    /// Things you click, not type into.
    private static readonly HashSet<int> ControlTypes =
        [Button, SplitButton, CheckBox, RadioButton, Hyperlink, Image, Slider, MenuItem, TabItem];

    private readonly BlockingCollection<Action<IUIAutomation2>> _work = new();

    public FocusInspector()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "Mindtalk UI Automation" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public Task<FieldKind> FocusedFieldAsync(nint target) => Ask(uia => Decide(uia, target), FieldKind.Unknown);

    /// The focused element says it's a password field.
    public Task<bool> IsSecureFieldFocusedAsync() => Ask(uia =>
    {
        var element = uia.GetFocusedElement();
        if (element is null) return false;
        try { return Get(element, IsPasswordProperty) is true; }
        finally { Release(element); }
    }, false);

    private async Task<T> Ask<T>(Func<IUIAutomation2, T> query, T fallback)
    {
        var answer = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = Stopwatch.GetTimestamp() + (long)(Limit.TotalSeconds * Stopwatch.Frequency);
        try
        {
            _work.Add(uia =>
            {
                // Still queued behind a slow answer when the time was up: don't bother.
                if (Stopwatch.GetTimestamp() > deadline) { answer.TrySetResult(fallback); return; }
                try { answer.TrySetResult(query(uia)); }
                catch (Exception ex)
                {
                    Trace.WriteLine($"FocusInspector: no answer ({ex.GetType().Name} 0x{ex.HResult:X8})");
                    answer.TrySetResult(fallback);
                }
            });
        }
        catch (InvalidOperationException) { return fallback; }   // disposed

        var first = await Task.WhenAny(answer.Task, Task.Delay(Limit)).ConfigureAwait(false);
        if (first == answer.Task) return await answer.Task.ConfigureAwait(false);
        Trace.WriteLine("FocusInspector: no answer in time");
        return fallback;
    }

    private void Run()
    {
        IUIAutomation2? uia = null;
        try
        {
            uia = (IUIAutomation2)Activator.CreateInstance(Type.GetTypeFromCLSID(CUIAutomation8, throwOnError: true)!)!;
            // UI Automation's own limits (default: seconds), so a hung app frees this thread soon.
            uia.SetConnectionTimeout((uint)Limit.TotalMilliseconds);
            uia.SetTransactionTimeout((uint)Limit.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"FocusInspector: UI Automation unavailable ({ex.GetType().Name})");
        }
        foreach (var work in _work.GetConsumingEnumerable())
        {
            if (uia is null) work(null!);   // answers with the fallback (the call throws)
            else work(uia);
        }
    }

    private static FieldKind Decide(IUIAutomation2 uia, nint target)
    {
        if (target == 0) target = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(target, out var pid);
        var app = ProcessName(pid);
        var windowClass = ClassName(target);
        // Explorer: the desktop (Progman/WorkerW) and File Explorer's file list.
        var isExplorer = app.Equals("explorer", StringComparison.OrdinalIgnoreCase) || windowClass is "Progman" or "WorkerW";

        // A store app's window belongs to its frame host; the focus is in the app's own process.
        if (app.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)) pid = 0;

        var (kind, role) = Decide(uia, pid, isExplorer);
        // Never the field's text — only what kind of thing it is.
        Trace.WriteLine($"FocusInspector: {app}: {role} → {kind}");
        return kind;
    }

    private static (FieldKind, string) Decide(IUIAutomation2 uia, uint pid, bool isExplorer)
    {
        IUIAutomationElement? element;
        try { element = uia.GetFocusedElement(); }
        catch (COMException) { element = null; }
        if (element is null) return (isExplorer ? FieldKind.None : FieldKind.Unknown, "no focus");
        try
        {
            if (pid != 0 && Get(element, ProcessIdProperty) is int owner && owner != pid)
                return (FieldKind.Unknown, "focus in another app");

            var type = Get(element, ControlTypeProperty) is int t ? t : 0;
            var role = ControlTypeName(type);
            if (ControlTypes.Contains(type)) return (FieldKind.None, role);
            if (type == Edit) return (FieldKind.Text, role);

            var editable = Get(element, IsValuePatternAvailableProperty) is true && Get(element, ValueIsReadOnlyProperty) is false;
            // On a web page (a browser, an Electron or WebView2 app) everything has text to
            // select, so only "editable" counts: a field, or an editor like Gmail's.
            if (Get(element, FrameworkIdProperty) is "Chrome" or "Gecko")
            {
                if (editable) return (FieldKind.Text, role + " editable");
                // A page whose content isn't there yet — its accessibility tree not built —
                // can't tell a field from the page.
                if (type == Document && !HasChildren(uia, element)) return (FieldKind.Unknown, role + " empty");
                return (FieldKind.None, role + " on page");
            }
            // A field that doesn't call itself one (a document, an editable combo box):
            // editable, or with a text cursor.
            if (editable) return (FieldKind.Text, role + " editable");
            if (Get(element, IsTextPatternAvailableProperty) is true && type != Text) return (FieldKind.Text, role + " text");
            if (isExplorer) return (FieldKind.None, role);
            return (FieldKind.Unknown, role);
        }
        finally { Release(element); }
    }

    private static bool HasChildren(IUIAutomation2 uia, IUIAutomationElement element)
    {
        var any = uia.CreateTrueCondition();
        try
        {
            var child = element.FindFirst(TreeScopeChildren, any);
            Release(child);
            return child is not null;
        }
        finally { Release(any); }
    }

    /// The property's value; null when the element doesn't support it (UI Automation then
    /// hands back a "not supported" COM object).
    private static object? Get(IUIAutomationElement element, int property)
    {
        var value = element.GetCurrentPropertyValue(property);
        if (value is null || !Marshal.IsComObject(value)) return value;
        Marshal.ReleaseComObject(value);
        return null;
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }

    private static string ProcessName(uint pid)
    {
        try { using var process = Process.GetProcessById((int)pid); return process.ProcessName; }
        catch { return "?"; }
    }

    private static string ClassName(nint window)
    {
        var buffer = new char[256];
        var length = window == 0 ? 0 : Native.GetClassNameW(window, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(length, 0));
    }

    private static string ControlTypeName(int type) => type switch
    {
        Button => "Button", CheckBox => "CheckBox", ComboBox => "ComboBox", Edit => "Edit", Hyperlink => "Hyperlink",
        Image => "Image", MenuItem => "MenuItem", RadioButton => "RadioButton", Slider => "Slider", TabItem => "TabItem",
        Text => "Text", Document => "Document", SplitButton => "SplitButton",
        50007 => "ListItem", 50008 => "List", 50009 => "Menu", 50018 => "Tab", 50023 => "Tree", 50024 => "TreeItem",
        50025 => "Custom", 50026 => "Group", 50028 => "DataGrid", 50029 => "DataItem", 50032 => "Window", 50033 => "Pane",
        50036 => "Table",
        0 => "?",
        _ => $"control {type}",
    };

    public void Dispose() => _work.CompleteAdding();

    // ---- UIAutomationClient.h: only what's used; the other slots hold their place -------

    private static readonly Guid CUIAutomation8 = new("e22ad333-b25f-460c-83d0-0581107395c9");

    [ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationCondition { }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void _SetFocus();
        void _GetRuntimeId();
        IUIAutomationElement? FindFirst(int scope, IUIAutomationCondition condition);
        void _FindAll();
        void _FindFirstBuildCache();
        void _FindAllBuildCache();
        void _BuildUpdatedCache();
        [return: MarshalAs(UnmanagedType.Struct)] object? GetCurrentPropertyValue(int propertyId);
    }

    /// IUIAutomation (slots 3–57) followed by IUIAutomation2's timeouts (58–63).
    [ComImport, Guid("34723aff-0c9d-49d0-9896-7ab52df8cd8a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation2
    {
        void _CompareElements();
        void _CompareRuntimeIds();
        void _GetRootElement();
        void _ElementFromHandle();
        void _ElementFromPoint();
        IUIAutomationElement? GetFocusedElement();
        void _GetRootElementBuildCache();
        void _ElementFromHandleBuildCache();
        void _ElementFromPointBuildCache();
        void _GetFocusedElementBuildCache();
        void _CreateTreeWalker();
        void _ControlViewWalker();
        void _ContentViewWalker();
        void _RawViewWalker();
        void _RawViewCondition();
        void _ControlViewCondition();
        void _ContentViewCondition();
        void _CreateCacheRequest();
        IUIAutomationCondition CreateTrueCondition();
        void _CreateFalseCondition();
        void _CreatePropertyCondition();
        void _CreatePropertyConditionEx();
        void _CreateAndCondition();
        void _CreateAndConditionFromArray();
        void _CreateAndConditionFromNativeArray();
        void _CreateOrCondition();
        void _CreateOrConditionFromArray();
        void _CreateOrConditionFromNativeArray();
        void _CreateNotCondition();
        void _AddAutomationEventHandler();
        void _RemoveAutomationEventHandler();
        void _AddPropertyChangedEventHandlerNativeArray();
        void _AddPropertyChangedEventHandler();
        void _RemovePropertyChangedEventHandler();
        void _AddStructureChangedEventHandler();
        void _RemoveStructureChangedEventHandler();
        void _AddFocusChangedEventHandler();
        void _RemoveFocusChangedEventHandler();
        void _RemoveAllEventHandlers();
        void _IntNativeArrayToSafeArray();
        void _IntSafeArrayToNativeArray();
        void _RectToVariant();
        void _VariantToRect();
        void _SafeArrayToRectNativeArray();
        void _CreateProxyFactoryEntry();
        void _ProxyFactoryMapping();
        void _GetPropertyProgrammaticName();
        void _GetPatternProgrammaticName();
        void _PollForPotentialSupportedPatterns();
        void _PollForPotentialSupportedProperties();
        void _CheckNotSupported();
        void _ReservedNotSupportedValue();
        void _ReservedMixedAttributeValue();
        void _ElementFromIAccessible();
        void _ElementFromIAccessibleBuildCache();
        void _GetAutoSetFocus();
        void _SetAutoSetFocus();
        void _GetConnectionTimeout();
        void SetConnectionTimeout(uint milliseconds);
        void _GetTransactionTimeout();
        void SetTransactionTimeout(uint milliseconds);
    }
}
