using System.Runtime.InteropServices;

namespace WhispUI.Interop;

// ── UI Automation — focused-element text-editability probe ──────────────────
//
// Used by WhispEngine.PasteFromClipboard to decide whether the window that
// currently owns the keyboard focus at Stop time is safe to paste into.
// "Safe" means: UIA can answer, and the focused element is a text-accepting
// control (Edit or Document). "Can't tell" (UIA refuses, COM exception,
// protected process) is treated as not safe — clipboard-only path.
//
// COM interop via classic ComImport (no AOT in this project). Only the vtable
// slots actually needed are declared; earlier slots are placeholders with
// opaque IntPtr types to preserve ordering.

internal static class UIAutomation
{
    // https://learn.microsoft.com/windows/win32/winauto/uiauto-automation-element-propids
    private const int UIA_ControlTypePropertyId = 30003;

    // https://learn.microsoft.com/windows/win32/winauto/uiauto-controltype-ids
    private const int UIA_EditControlTypeId     = 50004;
    private const int UIA_DocumentControlTypeId = 50030;

    // CoClass CUIAutomation.
    private static readonly Guid CLSID_CUIAutomation =
        new("FF48DBA4-60EF-4201-AA87-54103EEF594E");

    // Lazy-cached singleton. The UIAutomation client object is thread-safe
    // and meant to be reused across the life of the process.
    private static IUIAutomation? _instance;
    private static readonly object _lock = new();

    // Returns true only when UIA confirms that the system-focused element
    // accepts text input (ControlType is Edit or Document). Any other outcome
    // — element not found, exception, unknown ControlType — returns false and
    // fills `diagnostic` with a short line for the Paste log source.
    public static bool IsFocusedElementTextEditable(out string diagnostic)
    {
        try
        {
            var ua = GetInstance();
            int hr = ua.GetFocusedElement(out var el);
            if (hr != 0 || el is null)
            {
                diagnostic = $"GetFocusedElement hr=0x{hr:X} el={(el is null ? "null" : "ok")}";
                return false;
            }

            hr = el.GetCurrentPropertyValue(UIA_ControlTypePropertyId, out var value);
            if (hr != 0 || value is null)
            {
                diagnostic = $"GetCurrentPropertyValue hr=0x{hr:X} value={(value is null ? "null" : "ok")}";
                return false;
            }

            int controlType = Convert.ToInt32(value);
            bool ok = controlType == UIA_EditControlTypeId
                   || controlType == UIA_DocumentControlTypeId;
            diagnostic = $"ControlType={controlType} (editable={ok})";
            return ok;
        }
        catch (Exception ex)
        {
            diagnostic = $"UIA exception: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static IUIAutomation GetInstance()
    {
        if (_instance is not null) return _instance;
        lock (_lock)
        {
            if (_instance is not null) return _instance;
            Type? t = Type.GetTypeFromCLSID(CLSID_CUIAutomation, throwOnError: true);
            _instance = (IUIAutomation)Activator.CreateInstance(t!)!;
            return _instance;
        }
    }

    // IUIAutomation IID: 30CBE57D-D9D0-452A-AB13-7AC5AC4825EE
    // Declared up to GetFocusedElement (vtable slot 5); earlier slots are
    // opaque placeholders because we never call them.
    [ComImport]
    [Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        [PreserveSig] int CompareElements(IntPtr el1, IntPtr el2, out int areSame);
        [PreserveSig] int CompareRuntimeIds(IntPtr ra1, IntPtr ra2, out int areSame);
        [PreserveSig] int GetRootElement(out IUIAutomationElement? root);
        [PreserveSig] int ElementFromHandle(IntPtr hwnd, out IUIAutomationElement? el);
        [PreserveSig] int ElementFromPoint(POINT pt, out IUIAutomationElement? el);
        [PreserveSig] int GetFocusedElement(out IUIAutomationElement? el);
    }

    // IUIAutomationElement IID: D22108AA-8AC5-49A5-837B-37BBB3D7591E
    // Declared up to GetCurrentPropertyValue (slot 7).
    [ComImport]
    [Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        [PreserveSig] int SetFocus();
        [PreserveSig] int GetRuntimeId(out IntPtr runtimeId);
        [PreserveSig] int FindFirst(int scope, IntPtr condition, out IUIAutomationElement? found);
        [PreserveSig] int FindAll(int scope, IntPtr condition, out IntPtr found);
        [PreserveSig] int FindFirstBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IUIAutomationElement? found);
        [PreserveSig] int FindAllBuildCache(int scope, IntPtr condition, IntPtr cacheRequest, out IntPtr found);
        [PreserveSig] int BuildUpdatedCache(IntPtr cacheRequest, out IUIAutomationElement? updated);
        [PreserveSig] int GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }
}
