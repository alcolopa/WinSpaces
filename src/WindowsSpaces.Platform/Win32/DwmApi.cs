using System;
using System.Runtime.InteropServices;

namespace WindowsSpaces.Platform.Win32;

public static class DwmApi
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public RECT(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        public bool fVisible;
        public bool fSourceClientAreaOnly;
    }

    public const uint DWM_TNP_RECTDESTINATION = 0x00000001;
    public const uint DWM_TNP_RECTSOURCE = 0x00000002;
    public const uint DWM_TNP_OPACITY = 0x00000004;
    public const uint DWM_TNP_VISIBLE = 0x00000008;
    public const uint DWM_TNP_SOURCECLIENTAREAONLY = 0x00000010;

    /// <summary>DWMWA_TRANSITIONS_FORCEDISABLED — suppresses DWM's own show/hide and min/max animations for one window.</summary>
    public const uint DWMWA_TRANSITIONS_FORCEDISABLED = 3;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmSetWindowAttribute(nint hwnd, uint dwAttribute, ref int pvAttribute, int cbAttribute);

    /// <summary>
    /// Turns DWM's fade animation for a window on or off.
    ///
    /// Hiding a window normally fades it out over roughly 200ms, and DWM keeps
    /// painting it for the whole fade — long after <c>ShowWindow</c> has
    /// returned and the workspace switch is, as far as this app is concerned,
    /// finished. That fade is what makes the outgoing space flash back into
    /// view once the transition overlay comes down. Disabling transitions
    /// before hiding makes the window disappear on the frame it is hidden.
    /// </summary>
    public static void SetTransitionsDisabled(nint hwnd, bool disabled)
    {
        var value = disabled ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmRegisterThumbnail(nint hwndDestination, nint hwndSource, out nint phThumbnailId);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmUnregisterThumbnail(nint hThumbnailId);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmUpdateThumbnailProperties(nint hThumbnailId, ref DWM_THUMBNAIL_PROPERTIES ptnProperties);
}
