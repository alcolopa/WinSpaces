namespace WindowsSpaces.Core;

[Flags]
public enum ModifierKeys
{
    None = 0x0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8
}

public interface IHotkeyManager
{
    void Register(int id, ModifierKeys modifiers, int virtualKey, Action callback);
    void Unregister(int id);
}
