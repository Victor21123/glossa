using System.Runtime.InteropServices;
using Glossa.App.Interop;
using Glossa.Core.Logging;
using Glossa.Core.Ocr;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Glossa.App.Capture;

/// <summary>A captured monitor image, BGRA, top-down. <see cref="Bounds"/> is in physical desktop pixels.</summary>
public sealed record CapturedFrame(byte[] Bgra, int Width, int Height, int Stride, PixelRect Bounds)
{
    /// <summary>Copies a screen-space rectangle out of the frame (clamped to it).</summary>
    public (byte[] Bgra, int Width, int Height, int Stride, PixelRect Region) Crop(PixelRect screen)
    {
        var l = (int)Math.Clamp(screen.Left - Bounds.Left, 0, Width);
        var t = (int)Math.Clamp(screen.Top - Bounds.Top, 0, Height);
        var r = (int)Math.Clamp(screen.Right - Bounds.Left, l, Width);
        var b = (int)Math.Clamp(screen.Bottom - Bounds.Top, t, Height);
        int w = r - l, h = b - t, stride = w * 4;
        var dst = new byte[stride * h];
        for (var y = 0; y < h; y++)
            Buffer.BlockCopy(Bgra, (t + y) * Stride + l * 4, dst, y * stride, stride);
        return (dst, w, h, stride, new PixelRect(Bounds.Left + l, Bounds.Top + t, Bounds.Left + r, Bounds.Top + b));
    }
}

/// <summary>
/// Grabs the monitor under a point with DXGI Desktop Duplication (works for borderless DirectX games and
/// does not touch the game process). A duplication session is opened per capture so the game is not kept
/// out of independent-flip mode between lookups; GDI is the fallback.
/// </summary>
public sealed class ScreenCapture(ILog log) : IDisposable
{
    private readonly Dictionary<long, (ID3D11Device Device, ID3D11DeviceContext Context)> _devices = [];

    public CapturedFrame CaptureMonitorAt(int x, int y)
    {
        try
        {
            var frame = CaptureDxgi(x, y);
            if (frame is not null) return frame;
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or COMException)
        {
            log.Warn($"DXGI capture failed, using GDI: {ex.Message}");
            DropDevices();
        }
        var m = Native.MonitorBoundsAt(new Native.POINT { X = x, Y = y });
        return CaptureGdi(new PixelRect(m.Left, m.Top, m.Right, m.Bottom));
    }

