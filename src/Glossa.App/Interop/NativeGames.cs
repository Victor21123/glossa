using System.Runtime.InteropServices;
using System.Text;

namespace Glossa.App.Interop;

/// <summary>
/// Win32 for P4: the game's program and window, pausing a process, focus, Raw Input (mouse buttons, HID gamepads) and
/// XInput. Nothing here opens a game for reading or writing its memory: a process handle is asked only for its name
/// (PROCESS_QUERY_LIMITED_INFORMATION) or to suspend it (PROCESS_SUSPEND_RESUME).
/// </summary>
internal static partial class Native
{
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, PROCESS_SUSPEND_RESUME = 0x0800, SYNCHRONIZE = 0x00100000;
    public const int GWL_STYLE = -16;
    public const long WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_SYSMENU = 0x00080000,
        WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
    public const uint SWP_NOZORDER = 0x4, SWP_FRAMECHANGED = 0x20, SWP_NOOWNERZORDER = 0x200;
    public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    public const uint GW_OWNER = 4;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint size);

    [DllImport("ntdll.dll")]
    public static extern int NtSuspendProcess(IntPtr process);

    [DllImport("ntdll.dll")]
    public static extern int NtResumeProcess(IntPtr process);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint attach, uint to, bool on);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    public static extern bool ClipCursor(IntPtr rect);

    [DllImport("shell32.dll")]
    public static extern int SHQueryUserNotificationState(out int state);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    /// <summary>The full path of a process's program, or null (gone, or not even its name may be asked).</summary>
    public static string? ProcessPath(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            var size = (uint)sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, (int)size) : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public static RECT MonitorBoundsOf(IntPtr hwnd)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info);
        return info.rcMonitor;
    }

    /// <summary>
    /// Brings a Glossa window to the front and gives it the keyboard. A hotkey lets Glossa do this directly; after a
    /// mouse button or a gamepad Windows refuses, so the input queues are joined for a moment (no input is sent).
    /// </summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd) return;
        var front = GetForegroundWindow();
        var theirs = GetWindowThreadProcessId(front, out _);
        var mine = GetCurrentThreadId();
        if (theirs == 0 || theirs == mine)
        {
            SetForegroundWindow(hwnd);
            return;
        }
        AttachThreadInput(mine, theirs, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            AttachThreadInput(mine, theirs, false);
        }
    }

    // ---- Raw Input ----

    public const int WM_INPUT = 0x00FF, WM_INPUT_DEVICE_CHANGE = 0x00FE, WM_TIMER = 0x0113, WM_APP = 0x8000, WM_QUIT = 0x0012;
    public const uint RIDEV_REMOVE = 0x1, RIDEV_INPUTSINK = 0x100, RIDEV_DEVNOTIFY = 0x2000;
    public const uint RID_INPUT = 0x10000003, RIM_TYPEMOUSE = 0, RIM_TYPEHID = 2;
    public const uint RIDI_PREPARSEDDATA = 0x20000005, RIDI_DEVICENAME = 0x20000007;
    public const ushort RI_MOUSE_BUTTON_4_DOWN = 0x0040, RI_MOUSE_BUTTON_5_DOWN = 0x0100;
    public const int GIDC_REMOVAL = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE
    {
        public ushort UsagePage, Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTHEADER
    {
        public uint Type, Size;
        public IntPtr Device, WParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    public static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);

    // ---- a plain Win32 window for the input thread (no WPF there) ----

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize, style;
        public WndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(uint exStyle, string className, string name, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    public static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr id, uint ms, IntPtr proc);

    [DllImport("user32.dll")]
    public static extern bool KillTimer(IntPtr hWnd, UIntPtr id);

    [DllImport("user32.dll")]
    public static extern uint MsgWaitForMultipleObjects(uint count, IntPtr[] handles, bool waitAll, uint ms, uint wakeMask);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? name);

    public const uint PM_REMOVE = 1, QS_ALLINPUT = 0x04FF, WAIT_TIMEOUT = 0x102;
    public static readonly IntPtr HWND_MESSAGE = new(-3);

    // ---- HID (hid.dll ships with Windows) ----

    public const int HIDP_STATUS_SUCCESS = 0x00110000;
    public const int HidP_Input = 0;

    [DllImport("hid.dll")]
    public static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection, [Out] ushort[] usages,
        ref uint usageLength, IntPtr preparsed, IntPtr report, uint reportLength);

    [DllImport("hid.dll")]
    public static extern uint HidP_MaxUsageListLength(int reportType, ushort usagePage, IntPtr preparsed);

    [DllImport("hid.dll")]
    public static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage,
        out uint value, IntPtr preparsed, IntPtr report, uint reportLength);

    [DllImport("hid.dll")]
    public static extern int HidP_GetScaledUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage,
        out int value, IntPtr preparsed, IntPtr report, uint reportLength);

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")]
    public static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);

    /// <summary>HIDP_VALUE_CAPS is 72 bytes; read as raw bytes (Input.HidPad names the offsets it uses).</summary>
    [DllImport("hid.dll")]
    public static extern int HidP_GetValueCaps(int reportType, [Out] byte[] caps, ref ushort length, IntPtr preparsed);

    // ---- XInput (xinput1_4.dll ships with Windows 10 and 11) ----

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_GAMEPAD
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_STATE
    {
        public uint PacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [DllImport("xinput1_4.dll")]
    public static extern uint XInputGetState(uint user, out XINPUT_STATE state);

    public const uint ERROR_SUCCESS = 0;
}
