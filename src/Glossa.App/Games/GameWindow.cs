using System.Diagnostics;
using Glossa.App.Interop;
using Glossa.Core.Games;
using Glossa.Core.Ocr;

namespace Glossa.App.Games;

/// <summary>
/// The window a lookup starts in (usually a game): its program, title, place on the monitor and how it is shown.
/// Only window facts are asked of Windows; the game's process is never opened beyond its name.
/// </summary>
public sealed record GameWindow(IntPtr Hwnd, int Pid, string ExePath, string Title, WindowMode Mode, PixelRect Bounds, PixelRect Monitor)
{
    /// <summary>Original style and place of windows made frameless, to give the frame back.</summary>
    private static readonly Dictionary<IntPtr, (long Style, Native.RECT Rect)> Restyled = [];

    /// <summary>"P5R.exe": what the dictionary stores with each sentence.</summary>
    public string ExeName => Path.GetFileName(ExePath);

    public bool IsGlossa => Pid == Environment.ProcessId;

    public static GameWindow Foreground() => Of(Native.GetForegroundWindow());

    public static GameWindow Of(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        var path = Native.ProcessPath((int)pid) ?? NameOnly((int)pid);
        Native.GetWindowRect(hwnd, out var r);
        var m = Native.MonitorBoundsOf(hwnd);
        var style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
        var exclusive = Native.SHQueryUserNotificationState(out var state) == 0 && state == Native.QUNS_RUNNING_D3D_FULL_SCREEN
                        && Native.GetForegroundWindow() == hwnd;
        var bounds = new PixelRect(r.Left, r.Top, r.Right, r.Bottom);
        var monitor = new PixelRect(m.Left, m.Top, m.Right, m.Bottom);
        var mode = WindowModes.Classify(bounds, monitor, (style & Native.WS_CAPTION) == Native.WS_CAPTION, (style & Native.WS_THICKFRAME) != 0, exclusive);
        return new GameWindow(hwnd, (int)pid, path, Native.WindowTitle(hwnd), mode, bounds, monitor);
    }

    /// <summary>
    /// The main window of a running game given its programs (by path, then by file name), or null when it is not running.
    /// Its biggest visible top-level window is taken — a launcher's or a game's small helper windows lose.
    /// </summary>
    public static GameWindow? FindRunning(IReadOnlyCollection<string> programs)
    {
        if (programs.Count == 0) return null;
        var names = programs.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var paths = new Dictionary<uint, string?>();
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd) || Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) return true;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (!paths.TryGetValue(pid, out var path)) paths[pid] = path = Native.ProcessPath((int)pid);
            if (path is null) return true;
            var match = programs.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) || names.Contains(Path.GetFileName(path));
            if (!match) return true;
            Native.GetWindowRect(hwnd, out var r);
            var area = (long)r.Width * r.Height;
            if (area > bestArea) { bestArea = area; best = hwnd; }
            return true;
        }, IntPtr.Zero);
        return best == IntPtr.Zero ? null : Of(best);
    }

    /// <summary>
    /// «Растягивать без рамки»: takes the frame off the game's window and stretches it over its monitor, so the card
    /// and the still frame show over it. Only the window's style and place change, as with any window manager tool.
    /// </summary>
    public bool MakeBorderless()
    {
        if (!Native.IsWindow(Hwnd)) return false;
        var style = Native.GetWindowLongPtr(Hwnd, Native.GWL_STYLE).ToInt64();
        Native.GetWindowRect(Hwnd, out var rect);
        lock (Restyled) Restyled.TryAdd(Hwnd, (style, rect));
        var frameless = style & ~(Native.WS_CAPTION | Native.WS_THICKFRAME | Native.WS_SYSMENU | Native.WS_MINIMIZEBOX | Native.WS_MAXIMIZEBOX);
        Native.SetWindowLongPtr(Hwnd, Native.GWL_STYLE, new IntPtr(frameless));
        return Native.SetWindowPos(Hwnd, IntPtr.Zero, (int)Monitor.Left, (int)Monitor.Top, (int)Monitor.Width, (int)Monitor.Height,
            Native.SWP_FRAMECHANGED | Native.SWP_NOZORDER | Native.SWP_NOOWNERZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>Gives back the frame and place a window had before <see cref="MakeBorderless"/>, while it still runs.</summary>
    public bool RestoreFrame()
    {
        (long Style, Native.RECT Rect) was;
        lock (Restyled)
            if (!Restyled.Remove(Hwnd, out was)) return false;
        if (!Native.IsWindow(Hwnd)) return false;
        Native.SetWindowLongPtr(Hwnd, Native.GWL_STYLE, new IntPtr(was.Style));
        return Native.SetWindowPos(Hwnd, IntPtr.Zero, was.Rect.Left, was.Rect.Top, was.Rect.Width, was.Rect.Height,
            Native.SWP_FRAMECHANGED | Native.SWP_NOZORDER | Native.SWP_NOOWNERZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>Gives the keyboard back to the game after a still frame.</summary>
    public void Focus()
    {
        if (Native.IsWindow(Hwnd)) Native.SetForegroundWindow(Hwnd);
    }

    private static string NameOnly(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName + ".exe";
        }
        catch (Exception)
        {
            return "";
        }
    }
}
