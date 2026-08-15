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

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmRegisterThumbnail(nint hwndDestination, nint hwndSource, out nint phThumbnailId);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmUnregisterThumbnail(nint hThumbnailId);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmUpdateThumbnailProperties(nint hThumbnailId, ref DWM_THUMBNAIL_PROPERTIES ptnProperties);
}
