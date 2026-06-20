using System;
using System.Runtime.InteropServices;

namespace Yagu.Helpers;

/// <summary>
/// Minimal wrapper around the Win32 Common Item Dialog (<c>IFileSaveDialog</c> /
/// <c>IFileOpenDialog</c>). Unlike the WinAppSDK <c>FileSavePicker</c>/<c>FileOpenPicker</c>
/// and <c>FolderPicker</c>,
/// which route through a broker that fails with <c>E_FAIL</c> when the process runs elevated,
/// these COM dialogs work whether or not the process is elevated. This helper uses raw
/// CoCreateInstance plus vtable calls so it also works when built-in COM interop is disabled
/// by Yagu's Native AOT settings.
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage] // thin COM interop over a modal OS dialog
internal static unsafe class Win32FileDialog
{
    /// <summary>Shows a Save dialog. Returns the chosen full path, or null if cancelled.</summary>
    public static string? Save(
        IntPtr owner,
        string title,
        string suggestedFileName,
        string defaultExtension,
        (string Name, string Spec)[] filters)
        => Show(CLSID_FileSaveDialog, owner, title, suggestedFileName, defaultExtension, filters,
                FOS_OVERWRITEPROMPT | FOS_PATHMUSTEXIST | FOS_NOREADONLYRETURN);

    /// <summary>Shows an Open dialog. Returns the chosen full path, or null if cancelled.</summary>
    public static string? Open(
        IntPtr owner,
        string title,
        (string Name, string Spec)[] filters)
        => Show(CLSID_FileOpenDialog, owner, title, null, null, filters,
                FOS_FILEMUSTEXIST | FOS_PATHMUSTEXIST);

    /// <summary>Shows a folder picker dialog. Returns the chosen full path, or null if cancelled.</summary>
    public static string? SelectFolder(IntPtr owner, string title)
        => Show(CLSID_FileOpenDialog, owner, title, null, null, [],
                FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);

    private static string? Show(
        Guid clsid,
        IntPtr owner,
        string title,
        string? suggestedFileName,
        string? defaultExtension,
        (string Name, string Spec)[] filters,
        uint options)
    {
        int createResult = CoCreateInstance(in clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, in IID_IFileDialog, out IntPtr dialog);
        ThrowIfFailed(createResult);
        if (dialog == IntPtr.Zero)
            throw new InvalidOperationException("Common Item Dialog COM class is unavailable.");

        try
        {
            SetDialogString(dialog, VtableSlot_SetTitle, title);

            if (filters is { Length: > 0 })
            {
                SetFileTypes(dialog, filters);
                ThrowIfFailed(GetMethod<SetUIntDelegate>(dialog, VtableSlot_SetFileTypeIndex)(dialog, 1));
            }

            SetDialogString(dialog, VtableSlot_SetFileName, suggestedFileName);

            if (!string.IsNullOrEmpty(defaultExtension))
                SetDialogString(dialog, VtableSlot_SetDefaultExtension, defaultExtension.TrimStart('.'));

            ThrowIfFailed(GetMethod<SetUIntDelegate>(dialog, VtableSlot_SetOptions)(dialog, options));

            int showResult = GetMethod<ShowDelegate>(dialog, VtableSlot_Show)(dialog, owner);
            if (showResult == HRESULT_ERROR_CANCELLED) return null; // user dismissed the dialog
            ThrowIfFailed(showResult);

            ThrowIfFailed(GetMethod<GetResultDelegate>(dialog, VtableSlot_GetResult)(dialog, out IntPtr item));
            try
            {
                ThrowIfFailed(GetMethod<GetDisplayNameDelegate>(item, VtableSlot_GetDisplayName)(item, SIGDN_FILESYSPATH, out IntPtr pathPointer));
                string? path;
                try
                {
                    path = Marshal.PtrToStringUni(pathPointer);
                }
                finally
                {
                    if (pathPointer != IntPtr.Zero)
                        Marshal.FreeCoTaskMem(pathPointer);
                }

                if (string.IsNullOrWhiteSpace(path))
                    throw new InvalidOperationException("Common Item Dialog did not return a file-system path.");
                return path;
            }
            finally
            {
                if (item != IntPtr.Zero)
                    Marshal.Release(item);
            }
        }
        finally
        {
            Marshal.Release(dialog);
        }
    }

