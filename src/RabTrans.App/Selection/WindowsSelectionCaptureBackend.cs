using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using RabTrans.Core.Selection;
using Serilog;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace RabTrans.Selection;

internal sealed class WindowsSelectionCaptureBackend(IntPtr hwnd) : ISelectionCaptureBackend
{
    private ComDataObject? _restoredClipboard;
    public long TickCount => Environment.TickCount64;
    public IntPtr ForegroundWindow => GetForegroundWindow();
    public uint ClipboardSequence => GetClipboardSequenceNumber();

    public Task WaitAsync(int milliseconds) => Task.Delay(milliseconds);

    public bool AreKeysReleased(uint triggerKey) =>
        !IsKeyDown(0x10) && !IsKeyDown(0x11) && !IsKeyDown(0x12) &&
        !IsKeyDown(0x5B) && !IsKeyDown(0x5C) &&
        (triggerKey == 0 || !IsKeyDown((int)triggerKey));

    private static bool IsKeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    public bool ClipboardBelongsTo(IntPtr window)
    {
        var owner = GetClipboardOwner();
        if (owner == IntPtr.Zero)
            return false;
        GetWindowThreadProcessId(owner, out var ownerProcess);
        GetWindowThreadProcessId(window, out var targetProcess);
        var matches = ownerProcess != 0 && ownerProcess == targetProcess;
        if (!matches)
            Log.Debug("Selection clipboard belongs to a different process: owner {Owner}, target {Target}", ownerProcess, targetProcess);
        return matches;
    }

    public bool TryBackupClipboard(out object? snapshot)
    {
        snapshot = null;
        // Avoid WPF's internal clipboard-lock retry sleeps on the UI thread.
        if (!OpenClipboard(hwnd))
            return false;
        CloseClipboard();
        try
        {
            var sequence = ClipboardSequence;
            var original = Clipboard.GetDataObject();
            var copy = new DataObject();
            if (original != null)
            {
                foreach (var format in original.GetFormats(autoConvert: false))
                {
                    var data = original.GetData(format, autoConvert: false);
                    if (data == null)
                        continue;
                    data = data switch
                    {
                        MemoryStream stream => new MemoryStream(stream.ToArray()),
                        BitmapSource bitmap => bitmap.CloneCurrentValue(),
                        Array array => array.Clone(),
                        _ => data
                    };
                    copy.SetData(format, data, autoConvert: false);
                }
            }
            if (sequence != ClipboardSequence)
                return false;
            snapshot = copy;
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Selection clipboard backup unavailable");
            return false;
        }
    }

    public bool TryReadClipboard(IntPtr target, out string? text, out uint sequence)
    {
        text = null;
        sequence = 0;
        if (!OpenClipboard(hwnd))
            return false;
        try
        {
            if (!ClipboardBelongsTo(target))
                return false;
            const uint unicodeText = 13;
            if (IsClipboardFormatAvailable(unicodeText))
            {
                var handle = GetClipboardData(unicodeText);
                var pointer = handle == IntPtr.Zero ? IntPtr.Zero : GlobalLock(handle);
                if (pointer == IntPtr.Zero)
                    return false;
                try { text = Marshal.PtrToStringUni(pointer); }
                finally { GlobalUnlock(handle); }
            }
            sequence = ClipboardSequence;
            Log.Debug("Selection clipboard read: has text {HasText}, sequence {Sequence}",
                !string.IsNullOrWhiteSpace(text), sequence);
            return true;
        }
        finally { CloseClipboard(); }
    }

    public bool TryRestoreClipboard(object snapshot, uint expectedSequence)
    {
        if (ClipboardSequence != expectedSequence)
            return true;
        // OleSetClipboard keeps the materialized DataObject alive without the synchronous
        // OleFlushClipboard/retry sleeps of Clipboard.SetDataObject(..., true).
        // This is best effort: never clear the clipboard if another process holds it.
        var result = OleSetClipboard((ComDataObject)snapshot);
        if (result >= 0)
            _restoredClipboard = (ComDataObject)snapshot;
        if (result < 0)
            Log.Debug("Selection clipboard restore deferred: {HResult:X8}", result);
        return result >= 0;
    }

    public void PersistRestoredClipboard()
    {
        if (_restoredClipboard != null && OleIsCurrentClipboard(_restoredClipboard) == 0)
        {
            var result = OleFlushClipboard();
            if (result < 0)
                Log.Warning("Could not persist restored clipboard on exit: {HResult:X8}", result);
        }
        _restoredClipboard = null;
    }

    public bool SendCopy()
    {
        var sent = SendKeys(Key(0x11), Key(0x43), Key(0x43, true), Key(0x11, true));
        Log.Debug("Selection Ctrl+C dispatched: {Success}", sent);
        return sent;
    }

    public bool SendEscape()
    {
        var sent = SendKeys(Key(0x1B), Key(0x1B, true));
        Log.Debug("Selection Alt focus recovery dispatched: {Success}", sent);
        return sent;
    }

    private static INPUT Key(ushort key, bool up = false) => new()
    {
        type = 1,
        data = new INPUTUNION { keyboard = new KEYBDINPUT { wVk = key, dwFlags = up ? 2u : 0u } }
    };

    private static bool SendKeys(params INPUT[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == inputs.Length)
            return true;
        Log.Warning("Selection input failed: sent {Sent}/{Count}, Win32 error {Error}",
            sent, inputs.Length, Marshal.GetLastWin32Error());
        if (sent > 0)
        {
            // Release only keys whose down events were inserted in a partial batch.
            var releases = inputs.Take((int)sent).Where(i => i.data.keyboard.dwFlags == 0)
                .Select(i => Key(i.data.keyboard.wVk, true)).ToArray();
            SendInput((uint)releases.Length, releases, Marshal.SizeOf<INPUT>());
        }
        return false;
    }

    [DllImport("ole32.dll")] private static extern int OleSetClipboard(ComDataObject data);
    [DllImport("ole32.dll")] private static extern int OleIsCurrentClipboard(ComDataObject data);
    [DllImport("ole32.dll")] private static extern int OleFlushClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr window);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public INPUTUNION data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT keyboard;
        // Preserve the full native union size/alignment (40-byte INPUT on x64/ARM64).
        [FieldOffset(0)] public MOUSEINPUT mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public UIntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public UIntPtr dwExtraInfo;
    }
}
