using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace WindowsSpaces.Platform.Win32;

/// <summary>A downscaled copy of a window's contents, as top-down 32-bit BGRA.</summary>
public sealed record WindowPicture(int Width, int Height, byte[] Bgra, DateTime CapturedUtc);

/// <summary>
/// Keeps the last picture of each window. A window in another space is SW_HIDE'd,
/// so DWM has nothing to mirror for it and the overview would have no preview;
/// the picture taken while it was still on screen stands in for it.
/// </summary>
public sealed class WindowSnapshotCache
{
    /// <summary>Longest edge of a stored snapshot. Tiles are ~92px wide; this leaves room for high DPI.</summary>
    private const int MaxEdge = 320;

    private const uint PW_RENDERFULLCONTENT = 0x2;
    private const int SRCCOPY = 0x00CC0020;
    private const int HALFTONE = 4;
    private const int BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    private readonly ConcurrentDictionary<nint, WindowPicture> _snapshots = new();

    public WindowPicture? TryGet(nint hwnd) => _snapshots.TryGetValue(hwnd, out var snapshot) ? snapshot : null;

    public void Remove(nint hwnd) => _snapshots.TryRemove(hwnd, out _);

    /// <summary>Captures only if there is no picture yet — the cheap path for the switch hot path.</summary>
    public void CaptureIfMissing(nint hwnd)
    {
        if (!_snapshots.ContainsKey(hwnd)) Capture(hwnd);
    }

    /// <summary>
    /// Captures the window now. Never throws: a preview is decoration, and a
    /// window that can't be captured (minimized, hung, DRM) just keeps its old
    /// picture or none.
    /// </summary>
    public void Capture(nint hwnd)
    {
        nint screenDc = 0, fullDc = 0, fullBitmap = 0, fullOld = 0;
        nint smallDc = 0, smallBitmap = 0, smallOld = 0;

        try
        {
            if (!NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return;
            if (IsHungAppWindow(hwnd)) return;
            if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return;

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width < 32 || height < 32) return;

            screenDc = GetDC(0);
            fullDc = CreateCompatibleDC(screenDc);
            fullBitmap = CreateCompatibleBitmap(screenDc, width, height);
            if (fullDc == 0 || fullBitmap == 0) return;
            fullOld = SelectObject(fullDc, fullBitmap);

            if (!PrintWindow(hwnd, fullDc, PW_RENDERFULLCONTENT)) return;

            var scale = Math.Min(1.0, MaxEdge / (double)Math.Max(width, height));
            var targetWidth = Math.Max(1, (int)Math.Round(width * scale));
            var targetHeight = Math.Max(1, (int)Math.Round(height * scale));

            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = targetWidth,
                    biHeight = -targetHeight, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB
                }
            };

            smallDc = CreateCompatibleDC(screenDc);
            smallBitmap = CreateDIBSection(smallDc, ref info, DIB_RGB_COLORS, out var bits, 0, 0);
            if (smallDc == 0 || smallBitmap == 0 || bits == 0) return;
            smallOld = SelectObject(smallDc, smallBitmap);

            SetStretchBltMode(smallDc, HALFTONE);
            if (!StretchBlt(smallDc, 0, 0, targetWidth, targetHeight, fullDc, 0, 0, width, height, SRCCOPY)) return;

            var pixels = new byte[targetWidth * targetHeight * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);

            // GDI leaves alpha undefined; the preview is opaque.
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 0xFF;

            _snapshots[hwnd] = new WindowPicture(targetWidth, targetHeight, pixels, DateTime.UtcNow);
        }
        catch (Exception)
        {
            // Decoration only.
        }
        finally
        {
            if (smallDc != 0 && smallOld != 0) SelectObject(smallDc, smallOld);
            if (smallBitmap != 0) DeleteObject(smallBitmap);
            if (smallDc != 0) DeleteDC(smallDc);
            if (fullDc != 0 && fullOld != 0) SelectObject(fullDc, fullOld);
            if (fullBitmap != 0) DeleteObject(fullBitmap);
            if (fullDc != 0) DeleteDC(fullDc);
            if (screenDc != 0) ReleaseDC(0, screenDc);
        }
    }

    /// <summary>
    /// Captures the window and reports whether it has actually painted: a
    /// window just returned from SW_HIDE renders as one flat colour (usually
    /// black) until its app repaints it. Used to decide when it is safe to
    /// start mirroring the window.
    /// </summary>
    public bool CaptureAndCheckPainted(nint hwnd)
    {
        var started = DateTime.UtcNow;
        Capture(hwnd);

        var picture = TryGet(hwnd);
        if (picture is null || picture.CapturedUtc < started) return false;

        // Sample a grid of pixels; a painted window has at least some that
        // differ noticeably from the first one.
        var pixels = picture.Bgra;
        var count = pixels.Length / 4;
        var step = Math.Max(1, count / 400);
        var differing = 0;
        for (var i = step; i < count; i += step)
        {
            var o = i * 4;
            var delta = Math.Abs(pixels[o] - pixels[0]) + Math.Abs(pixels[o + 1] - pixels[1]) + Math.Abs(pixels[o + 2] - pixels[2]);
            if (delta > 24) differing++;
        }

        if (differing < 4) return false;

        // Not flat is not enough: an app repaints in pieces (WhatsApp draws its
        // chat pane well before its sidebar and title bar), and a half-drawn
        // window mirrors as a half-drawn window. Painted means the picture has
        // stopped changing between two consecutive looks.
        var hash = new HashCode();
        hash.AddBytes(pixels);
        var current = hash.ToHashCode();

        var now = Environment.TickCount64;
        var stable = _lastLook.TryGetValue(hwnd, out var previous) &&
                     previous.Hash == current &&
                     now - previous.At < 400;
        _lastLook[hwnd] = (current, now);
        return stable;
    }

    private readonly ConcurrentDictionary<nint, (int Hash, long At)> _lastLook = new();

    /// <summary>Drops pictures of windows that no longer exist.</summary>
    public void Prune()
    {
        foreach (var hwnd in _snapshots.Keys)
        {
            if (!NativeMethods.IsWindow(hwnd)) _snapshots.TryRemove(hwnd, out _);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
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
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public int bmiColors;
    }

    [DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hWnd, nint hDc);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint hWnd, nint hdcBlt, uint nFlags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint hdc, int cx, int cy);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFO pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hdc, nint h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint ho);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(nint hdc, int mode);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(nint hdcDest, int xDest, int yDest, int wDest, int hDest, nint hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, int rop);
}
