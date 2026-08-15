using Microsoft.UI.Xaml;

namespace WindowsSpaces.App;

/// <summary>
/// WinUI3 application object. The app's real lifetime is still driven by the
/// raw Win32 message-only window and <c>GetMessage</c> pump in
/// <c>Program</c> (hotkeys and the tray icon depend on it); this type exists
/// so that <see cref="Application.Start"/> initializes the XAML framework on
/// that same STA thread, letting Tasks 8-10 create real
/// <see cref="Microsoft.UI.Xaml.Window"/> instances on demand.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// The single <see cref="AppHost"/> owned by <c>Program</c>'s message
    /// loop. Assigned once the host has been constructed and started so that
    /// windows opened later can reach configuration/diagnostics without
    /// building a second host.
    /// </summary>
    public AppHost? Host { get; set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Intentionally does not create or start an AppHost: Program owns the
        // single host instance, constructing and starting it inside the same
        // message loop that delivers WM_HOTKEY/WM_APP. This override exists
        // only as the WinUI window-lifetime hook for Tasks 8-10.
    }
}
