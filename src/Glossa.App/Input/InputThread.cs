using System.Diagnostics;
using System.Runtime.InteropServices;
using Glossa.App.Interop;
using Glossa.Core.Input;
using Glossa.Core.Logging;

namespace Glossa.App.Input;

/// <summary>
/// Mouse side buttons and gamepads, on a thread of their own so a 1000 Hz mouse never touches the window's thread.
/// Mouse and HID pads come through Raw Input (RIDEV_INPUTSINK: delivered while a game is in front; nothing is hooked,
/// nothing is taken from the game), XInput pads are polled 30 times a second. Each part is on only while a setting
/// needs it; switched off, the thread sleeps in GetMessage and costs nothing.
/// </summary>
public sealed class InputThread : IDisposable
{
    private const uint WmConfigure = Native.WM_APP + 1;
    private static readonly UIntPtr PollTimer = new(1);
    private const ushort RawMouse = 2, RawJoystick = 4, RawGamepad = 5;

    private readonly ILog _log;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly Dictionary<IntPtr, HidPad?> _hid = [];
    private readonly PadButtons[] _xinput = new PadButtons[4];
    private readonly bool[] _connected = new bool[4];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Native.WndProc? _proc;
    private IntPtr _hwnd;
    private uint _threadId;
    private IntPtr _buffer;
    private int _bufferSize;
    private volatile bool _wantMouse, _wantPad;
    private bool _mouseOn, _padOn;
    private long _probeAt;
    private PadButtons _last;
    private bool _xinputMissing;

    /// <summary>Side button 4 or 5 went down (raised on the input thread).</summary>
    public event Action<int>? MouseButton;

    /// <summary>The buttons held on all pads together changed (raised on the input thread).</summary>
    public event Action<PadButtons>? PadChanged;

