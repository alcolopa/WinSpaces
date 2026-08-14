using System.Collections.Concurrent;

namespace WindowsSpaces.Core;

/// <summary>
/// Marks hwnds as "we just moved/hid/showed this ourselves" so the event
/// pipeline can distinguish our own SetWindowPos-driven notifications from
/// independent user actions and avoid feedback loops.
/// </summary>
public sealed class OperationGuard
{
    private readonly ConcurrentDictionary<nint, byte> _suppressed = new();

    public bool IsSuppressed(nint hwnd) => _suppressed.ContainsKey(hwnd);

    public IDisposable Suppress(nint hwnd)
    {
        _suppressed[hwnd] = 0;
        return new Scope(this, hwnd);
    }

    private sealed class Scope : IDisposable
    {
        private readonly OperationGuard _owner;
        private readonly nint _hwnd;
        public Scope(OperationGuard owner, nint hwnd) { _owner = owner; _hwnd = hwnd; }
        public void Dispose() => _owner._suppressed.TryRemove(_hwnd, out _);
    }
}
