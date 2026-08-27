using System.Drawing;

namespace WindowsSpaces.Core;

/// <summary>
/// Pure geometry for relocating a window between monitors. Kept out of
/// <see cref="WorkspaceManager"/> so it can be reasoned about (and tested)
/// without any window state at all.
/// </summary>
public static class MonitorGeometry
{
    /// <summary>
    /// Maps a window rectangle from one monitor to another, keeping its
    /// relative position and its relative size. A window filling the left
    /// half of a 1920x1080 monitor fills the left half of a 2560x1440 one.
    ///
    /// The result is always clamped inside the destination monitor: a window
    /// the user cannot reach is the one outcome worse than a window that
    /// changed size, and mixed-resolution setups otherwise push windows off
    /// the bottom-right edge.
    /// </summary>
    public static Rectangle MapBetweenMonitors(Rectangle bounds, Rectangle from, Rectangle to)
    {
        if (from.Width <= 0 || from.Height <= 0 || to.Width <= 0 || to.Height <= 0) return bounds;
        if (from == to) return bounds;

        var scaleX = (double)to.Width / from.Width;
        var scaleY = (double)to.Height / from.Height;

        var width = (int)Math.Round(bounds.Width * scaleX);
        var height = (int)Math.Round(bounds.Height * scaleY);

        // A window bigger than the destination would otherwise be clamped to a
        // negative position; cap it at the monitor first.
        width = Math.Clamp(width, 1, to.Width);
        height = Math.Clamp(height, 1, to.Height);

        var x = to.X + (int)Math.Round((bounds.X - from.X) * scaleX);
        var y = to.Y + (int)Math.Round((bounds.Y - from.Y) * scaleY);

        x = Math.Clamp(x, to.X, to.Right - width);
        y = Math.Clamp(y, to.Y, to.Bottom - height);

        return new Rectangle(x, y, width, height);
    }
}