    private static void SetDialogString(IntPtr dialog, int vtableSlot, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;

        IntPtr valuePointer = Marshal.StringToCoTaskMemUni(value);
        try
        {
            ThrowIfFailed(GetMethod<SetStringDelegate>(dialog, vtableSlot)(dialog, valuePointer));
        }
        finally
        {
            Marshal.FreeCoTaskMem(valuePointer);
        }
    }

    private static void SetFileTypes(IntPtr dialog, (string Name, string Spec)[] filters)
    {
        int specSize = sizeof(COMDLG_FILTERSPEC);
        IntPtr specsPointer = Marshal.AllocCoTaskMem(specSize * filters.Length);
        var namePointers = new IntPtr[filters.Length];
        var specPointers = new IntPtr[filters.Length];

        try
        {
            var specs = (COMDLG_FILTERSPEC*)specsPointer;
            for (int index = 0; index < filters.Length; index++)
            {
                namePointers[index] = Marshal.StringToCoTaskMemUni(filters[index].Name);
                specPointers[index] = Marshal.StringToCoTaskMemUni(filters[index].Spec);
                specs[index] = new COMDLG_FILTERSPEC
                {
                    pszName = namePointers[index],
                    pszSpec = specPointers[index],
                };
            }

            ThrowIfFailed(GetMethod<SetFileTypesDelegate>(dialog, VtableSlot_SetFileTypes)(dialog, (uint)filters.Length, specsPointer));
        }
        finally
        {
            for (int index = 0; index < filters.Length; index++)
            {
                if (namePointers[index] != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(namePointers[index]);
                if (specPointers[index] != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(specPointers[index]);
            }

            Marshal.FreeCoTaskMem(specsPointer);
        }
    }

    private static TDelegate GetMethod<TDelegate>(IntPtr instance, int vtableSlot)
        where TDelegate : Delegate
    {
        IntPtr vtable = *(IntPtr*)instance;
        IntPtr methodPointer = *((IntPtr*)vtable + vtableSlot);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(methodPointer);
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0)
            Marshal.ThrowExceptionForHR(result);
    }

    private static readonly Guid CLSID_FileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    private static readonly Guid CLSID_FileSaveDialog = new("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B");
    private static readonly Guid IID_IFileDialog = new("42F85136-DB7E-439C-85F1-E4075D135FC8");

    private const uint CLSCTX_INPROC_SERVER = 1;

    private const uint FOS_OVERWRITEPROMPT = 0x00000002;
    private const uint FOS_NOREADONLYRETURN = 0x00008000;
    private const uint FOS_PATHMUSTEXIST = 0x00000800;
    private const uint FOS_FILEMUSTEXIST = 0x00001000;
    private const uint FOS_PICKFOLDERS = 0x00000020;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const int HRESULT_ERROR_CANCELLED = unchecked((int)0x800704C7);

    private const int VtableSlot_Show = 3;
    private const int VtableSlot_SetFileTypes = 4;
    private const int VtableSlot_SetFileTypeIndex = 5;
    private const int VtableSlot_SetOptions = 9;
    private const int VtableSlot_SetFileName = 15;
    private const int VtableSlot_SetTitle = 17;
    private const int VtableSlot_GetResult = 20;
    private const int VtableSlot_SetDefaultExtension = 22;
    private const int VtableSlot_GetDisplayName = 5;

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        in Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        in Guid riid,
        out IntPtr ppv);

    [StructLayout(LayoutKind.Sequential)]
    private struct COMDLG_FILTERSPEC
    {
        public IntPtr pszName;
        public IntPtr pszSpec;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ShowDelegate(IntPtr instance, IntPtr owner);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetFileTypesDelegate(IntPtr instance, uint fileTypeCount, IntPtr filterSpecs);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetUIntDelegate(IntPtr instance, uint value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetStringDelegate(IntPtr instance, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetResultDelegate(IntPtr instance, out IntPtr item);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDisplayNameDelegate(IntPtr instance, uint displayNameKind, out IntPtr name);
}
