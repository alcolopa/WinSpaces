using System.Drawing;

namespace WindowsSpaces.Core;

/// <summary>
/// Stable monitor identity. Id must be derived from device path/EDID data,
/// never from enumeration order, since order is not stable across reboots or reconnects.
/// </summary>
public sealed record Monitor(
    string Id,
    string DevicePath,
    Rectangle Bounds,
    bool IsPrimary);