    private CapturedFrame? CaptureDxgi(int x, int y)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var d = output.Description;
                        var rc = d.DesktopCoordinates;
                        if (!d.AttachedToDesktop || x < rc.Left || x >= rc.Right || y < rc.Top || y >= rc.Bottom) continue;
                        return Duplicate(adapter, output, new PixelRect(rc.Left, rc.Top, rc.Right, rc.Bottom));
                    }
                }
            }
        }
        return null;
    }

    private CapturedFrame? Duplicate(IDXGIAdapter1 adapter, IDXGIOutput output, PixelRect bounds)
    {
        var luid = adapter.Description1.Luid;
        var key = ((long)luid.HighPart << 32) | luid.LowPart;
        if (!_devices.TryGetValue(key, out var dev))
        {
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
            dev = (device, context);
            _devices[key] = dev;
        }

        using var output1 = output.QueryInterface<IDXGIOutput1>();
        using var dup = output1.DuplicateOutput(dev.Device);

        // A new duplication only delivers pixels once something is presented. Games present every frame;
        // a static desktop may not, and then the acquired surface is empty (LastPresentTime == 0).
        // Wait briefly for a real frame and let the caller fall back to GDI otherwise.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = dup.AcquireNextFrame(attempt == 0 ? 60u : 90u, out var info, out var resource);
            if (result.Failure) continue;
            if (info.LastPresentTime == 0)
            {
                resource?.Dispose();
                dup.ReleaseFrame();
                continue;
            }
            try
            {
                using var tex = resource!.QueryInterface<ID3D11Texture2D>();
                var td = tex.Description;
                if (td.Format != Format.B8G8R8A8_UNorm)
                {
                    log.Warn($"Desktop format {td.Format} (HDR?) — falling back to GDI");
                    return null;
                }
                return Read(dev.Device, dev.Context, tex, (int)td.Width, (int)td.Height, bounds);
            }
            finally
            {
                resource?.Dispose();
                dup.ReleaseFrame();
            }
        }
        return null;
    }

    private static CapturedFrame Read(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D tex, int w, int h, PixelRect bounds)
    {
        var desc = new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
        };
        using var staging = device.CreateTexture2D(desc);
        context.CopyResource(staging, tex);
        var map = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var stride = w * 4;
            var buffer = new byte[stride * h];
            for (var y = 0; y < h; y++)
                Marshal.Copy(map.DataPointer + y * (int)map.RowPitch, buffer, y * stride, stride);
            return new CapturedFrame(buffer, w, h, stride, bounds);
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    private static CapturedFrame CaptureGdi(PixelRect bounds)
    {
        int w = (int)bounds.Width, h = (int)bounds.Height;
        var screen = Native.GetDC(IntPtr.Zero);
        var mem = Native.CreateCompatibleDC(screen);
        var bmi = new Native.BITMAPINFO
        {
            biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
        };
        var dib = Native.CreateDIBSection(screen, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        var old = Native.SelectObject(mem, dib);
        try
        {
            Native.BitBlt(mem, 0, 0, w, h, screen, (int)bounds.Left, (int)bounds.Top, Native.SRCCOPY | Native.CAPTUREBLT);
            var buffer = new byte[w * h * 4];
            Marshal.Copy(bits, buffer, 0, buffer.Length);
            return new CapturedFrame(buffer, w, h, w * 4, bounds);
        }
        finally
        {
            Native.SelectObject(mem, old);
            Native.DeleteObject(dib);
            Native.DeleteDC(mem);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>
    /// Живой перевод: the monitor under a point duplicated for as long as it runs. Opening a duplication per grab twice a
    /// second would cost the game more, and a device of its own never races a lookup's capture on another thread. Null
    /// when the monitor cannot be duplicated (HDR, a remote session).
    /// </summary>
    public WatchSession? Watch(int x, int y)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var d = output.Description;
                        var rc = d.DesktopCoordinates;
                        if (!d.AttachedToDesktop || x < rc.Left || x >= rc.Right || y < rc.Top || y >= rc.Bottom) continue;
                        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
                        using var output1 = output.QueryInterface<IDXGIOutput1>();
                        return new WatchSession(device, context, output1.DuplicateOutput(device), new PixelRect(rc.Left, rc.Top, rc.Right, rc.Bottom));
                    }
                }
            }
        }
        return null;
    }

    /// <summary>One monitor kept duplicated: <see cref="Grab"/> returns its newest frame, or null when nothing new was presented.</summary>
    public sealed class WatchSession(ID3D11Device device, ID3D11DeviceContext context, IDXGIOutputDuplication dup, PixelRect bounds) : IDisposable
    {
        public PixelRect Bounds { get; } = bounds;

        /// <summary>Throws when the duplication is lost (a mode switch, a full-screen change): open a new session then.</summary>
        public CapturedFrame? Grab()
        {
            var result = dup.AcquireNextFrame(50, out var info, out var resource);
            if (result.Failure)
            {
                if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) return null;
                result.CheckError();
            }
            try
            {
                if (info.LastPresentTime == 0) return null;
                using var tex = resource!.QueryInterface<ID3D11Texture2D>();
                var td = tex.Description;
                return td.Format == Format.B8G8R8A8_UNorm ? Read(device, context, tex, (int)td.Width, (int)td.Height, Bounds) : null;
            }
            finally
            {
                resource?.Dispose();
                dup.ReleaseFrame();
            }
        }

        public void Dispose()
        {
            dup.Dispose();
            context.Dispose();
            device.Dispose();
        }
    }

    private void DropDevices()
    {
        foreach (var (d, c) in _devices.Values) { c.Dispose(); d.Dispose(); }
        _devices.Clear();
    }

    public void Dispose() => DropDevices();
}
