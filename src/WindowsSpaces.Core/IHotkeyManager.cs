namespace WindowsSpaces.Core;

public enum ModifierKeys
{
    Control = 0x2,
    Alt = 0x1,
    Shift = 0x4
}

public interface IHotkeyManager
{
    void Register(int id, ModifierKeys modifiers, int virtualKey, Action callback);
    void Unregister(int id);
}
