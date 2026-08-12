using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Yagu.Helpers;

/// <summary>
/// Hosts the traditional Explorer context menu for one file-system item. Raw COM vtable calls through
/// unmanaged function pointers keep this compatible with Yagu's Native AOT build, where built-in COM
/// interop is disabled. Building the menu loads the registered shell extensions for the item into this
/// process, exactly as Explorer does — see docs/security.md.
/// </summary>
[ExcludeFromCodeCoverage]
internal static unsafe class ShellContextMenu
{
    public static void ShowAtCursor(IntPtr owner, string path)
    {
        if (!TryGetCursorPosition(out int x, out int y))
            throw new InvalidOperationException("Could not determine the pointer position for the shell context menu.");

        ShowAt(owner, path, x, y);
    }

    /// <summary>Opens the menu with its top-left corner at the given physical screen point.</summary>
    public static void ShowAt(IntPtr owner, string path, int screenX, int screenY)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (owner == IntPtr.Zero)
            throw new ArgumentException("A shell context menu requires an owner window.", nameof(owner));

        Show(owner, path, screenX, screenY);
    }

    /// <summary>The current pointer position in physical screen pixels.</summary>
    public static bool TryGetCursorPosition(out int screenX, out int screenY)
    {
        bool ok = GetCursorPos(out POINT cursor);
        screenX = cursor.X;
        screenY = cursor.Y;
        return ok;
    }

    private static void Show(IntPtr owner, string path, int screenX, int screenY)
    {
        IntPtr itemPidl = IntPtr.Zero;
        IntPtr parentFolder = IntPtr.Zero;
        IntPtr childPidlArray = IntPtr.Zero;
        IntPtr contextMenu = IntPtr.Zero;
        IntPtr contextMenu2 = IntPtr.Zero;
        IntPtr contextMenu3 = IntPtr.Zero;
        IntPtr menu = IntPtr.Zero;
        SubclassProc? menuWindowProc = null;
        bool subclassInstalled = false;

        try
        {
            ThrowIfFailed(SHParseDisplayName(path, IntPtr.Zero, out itemPidl, 0, out _));
            if (itemPidl == IntPtr.Zero)
                throw new InvalidOperationException($"Explorer could not resolve '{path}'.");

            ThrowIfFailed(SHBindToParent(itemPidl, in IID_IShellFolder, out parentFolder, out IntPtr childPidl));
            if (parentFolder == IntPtr.Zero || childPidl == IntPtr.Zero)
                throw new InvalidOperationException($"Explorer could not resolve the parent of '{path}'.");

            childPidlArray = Marshal.AllocCoTaskMem(IntPtr.Size);
            Marshal.WriteIntPtr(childPidlArray, childPidl);
            Guid contextMenuId = IID_IContextMenu;
            var getUiObjectOf = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, Guid*, IntPtr, IntPtr*, int>)
                VtableMethod(parentFolder, VtableSlot_GetUiObjectOf);
            IntPtr contextMenuResult;
            ThrowIfFailed(getUiObjectOf(
                parentFolder,
                owner,
                1,
                childPidlArray,
                &contextMenuId,
                IntPtr.Zero,
                &contextMenuResult));
            contextMenu = contextMenuResult;
            if (contextMenu == IntPtr.Zero)
                throw new InvalidOperationException($"Explorer did not provide a context menu for '{path}'.");

            contextMenu3 = TryQueryInterface(contextMenu, IID_IContextMenu3);
            contextMenu2 = TryQueryInterface(contextMenu, IID_IContextMenu2);

            menu = CreatePopupMenu();
            if (menu == IntPtr.Zero)
                throw new InvalidOperationException("Could not create the Explorer context menu window.");

            // Explorer only reveals extended verbs while Shift is held; mirror that instead of always showing them.
            // The menu opens on a queued continuation, so read the live key state rather than the message-time one.
            uint queryFlags = CmfNormal;
            if ((GetAsyncKeyState(VkShift) & 0x8000) != 0)
                queryFlags |= CmfExtendedVerbs;

            var queryContextMenu = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, int>)
                VtableMethod(contextMenu, VtableSlot_QueryContextMenu);
            ThrowIfFailed(queryContextMenu(contextMenu, menu, 0, CommandIdFirst, CommandIdLast, queryFlags));

            if (contextMenu2 != IntPtr.Zero || contextMenu3 != IntPtr.Zero)
            {
                menuWindowProc = (hWnd, message, wParam, lParam, _, _) =>
                {
                    if (TryHandleShellMenuMessage(contextMenu2, contextMenu3, message, wParam, lParam, out IntPtr result))
                        return result;

                    return DefSubclassProc(hWnd, message, wParam, lParam);
                };
                subclassInstalled = SetWindowSubclass(owner, menuWindowProc, ShellMenuSubclassId, UIntPtr.Zero);
                if (!subclassInstalled)
                    throw new InvalidOperationException("Could not attach the Explorer context menu to the Yagu window.");
            }

            SetForegroundWindow(owner);
            uint selectedCommand = TrackPopupMenuEx(
                menu,
                TpmReturnCommand | TpmRightButton | TpmLeftAlign | TpmTopAlign,
                screenX,
                screenY,
                owner,
                IntPtr.Zero);
            if (selectedCommand == 0)
                return;

            if (subclassInstalled && menuWindowProc is not null)
            {
                RemoveWindowSubclass(owner, menuWindowProc, ShellMenuSubclassId);
                subclassInstalled = false;
            }

            uint commandOffset = selectedCommand - CommandIdFirst;
            var invokeInfo = new CMINVOKECOMMANDINFOEX
            {
                cbSize = sizeof(CMINVOKECOMMANDINFOEX),
                fMask = CmicMaskUnicode | CmicMaskPointInvoke,
                hwnd = owner,
                lpVerb = (IntPtr)commandOffset,
                lpVerbW = (IntPtr)commandOffset,
                nShow = SwShowNormal,
                ptInvoke = new POINT { X = screenX, Y = screenY },
            };
            var invokeCommand = (delegate* unmanaged[Stdcall]<IntPtr, CMINVOKECOMMANDINFOEX*, int>)
                VtableMethod(contextMenu, VtableSlot_InvokeCommand);
            ThrowIfFailed(invokeCommand(contextMenu, &invokeInfo));
        }
        finally
        {
            if (subclassInstalled && menuWindowProc is not null)
                RemoveWindowSubclass(owner, menuWindowProc, ShellMenuSubclassId);
            GC.KeepAlive(menuWindowProc);
            if (menu != IntPtr.Zero)
                DestroyMenu(menu);
            Release(contextMenu3);
            Release(contextMenu2);
            Release(contextMenu);
            Release(parentFolder);
            if (childPidlArray != IntPtr.Zero)
                Marshal.FreeCoTaskMem(childPidlArray);
            if (itemPidl != IntPtr.Zero)
                CoTaskMemFree(itemPidl);
        }
    }

    private static bool TryHandleShellMenuMessage(
        IntPtr contextMenu2,
        IntPtr contextMenu3,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        out IntPtr result)
    {
        result = IntPtr.Zero;
        if (message is not (WmInitMenuPopup or WmDrawItem or WmMeasureItem or WmMenuChar))
            return false;

        if (contextMenu3 != IntPtr.Zero)
        {
            var handleMenuMessage2 = (delegate* unmanaged[Stdcall]<IntPtr, uint, UIntPtr, IntPtr, IntPtr*, int>)
                VtableMethod(contextMenu3, VtableSlot_HandleMenuMessage2);
            IntPtr handled;
            int handleResult = handleMenuMessage2(contextMenu3, message, wParam, lParam, &handled);
            result = handled;
            return handleResult >= 0;
        }

        if (contextMenu2 == IntPtr.Zero)
            return false;

        var handleMenuMessage = (delegate* unmanaged[Stdcall]<IntPtr, uint, UIntPtr, IntPtr, int>)
            VtableMethod(contextMenu2, VtableSlot_HandleMenuMessage);
        return handleMenuMessage(contextMenu2, message, wParam, lParam) >= 0;
    }

    private static IntPtr TryQueryInterface(IntPtr instance, Guid interfaceId)
    {
        var queryInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)
            VtableMethod(instance, VtableSlot_QueryInterface);
        IntPtr queried;
        int result = queryInterface(instance, &interfaceId, &queried);
        return result >= 0 ? queried : IntPtr.Zero;
    }

    private static void Release(IntPtr instance)
    {
        if (instance == IntPtr.Zero)
            return;
        var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)VtableMethod(instance, VtableSlot_Release);
        release(instance);
    }

    private static IntPtr VtableMethod(IntPtr instance, int vtableSlot)
    {
        IntPtr vtable = *(IntPtr*)instance;
        return *((IntPtr*)vtable + vtableSlot);
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0)
            Marshal.ThrowExceptionForHR(result);
    }

    private static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");
    private static readonly Guid IID_IContextMenu2 = new("000214F4-0000-0000-C000-000000000046");
    private static readonly Guid IID_IContextMenu3 = new("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719");

    private static readonly UIntPtr ShellMenuSubclassId = new(0x53434D4Eu);

    private const uint CommandIdFirst = 1;
    private const uint CommandIdLast = 0x7FFF;
    private const uint CmfNormal = 0x00000000;
    private const uint CmfExtendedVerbs = 0x00000100;
    private const uint TpmLeftAlign = 0x0000;
    private const uint TpmTopAlign = 0x0000;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCommand = 0x0100;
    private const uint CmicMaskUnicode = 0x00004000;
    private const uint CmicMaskPointInvoke = 0x20000000;
    private const int SwShowNormal = 1;
    private const int VkShift = 0x10;

    private const uint WmDrawItem = 0x002B;
    private const uint WmMeasureItem = 0x002C;
    private const uint WmInitMenuPopup = 0x0117;
    private const uint WmMenuChar = 0x0120;

    private const int VtableSlot_QueryInterface = 0;
    private const int VtableSlot_Release = 2;
    private const int VtableSlot_GetUiObjectOf = 10;
    private const int VtableSlot_QueryContextMenu = 3;
    private const int VtableSlot_InvokeCommand = 4;
    private const int VtableSlot_HandleMenuMessage = 6;
    private const int VtableSlot_HandleMenuMessage2 = 7;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        string displayName,
        IntPtr bindContext,
        out IntPtr itemIdList,
        uint attributesIn,
        out uint attributesOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(
        IntPtr itemIdList,
        in Guid interfaceId,
        out IntPtr parentFolder,
        out IntPtr childItemIdList);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr memory);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(
        IntPtr menu,
        uint flags,
        int x,
        int y,
        IntPtr owner,
        IntPtr trackPopupMenuParameters);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(
        IntPtr window,
        SubclassProc subclassProc,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(
        IntPtr window,
        SubclassProc subclassProc,
        UIntPtr subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        public IntPtr lpTitleW;
        public POINT ptInvoke;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr SubclassProc(
        IntPtr window,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData);
}