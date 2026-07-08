using System.Runtime.InteropServices;
using System.Text;

namespace RabTrans.Core.Clipboard;

/// <summary>
/// Clipboard monitoring service using Win32 APIs.
/// </summary>
public class ClipboardMonitorService : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int CF_UNICODETEXT = 13;
    
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll")]
    private static extern bool SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint uFlags, uint dwBytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    private const uint GMEM_MOVEABLE = 0x0002;

    private readonly IntPtr _hwnd;
    private bool _isMonitoring = false;
    private bool _disposed = false;
    private string _lastClipboardText = "";

    public event EventHandler<ClipboardChangedEventArgs>? ClipboardChanged;

    public ClipboardMonitorService(IntPtr hwnd)
    {
        _hwnd = hwnd;
    }

    /// <summary>
    /// Starts monitoring clipboard changes.
    /// </summary>
    public void Start()
    {
        if (!_isMonitoring)
        {
            if (AddClipboardFormatListener(_hwnd))
            {
                _isMonitoring = true;
            }
        }
    }

    /// <summary>
    /// Stops monitoring clipboard changes.
    /// </summary>
    public void Stop()
    {
        if (_isMonitoring)
        {
            RemoveClipboardFormatListener(_hwnd);
            _isMonitoring = false;
        }
    }

    /// <summary>
    /// Processes clipboard update messages.
    /// </summary>
    public void ProcessClipboardUpdate()
    {
        try
        {
            var text = GetClipboardText();
            if (!string.IsNullOrWhiteSpace(text) && text != _lastClipboardText)
            {
                _lastClipboardText = text;
                ClipboardChanged?.Invoke(this, new ClipboardChangedEventArgs(text));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Clipboard update error: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the current clipboard text.
    /// </summary>
    public string? GetClipboardText()
    {
        try
        {
            if (!OpenClipboard(_hwnd))
                return null;

            try
            {
                IntPtr hData = GetClipboardData(CF_UNICODETEXT);
                if (hData == IntPtr.Zero)
                    return null;

                IntPtr pData = GlobalLock(hData);
                if (pData == IntPtr.Zero)
                    return null;

                try
                {
                    return Marshal.PtrToStringUni(pData);
                }
                finally
                {
                    GlobalUnlock(hData);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch
        {
            // Clipboard may be locked by another process
        }
        return null;
    }

    /// <summary>
    /// Sets the clipboard text.
    /// </summary>
    public void SetClipboardText(string text)
    {
        try
        {
            // Temporarily disable monitoring to avoid triggering our own event
            _lastClipboardText = text;

            var textBytes = Encoding.Unicode.GetBytes(text + "\0");
            IntPtr hData = GlobalAlloc(GMEM_MOVEABLE, (uint)textBytes.Length);
            
            if (hData == IntPtr.Zero)
                return;

            IntPtr pData = GlobalLock(hData);
            if (pData == IntPtr.Zero)
            {
                GlobalFree(hData);
                return;
            }

            try
            {
                Marshal.Copy(textBytes, 0, pData, textBytes.Length);
            }
            finally
            {
                GlobalUnlock(hData);
            }

            if (!OpenClipboard(_hwnd))
            {
                GlobalFree(hData);
                return;
            }

            try
            {
                EmptyClipboard();
                SetClipboardData(CF_UNICODETEXT, hData);
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Set clipboard error: {ex.Message}");
        }
    }

    /// <summary>
    /// Clears the clipboard.
    /// </summary>
    public void ClearClipboard()
    {
        try
        {
            if (OpenClipboard(_hwnd))
            {
                try
                {
                    EmptyClipboard();
                    _lastClipboardText = "";
                }
                finally
                {
                    CloseClipboard();
                }
            }
        }
        catch
        {
            // Clipboard may be locked
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    public void Dispose()
    {
        if (!_disposed)
        {
            Stop();
            _disposed = true;
        }
    }
}

public class ClipboardChangedEventArgs : EventArgs
{
    public string Text { get; }

    public ClipboardChangedEventArgs(string text)
    {
        Text = text;
    }
}
