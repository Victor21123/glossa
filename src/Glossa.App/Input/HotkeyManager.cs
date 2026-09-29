using System.Windows.Input;
using System.Windows.Interop;
using Glossa.App.Interop;

namespace Glossa.App.Input;

/// <summary>
/// System-wide hotkeys through RegisterHotKey on a message-only window. Unlike low-level hooks these
/// cannot be silently removed by Windows and keep working over games that run elevated.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly HashSet<int> _registered = [];

    public event Action<int>? Pressed;

    public HotkeyManager()
    {
        var p = new HwndSourceParameters("GlossaHotkeys") { ParentWindow = new IntPtr(-3), WindowStyle = 0 };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    public bool Register(int id, string spec)
    {
        Unregister(id);
        if (!TryParse(spec, out var mods, out var vk)) return false;
        if (!Native.RegisterHotKey(_source.Handle, id, mods | Native.MOD_NOREPEAT, vk)) return false;
        _registered.Add(id);
        return true;
    }

    public bool IsRegistered(int id) => _registered.Contains(id);

    public void Unregister(int id)
    {
        if (_registered.Remove(id)) Native.UnregisterHotKey(_source.Handle, id);
    }

    /// <summary>Parses "Alt+Q", "Ctrl+Shift+D", "Escape", "Ctrl+Multiply".</summary>
    public static bool TryParse(string spec, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        foreach (var raw in spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "alt": mods |= Native.MOD_ALT; continue;
                case "ctrl" or "control": mods |= Native.MOD_CONTROL; continue;
                case "shift": mods |= Native.MOD_SHIFT; continue;
                case "win": mods |= Native.MOD_WIN; continue;
            }
            var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
            if (!Enum.TryParse<Key>(name, ignoreCase: true, out var key)) return false;
            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        }
        return vk != 0;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY)
        {
            Pressed?.Invoke(wParam.ToInt32());
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _registered.ToList()) Unregister(id);
        _source.Dispose();
    }
}
