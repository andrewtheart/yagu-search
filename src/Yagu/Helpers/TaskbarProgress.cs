using System.Runtime.InteropServices;
using Yagu.Services;

namespace Yagu.Helpers;

/// <summary>
/// Provides taskbar progress-bar overlay via ITaskbarList3 COM interface.
/// Uses raw CoCreateInstance + vtable calls to bypass the managed COM interop
/// feature switch that PublishAot disables.
/// </summary>
internal static unsafe class TaskbarProgress
{
    private const int TBPF_NOPROGRESS = 0x00;
    private const int TBPF_NORMAL = 0x02;

    // ITaskbarList3 IID: ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf
    private static readonly Guid IID_ITaskbarList3 = new(0xea1afb91, 0x9e28, 0x4b86, 0x90, 0xe9, 0x9e, 0x9f, 0x8a, 0x5e, 0xef, 0xaf);
    // TaskbarList CLSID: 56fdf344-fd6d-11d0-958a-006097c9a090
    private static readonly Guid CLSID_TaskbarList = new(0x56fdf344, 0xfd6d, 0x11d0, 0x95, 0x8a, 0x00, 0x60, 0x97, 0xc9, 0xa0, 0x90);

    private const uint CLSCTX_INPROC_SERVER = 1;

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);

    // Vtable slot indices (after IUnknown: QueryInterface=0, AddRef=1, Release=2)
    // ITaskbarList: HrInit=3, AddTab=4, DeleteTab=5, ActivateTab=6, SetActiveAlt=7
    // ITaskbarList2: MarkFullscreenWindow=8
    // ITaskbarList3: SetProgressValue=9, SetProgressState=10
    private const int VtableSlot_HrInit = 3;
    private const int VtableSlot_SetProgressValue = 9;
    private const int VtableSlot_SetProgressState = 10;

    // Delegate types matching the COM vtable signatures
    private delegate int HrInitDelegate(IntPtr pThis);
    private delegate int SetProgressValueDelegate(IntPtr pThis, IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
    private delegate int SetProgressStateDelegate(IntPtr pThis, IntPtr hwnd, int tbpFlags);

    private static IntPtr _instance;
    private static bool _initAttempted;

    private static IntPtr GetInstance()
    {
        if (_instance != IntPtr.Zero) return _instance;
        if (_initAttempted) return IntPtr.Zero;
        _initAttempted = true;

        try
        {
            int hr = CoCreateInstance(in CLSID_TaskbarList, IntPtr.Zero, CLSCTX_INPROC_SERVER, in IID_ITaskbarList3, out IntPtr pTaskbar);
            if (hr != 0 || pTaskbar == IntPtr.Zero)
            {
                LogService.Instance.Warning("TaskbarProgress", $"CoCreateInstance failed: hr=0x{hr:X8}");
                return IntPtr.Zero;
            }

            // Call HrInit (vtable slot 3)
            IntPtr vtable = *(IntPtr*)pTaskbar;
            IntPtr hrInitPtr = *((IntPtr*)vtable + VtableSlot_HrInit);
            var hrInit = Marshal.GetDelegateForFunctionPointer<HrInitDelegate>(hrInitPtr);
            int initHr = hrInit(pTaskbar);
            if (initHr != 0)
            {
                LogService.Instance.Warning("TaskbarProgress", $"HrInit failed: hr=0x{initHr:X8}");
                Marshal.Release(pTaskbar);
                return IntPtr.Zero;
            }

            LogService.Instance.Warning("TaskbarProgress", "COM initialized successfully via CoCreateInstance");
            _instance = pTaskbar;
        }
        catch (Exception ex)
        {
            LogService.Instance.Warning("TaskbarProgress", $"Init exception: {ex.Message}");
        }
        return _instance;
    }

    public static void SetProgress(IntPtr hwnd, ulong completed, ulong total)
    {
        IntPtr pTaskbar = GetInstance();
        if (pTaskbar == IntPtr.Zero || hwnd == IntPtr.Zero) return;

        IntPtr vtable = *(IntPtr*)pTaskbar;

        // SetProgressState (slot 10)
        IntPtr setStatePtr = *((IntPtr*)vtable + VtableSlot_SetProgressState);
        var setState = Marshal.GetDelegateForFunctionPointer<SetProgressStateDelegate>(setStatePtr);
        setState(pTaskbar, hwnd, TBPF_NORMAL);

        // SetProgressValue (slot 9)
        IntPtr setValuePtr = *((IntPtr*)vtable + VtableSlot_SetProgressValue);
        var setValue = Marshal.GetDelegateForFunctionPointer<SetProgressValueDelegate>(setValuePtr);
        setValue(pTaskbar, hwnd, completed, total);
    }

    public static void ClearProgress(IntPtr hwnd)
    {
        IntPtr pTaskbar = GetInstance();
        if (pTaskbar == IntPtr.Zero || hwnd == IntPtr.Zero) return;

        IntPtr vtable = *(IntPtr*)pTaskbar;
        IntPtr setStatePtr = *((IntPtr*)vtable + VtableSlot_SetProgressState);
        var setState = Marshal.GetDelegateForFunctionPointer<SetProgressStateDelegate>(setStatePtr);
        setState(pTaskbar, hwnd, TBPF_NOPROGRESS);
    }
}
