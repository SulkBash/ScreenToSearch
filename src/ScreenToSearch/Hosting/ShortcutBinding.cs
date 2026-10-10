namespace screen_to_search;

[Flags]
internal enum ShortcutModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Windows = 8
}

internal readonly record struct ShortcutBinding(uint Key, ShortcutModifiers Modifiers)
{
    public static ShortcutBinding Disabled => new(0, ShortcutModifiers.None);
    public bool IsEnabled => Key != 0;

    public bool IsValid => Key <= 0xFE &&
        (Modifiers & ~(ShortcutModifiers.Control | ShortcutModifiers.Alt |
                       ShortcutModifiers.Shift | ShortcutModifiers.Windows)) == 0 &&
        Key is not (0x10 or 0x11 or 0x12 or 0x5B or 0x5C or
                    0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5);

    public string DisplayName
    {
        get
        {
            if (!IsEnabled) return "No shortcut set";
            var parts = new List<string>(5);
            if (Modifiers.HasFlag(ShortcutModifiers.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(ShortcutModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(ShortcutModifiers.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(ShortcutModifiers.Windows)) parts.Add("Win");
            parts.Add(Key switch
            {
                0x14 => "Caps Lock",
                0x2D => "Insert",
                0x91 => "Scroll Lock",
                >= 0x70 and <= 0x87 => $"F{Key - 0x6F}",
                >= 0x30 and <= 0x39 => ((char)Key).ToString(),
                >= 0x41 and <= 0x5A => ((char)Key).ToString(),
                _ => System.Windows.Input.KeyInterop.KeyFromVirtualKey((int)Key).ToString()
            });
            return string.Join(" + ", parts);
        }
    }
}
