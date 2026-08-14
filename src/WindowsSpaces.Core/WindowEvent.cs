namespace WindowsSpaces.Core;

public enum WindowEventKind
{
    Created,
    Destroyed,
    Shown,
    Hidden,
    LocationChanged,
    ForegroundChanged
}

public readonly record struct WindowEvent(WindowEventKind Kind, nint Hwnd, DateTimeOffset Timestamp);
