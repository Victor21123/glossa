using System.Runtime.InteropServices;
using Glossa.App.Interop;
using Glossa.Core.Input;

namespace Glossa.App.Input;

/// <summary>
/// A gamepad that is not an XInput one (DualSense, DualShock 4, Switch Pro and others), read from its HID reports with
/// Windows' own hid.dll: buttons, the hat switch as the D-pad, the left stick. XInput pads are skipped here —
/// XInput reads them already.
/// </summary>
internal sealed class HidPad : IDisposable
{
    private const ushort GenericDesktop = 0x01, ButtonPage = 0x09, UsageX = 0x30, UsageY = 0x31, UsageHat = 0x39;

    private readonly IntPtr _preparsed;
    private readonly int _vendor;
    private readonly ushort[] _usages;
    private readonly Axis? _x, _y, _hat;

    private sealed record Axis(int Min, int Max, bool Signed);

    private HidPad(IntPtr preparsed, int vendor, uint maxUsages, Axis? x, Axis? y, Axis? hat)
    {
        _preparsed = preparsed;
        _vendor = vendor;
        _usages = new ushort[Math.Max(1, maxUsages)];
        _x = x;
        _y = y;
        _hat = hat;
    }

    /// <summary>The pad's buttons as of its last report.</summary>
    public PadButtons State { get; set; }

    /// <summary>Opens a HID game controller for reading its reports; null for an XInput pad or one Windows will not describe.</summary>
    public static HidPad? Open(IntPtr device)
    {
        var name = DeviceName(device);
        if (name is null || name.Contains("IG_", StringComparison.OrdinalIgnoreCase)) return null; // XInput-compatible
        uint size = 0;
        Native.GetRawInputDeviceInfo(device, Native.RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        if (size == 0) return null;
        var preparsed = Marshal.AllocHGlobal((int)size);
        if (Native.GetRawInputDeviceInfo(device, Native.RIDI_PREPARSEDDATA, preparsed, ref size) == unchecked((uint)-1)
            || Native.HidP_GetCaps(preparsed, out var caps) != Native.HIDP_STATUS_SUCCESS)
        {
            Marshal.FreeHGlobal(preparsed);
            return null;
        }

        // HIDP_VALUE_CAPS, 72 bytes each: UsagePage @0, IsRange @12, BitSize @18, LogicalMin @40, LogicalMax @44,
        // Usage / UsageMin @56, UsageMax @58.
        Axis? x = null, y = null, hat = null;
        var count = caps.NumberInputValueCaps;
        if (count > 0)
        {
            var buffer = new byte[72 * count];
            if (Native.HidP_GetValueCaps(Native.HidP_Input, buffer, ref count, preparsed) == Native.HIDP_STATUS_SUCCESS)
            {
                for (var i = 0; i < count; i++)
                {
                    var o = i * 72;
                    if (BitConverter.ToUInt16(buffer, o) != GenericDesktop) continue;
                    var isRange = buffer[o + 12] != 0;
                    var bits = BitConverter.ToUInt16(buffer, o + 18);
                    var min = BitConverter.ToInt32(buffer, o + 40);
                    var max = BitConverter.ToInt32(buffer, o + 44);
                    if (max < min && bits is > 0 and < 32) max = (int)((1u << bits) - 1); // an unsigned range written as signed
                    var from = BitConverter.ToUInt16(buffer, o + 56);
                    var to = isRange ? BitConverter.ToUInt16(buffer, o + 58) : from;
                    var axis = new Axis(min, max, min < 0);
                    if (UsageX >= from && UsageX <= to) x ??= axis;
                    if (UsageY >= from && UsageY <= to) y ??= axis;
                    if (UsageHat >= from && UsageHat <= to) hat ??= axis;
                }
            }
        }
        return new HidPad(preparsed, VendorOf(name), Native.HidP_MaxUsageListLength(Native.HidP_Input, ButtonPage, preparsed), x, y, hat);
    }

    /// <summary>Reads one input report into buttons with Xbox names.</summary>
    public PadButtons Parse(IntPtr report, uint length)
    {
        var state = PadButtons.None;
        var n = (uint)_usages.Length;
        if (Native.HidP_GetUsages(Native.HidP_Input, ButtonPage, 0, _usages, ref n, _preparsed, report, length) == Native.HIDP_STATUS_SUCCESS)
            for (var i = 0; i < n; i++) state |= Pad.FromHid(_vendor, _usages[i]);

        if (_hat is { } hat && Value(UsageHat, hat, report, length) is { } h && hat.Max - hat.Min == 7) state |= Pad.Hat(h - hat.Min);
        if (_x is { } ax && _y is { } ay && Value(UsageX, ax, report, length) is { } vx && Value(UsageY, ay, report, length) is { } vy)
            state |= Pad.StickDirections(Normal(vx, ax), -Normal(vy, ay)); // HID's Y grows downwards
        return state;
    }

    private int? Value(ushort usage, Axis axis, IntPtr report, uint length)
    {
        if (axis.Signed)
            return Native.HidP_GetScaledUsageValue(Native.HidP_Input, GenericDesktop, 0, usage, out var s, _preparsed, report, length) == Native.HIDP_STATUS_SUCCESS ? s : null;
        return Native.HidP_GetUsageValue(Native.HidP_Input, GenericDesktop, 0, usage, out var u, _preparsed, report, length) == Native.HIDP_STATUS_SUCCESS ? (int)u : null;
    }

    private static double Normal(int v, Axis a) => a.Max > a.Min ? (v - a.Min) * 2.0 / (a.Max - a.Min) - 1 : 0;

    private static string? DeviceName(IntPtr device)
    {
        uint chars = 0;
        Native.GetRawInputDeviceInfo(device, Native.RIDI_DEVICENAME, IntPtr.Zero, ref chars);
        if (chars == 0) return null;
        var buffer = Marshal.AllocHGlobal((int)chars * 2);
        try
        {
            return Native.GetRawInputDeviceInfo(device, Native.RIDI_DEVICENAME, buffer, ref chars) == unchecked((uint)-1)
                ? null
                : Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>"\\?\HID#VID_054C&amp;PID_0CE6…" → 0x054C.</summary>
    private static int VendorOf(string name)
    {
        var at = name.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        return at >= 0 && at + 8 <= name.Length && int.TryParse(name.AsSpan(at + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out var vid) ? vid : 0;
    }

    public void Dispose() => Marshal.FreeHGlobal(_preparsed);
}
