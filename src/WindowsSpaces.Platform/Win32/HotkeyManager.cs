using WindowsSpaces.Core;
using static WindowsSpaces.Platform.Win32.NativeMethods;

namespace WindowsSpaces.Platform.Win32;

/// <summary>
/// Wraps RegisterHotKey/WM_HOTKEY. Must be constructed with the hwnd of a
/// window pumping messages on the thread that calls Register — WM_HOTKEY
/// is delivered to that window's message queue. WindowsSpaces.App feeds
/// WM_HOTKEY messages from its loop into HandleMessage.
/// </summary>
public sealed class HotkeyManager : IHotkeyManager, IDisposable
{
    private readonly nint _hwnd;
    private readonly Dictionary<int, Action> _callbacks = new();

    public HotkeyManager(nint hwnd)
    {
        _hwnd = hwnd;
    }

    public void Register(int id, ModifierKeys modifiers, int virtualKey, Action callback)
    {
        if (!RegisterHotKey(_hwnd, id, (uint)modifiers, (uint)virtualKey))
        {
            throw new InvalidOperationException($"RegisterHotKey failed for id {id}, Win32 error {GetLastError()}");
        }
        _callbacks[id] = callback;
    }

    public void Unregister(int id)
    {
        UnregisterHotKey(_hwnd, id);
        _callbacks.Remove(id);
    }

    /// <summary>
    /// Call from the App's message loop for every received message.
    /// Invokes the registered callback when the message is WM_HOTKEY.
    /// </summary>
    public void HandleMessage(uint message, nint wParam)
    {
        if (message != WM_HOTKEY) return;

        var id = (int)wParam;
        if (_callbacks.TryGetValue(id, out var callback))
        {
            callback();
        }
    }

    public void Dispose()
    {
        foreach (var id in _callbacks.Keys.ToList())
        {
            UnregisterHotKey(_hwnd, id);
        }
        _callbacks.Clear();
    }
}
