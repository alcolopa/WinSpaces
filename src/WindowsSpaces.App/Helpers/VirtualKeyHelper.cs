using WindowsSpaces.Core;

namespace WindowsSpaces.App.Helpers;

public sealed record KeyOption(int VirtualKey, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed class VirtualKeyHelperProvider
{
    public IReadOnlyList<KeyOption> AvailableKeys => VirtualKeyHelper.AvailableKeys;
}

public static class VirtualKeyHelper
{
    private static readonly Dictionary<int, string> KeyNames = new();
    public static IReadOnlyList<KeyOption> AvailableKeys { get; }

    static VirtualKeyHelper()
    {
        var list = new List<KeyOption>();

        // Numbers 1-9, 0
        for (int k = 0x31; k <= 0x39; k++) list.Add(new(k, ((char)k).ToString()));
        list.Add(new(0x30, "0"));

        // Letters A-Z
        for (int k = 0x41; k <= 0x5A; k++) list.Add(new(k, ((char)k).ToString()));

        // Function keys F1-F12
        for (int i = 1; i <= 12; i++) list.Add(new(0x6F + i, $"F{i}"));

        // Arrows & Navigation
        list.Add(new(0x25, "Left Arrow"));
        list.Add(new(0x26, "Up Arrow"));
        list.Add(new(0x27, "Right Arrow"));
        list.Add(new(0x28, "Down Arrow"));
        list.Add(new(0x24, "Home"));
        list.Add(new(0x23, "End"));
        list.Add(new(0x21, "Page Up"));
        list.Add(new(0x22, "Page Down"));
        list.Add(new(0x2D, "Insert"));
        list.Add(new(0x2E, "Delete"));

        // Common keys
        list.Add(new(0x20, "Space"));
        list.Add(new(0x09, "Tab"));
        list.Add(new(0x0D, "Enter"));
        list.Add(new(0x1B, "Escape"));
        list.Add(new(0x08, "Backspace"));
        list.Add(new(0xC0, "` (Tilde)"));
        list.Add(new(0xBD, "- (Minus)"));
        list.Add(new(0xBB, "= (Equals)"));
        list.Add(new(0xDB, "["));
        list.Add(new(0xDD, "]"));
        list.Add(new(0xBA, ";"));
        list.Add(new(0xDE, "'"));
        list.Add(new(0xBC, ","));
        list.Add(new(0xBE, "."));
        list.Add(new(0xBF, "/"));
        list.Add(new(0xDC, "\\"));

        // Numpad
        for (int i = 0; i <= 9; i++) list.Add(new(0x60 + i, $"Num {i}"));
        list.Add(new(0x6B, "Num +"));
        list.Add(new(0x6D, "Num -"));
        list.Add(new(0x6A, "Num *"));
        list.Add(new(0x6F, "Num /"));

        AvailableKeys = list;
        foreach (var item in list)
        {
            KeyNames[item.VirtualKey] = item.DisplayName;
        }
    }

    public static string GetKeyName(int virtualKey)
    {
        if (KeyNames.TryGetValue(virtualKey, out var name)) return name;
        if (virtualKey >= 0x41 && virtualKey <= 0x5A) return ((char)virtualKey).ToString();
        if (virtualKey >= 0x30 && virtualKey <= 0x39) return ((char)virtualKey).ToString();
        return $"0x{virtualKey:X2}";
    }

    public static List<string> GetKeyPills(ModifierKeys modifiers, int virtualKey)
    {
        var pills = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Win)) pills.Add("Win");
        if (modifiers.HasFlag(ModifierKeys.Control)) pills.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) pills.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) pills.Add("Shift");
        pills.Add(GetKeyName(virtualKey));
        return pills;
    }

    public static string FormatHotkey(ModifierKeys modifiers, int virtualKey)
    {
        var pills = GetKeyPills(modifiers, virtualKey);
        return string.Join(" + ", pills);
    }
}
