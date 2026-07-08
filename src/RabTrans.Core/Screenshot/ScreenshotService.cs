using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace RabTrans.Core.Screenshot;

/// <summary>
/// Screenshot service using Windows Win32 APIs.
/// </summary>
public class ScreenshotService : IDisposable
{
    private bool _disposed = false;

    public ScreenshotService()
    {
        // No initialization needed - using Win32 APIs for capture
    }

    /// <summary>
    /// Captures the entire screen.
    /// </summary>
    public async Task<Stream?> CaptureScreenAsync()
    {
        try
        {
            // Get screen dimensions using Win32 API
            int screenWidth = Win32.GetSystemMetrics(Win32.SM_CXSCREEN);
            int screenHeight = Win32.GetSystemMetrics(Win32.SM_CYSCREEN);

            // Use Win32 API for full screen capture
            return await CaptureScreenWin32Async(0, 0, screenWidth, screenHeight);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to capture screen: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Captures a region of the screen.
    /// </summary>
    public async Task<Stream?> CaptureRegionAsync(int x, int y, int width, int height)
    {
        try
        {
            return await CaptureScreenWin32Async(x, y, width, height);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to capture region: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Captures a specific window.
    /// </summary>
    public async Task<Stream?> CaptureWindowAsync(IntPtr hwnd)
    {
        try
        {
            if (!Win32.GetWindowRect(hwnd, out var rect))
            {
                return null;
            }

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;

            return await CaptureScreenWin32Async(rect.Left, rect.Top, width, height);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to capture window: {ex.Message}");
            return null;
        }
    }

    private Task<Stream?> CaptureScreenWin32Async(int x, int y, int width, int height)
    {
        return Task.Run<Stream?>(() =>
        {
            if (width <= 0 || height <= 0)
                return null;

            try
            {
                using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
                using var graphics = Graphics.FromImage(bitmap);
                graphics.CopyFromScreen(
                    x,
                    y,
                    0,
                    0,
                    new Size(width, height),
                    CopyPixelOperation.SourceCopy);

                var stream = new MemoryStream();
                bitmap.Save(stream, ImageFormat.Png);
                stream.Position = 0;
                return stream;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to capture screen with GDI+: {ex.Message}");
                return null;
            }
        });
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}

// Win32 interop
internal static class Win32
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest, IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    [DllImport("gdi32.dll")]
    public static extern int GetObject(IntPtr hObject, int nCount, ref DIBSECTION lpObject);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    public const int SRCCOPY = 0x00CC0020;
    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DIBSECTION
    {
        public BITMAPINFOHEADER dsBmih;
        public int dsType;
        public int dsWidth;
        public int dsHeight;
        public int dsWidthBytes;
        public short dsPlanes;
        public short dsBitsPixel;
        public BITMAP dsBm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public short bmPlanes;
        public short bmBitsPixel;
        public IntPtr bmBits;
    }
}