    public InputThread(ILog log)
    {
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "Glossa input" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Which sources to listen to; applied on the input thread.</summary>
    public void Configure(bool mouse, bool pad)
    {
        _wantMouse = mouse;
        _wantPad = pad;
        if (_hwnd != IntPtr.Zero) Native.PostMessage(_hwnd, WmConfigure, IntPtr.Zero, IntPtr.Zero);
    }

    private void Run()
    {
        try
        {
            _threadId = Native.GetCurrentThreadId();
            _proc = WndProc;
            var hInstance = Native.GetModuleHandle(null);
            var wc = new Native.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
                lpfnWndProc = _proc,
                hInstance = hInstance,
                lpszClassName = "GlossaInput",
            };
            Native.RegisterClassEx(ref wc);
            _hwnd = Native.CreateWindowEx(0, "GlossaInput", "", 0, 0, 0, 0, 0, Native.HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);
        }
        finally
        {
            _ready.Set();
        }
        if (_hwnd == IntPtr.Zero)
        {
            _log.Warn("input thread: no window, mouse buttons and gamepads are off");
            return;
        }
        Apply();
        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) Native.DispatchMessage(ref msg);
        Native.DestroyWindow(_hwnd);
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        foreach (var pad in _hid.Values) pad?.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case WmConfigure:
                    Apply();
                    return IntPtr.Zero;
                case Native.WM_TIMER:
                    PollXInput();
                    Merge();
                    return IntPtr.Zero;
                case Native.WM_INPUT:
                    OnRawInput(lParam);
                    break; // DefWindowProc frees the input
                case Native.WM_INPUT_DEVICE_CHANGE when wParam.ToInt32() == Native.GIDC_REMOVAL:
                    if (_hid.Remove(lParam, out var gone)) gone?.Dispose();
                    Merge();
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            _log.Error("input thread", ex);
        }
        return Native.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void Apply()
    {
        var size = (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>();
        if (_wantMouse != _mouseOn)
        {
            var mouse = new Native.RAWINPUTDEVICE
            {
                UsagePage = 1, Usage = RawMouse,
                Flags = _wantMouse ? Native.RIDEV_INPUTSINK : Native.RIDEV_REMOVE,
                Target = _wantMouse ? _hwnd : IntPtr.Zero,
            };
            if (Native.RegisterRawInputDevices([mouse], 1, size)) _mouseOn = _wantMouse;
            else _log.Warn($"raw input (mouse) not registered: {Marshal.GetLastWin32Error()}");
        }
        if (_wantPad != _padOn)
        {
            var flags = _wantPad ? Native.RIDEV_INPUTSINK | Native.RIDEV_DEVNOTIFY : Native.RIDEV_REMOVE;
            var target = _wantPad ? _hwnd : IntPtr.Zero;
            Native.RAWINPUTDEVICE[] pads =
            [
                new() { UsagePage = 1, Usage = RawJoystick, Flags = flags, Target = target },
                new() { UsagePage = 1, Usage = RawGamepad, Flags = flags, Target = target },
            ];
            if (!Native.RegisterRawInputDevices(pads, 2, size)) _log.Warn($"raw input (gamepads) not registered: {Marshal.GetLastWin32Error()}");
            if (_wantPad)
            {
                Native.SetTimer(_hwnd, PollTimer, 33, IntPtr.Zero);
                _probeAt = 0;
            }
            else
            {
                Native.KillTimer(_hwnd, PollTimer);
                Array.Clear(_xinput);
                Array.Clear(_connected);
                foreach (var pad in _hid.Values) pad?.Dispose();
                _hid.Clear();
                Merge();
            }
            _padOn = _wantPad;
        }
    }

    private void OnRawInput(IntPtr handle)
    {
        var headerSize = (uint)Marshal.SizeOf<Native.RAWINPUTHEADER>();
        uint size = 0;
        Native.GetRawInputData(handle, Native.RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return;
        if (size > _bufferSize)
        {
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _bufferSize = (int)Math.Max(size, 256);
            _buffer = Marshal.AllocHGlobal(_bufferSize);
        }
        if (Native.GetRawInputData(handle, Native.RID_INPUT, _buffer, ref size, headerSize) != size) return;
        var header = Marshal.PtrToStructure<Native.RAWINPUTHEADER>(_buffer);

        if (header.Type == Native.RIM_TYPEMOUSE)
        {
            // RAWMOUSE: usFlags (2 bytes), then the button flags at +4 (the union is 4-byte aligned).
            var buttons = (ushort)Marshal.ReadInt16(_buffer, (int)headerSize + 4);
            if ((buttons & Native.RI_MOUSE_BUTTON_4_DOWN) != 0) MouseButton?.Invoke(4);
            if ((buttons & Native.RI_MOUSE_BUTTON_5_DOWN) != 0) MouseButton?.Invoke(5);
            return;
        }
        if (header.Type != Native.RIM_TYPEHID || !_padOn) return;

        if (!_hid.TryGetValue(header.Device, out var pad)) _hid[header.Device] = pad = HidPad.Open(header.Device);
        if (pad is null) return;
        // RAWHID: dwSizeHid, dwCount, then dwCount reports of dwSizeHid bytes.
        var reportSize = (uint)Marshal.ReadInt32(_buffer, (int)headerSize);
        var count = Marshal.ReadInt32(_buffer, (int)headerSize + 4);
        var first = _buffer + (int)headerSize + 8;
        for (var i = 0; i < count; i++) pad.State = pad.Parse(first + i * (int)reportSize, reportSize);
        Merge();
    }

    /// <summary>Connected XInput pads every tick; empty slots once in 2 s (asking an empty slot is slow).</summary>
    private void PollXInput()
    {
        if (_xinputMissing) return;
        var now = _clock.ElapsedMilliseconds;
        var probe = now >= _probeAt;
        for (uint slot = 0; slot < 4; slot++)
        {
            if (!_connected[slot] && !probe) continue;
            Native.XINPUT_STATE state;
            try
            {
                if (Native.XInputGetState(slot, out state) != Native.ERROR_SUCCESS)
                {
                    _connected[slot] = false;
                    _xinput[slot] = PadButtons.None;
                    continue;
                }
            }
            catch (DllNotFoundException)
            {
                _xinputMissing = true;
                return;
            }
            _connected[slot] = true;
            var g = state.Gamepad;
            // XInput's button bits are the low bits of PadButtons (D-pad, Menu, View, sticks, shoulders, A B X Y).
            var buttons = (PadButtons)(g.Buttons & 0xF3FF);
            if (g.LeftTrigger > 64) buttons |= PadButtons.LT;
            if (g.RightTrigger > 64) buttons |= PadButtons.RT;
            buttons |= Pad.StickDirections(g.ThumbLX / 32767.0, g.ThumbLY / 32767.0);
            _xinput[slot] = buttons;
        }
        if (probe) _probeAt = now + 2000;
    }

    private void Merge()
    {
        var merged = PadButtons.None;
        foreach (var b in _xinput) merged |= b;
        foreach (var pad in _hid.Values) if (pad is not null) merged |= pad.State;
        if (merged == _last) return;
        _last = merged;
        PadChanged?.Invoke(merged);
    }

    public void Dispose()
    {
        if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }
}
