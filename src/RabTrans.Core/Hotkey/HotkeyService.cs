using System.Runtime.InteropServices;

namespace RabTrans.Core.Hotkey;

/// <summary>
/// Win32 Hotkey service using RegisterHotKey API.
/// </summary>
public class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // Modifier keys
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    // Virtual key codes
    public const uint VK_SNAPSHOT = 0x2C;  // Print Screen
    public const uint VK_A = 0x41;
    public const uint VK_C = 0x43;
    public const uint VK_V = 0x56;
    public const uint VK_T = 0x54;
    public const uint VK_ESCAPE = 0x1B;

    private readonly IntPtr _hwnd;
    private readonly Dictionary<int, Action> _hotkeyCallbacks = new();
    private int _currentId = 0;
    private bool _disposed = false;

    public event EventHandler<HotkeyEventArgs>? HotkeyPressed;

    public HotkeyService(IntPtr hwnd)
    {
        _hwnd = hwnd;
    }

    /// <summary>
    /// Registers a global hotkey.
    /// </summary>
    /// <param name="modifiers">Modifier keys (MOD_ALT, MOD_CONTROL, etc.)</param>
    /// <param name="key">Virtual key code</param>
    /// <param name="callback">Callback action when hotkey is pressed</param>
    /// <returns>Hotkey ID, or -1 on failure</returns>
    public int RegisterHotkey(uint modifiers, uint key, Action callback)
    {
        int id = ++_currentId;
        
        if (!RegisterHotKey(_hwnd, id, modifiers | MOD_NOREPEAT, key))
        {
            int error = Marshal.GetLastWin32Error();
            System.Diagnostics.Debug.WriteLine($"Failed to register hotkey: {error}");
            return -1;
        }

        _hotkeyCallbacks[id] = callback;
        return id;
    }

    /// <summary>
    /// Unregisters a hotkey.
    /// </summary>
    /// <param name="id">Hotkey ID returned from RegisterHotkey</param>
    public void UnregisterHotkey(int id)
    {
        if (_hotkeyCallbacks.ContainsKey(id))
        {
            UnregisterHotKey(_hwnd, id);
            _hotkeyCallbacks.Remove(id);
        }
    }

    public void UnregisterAll()
    {
        foreach (var id in _hotkeyCallbacks.Keys.ToList())
        {
            UnregisterHotkey(id);
        }
    }

    /// <summary>
    /// Processes the window message for hotkey events.
    /// </summary>
    public void ProcessMessage(IntPtr wParam)
    {
        int id = wParam.ToInt32();
        if (_hotkeyCallbacks.TryGetValue(id, out var callback))
        {
            callback?.Invoke();
            HotkeyPressed?.Invoke(this, new HotkeyEventArgs(id));
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            foreach (var id in _hotkeyCallbacks.Keys.ToList())
            {
                UnregisterHotKey(_hwnd, id);
            }
            _hotkeyCallbacks.Clear();
            _disposed = true;
        }
    }
}

public class HotkeyEventArgs : EventArgs
{
    public int HotkeyId { get; }

    public HotkeyEventArgs(int hotkeyId)
    {
        HotkeyId = hotkeyId;
    }
}
