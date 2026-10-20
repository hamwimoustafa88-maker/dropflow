namespace DropFlow.Core.App;

/// <summary>A global shortcut in the form Windows' RegisterHotKey wants.</summary>
public readonly record struct Hotkey(uint Modifiers, uint VirtualKey)
{
    public const uint Alt = 0x1, Ctrl = 0x2, Shift = 0x4, Win = 0x8;
}

public static class HotkeyParser
{
    /// <summary>Parses "Ctrl+Alt+D", "shift + F9", "Win+Space"… Returns null for anything that is not a usable global shortcut
    /// (no modifier, no key, unknown key, or a repeated modifier).</summary>
    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        uint mods = 0;
        uint? vk = null;

        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var flag = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => Hotkey.Ctrl,
                "alt" => Hotkey.Alt,
                "shift" => Hotkey.Shift,
                "win" or "windows" or "meta" => Hotkey.Win,
                _ => 0u,
            };
            if (flag != 0)
            {
                if ((mods & flag) != 0) return null;       // "Ctrl+Ctrl+D"
                mods |= flag;
                continue;
            }
            if (vk is not null) return null;               // two non-modifier keys
            vk = KeyCode(part);
            if (vk is null) return null;
        }
        return mods == 0 || vk is null ? null : new Hotkey(mods, vk.Value);
    }

    private static uint? KeyCode(string name)
    {
        var n = name.ToUpperInvariant();
        if (n.Length == 1 && n[0] is >= 'A' and <= 'Z') return n[0];                  // VK_A.. = ASCII
        if (n.Length == 1 && n[0] is >= '0' and <= '9') return n[0];
        if (n[0] == 'F' && int.TryParse(n.AsSpan(1), out var f) && f is >= 1 and <= 24) return (uint)(0x70 + f - 1);
        return n switch
        {
            "SPACE" => 0x20, "ENTER" => 0x0D, "TAB" => 0x09, "ESC" or "ESCAPE" => 0x1B,
            "LEFT" => 0x25, "UP" => 0x26, "RIGHT" => 0x27, "DOWN" => 0x28,
            "INSERT" => 0x2D, "DELETE" => 0x2E, "HOME" => 0x24, "END" => 0x23,
            "PAGEUP" => 0x21, "PAGEDOWN" => 0x22,
            _ => null,
        };
    }

    /// <summary>Canonical display text ("Ctrl+Alt+D") for a parsed hotkey.</summary>
    public static string Format(Hotkey h)
    {
        var parts = new List<string>();
        if ((h.Modifiers & Hotkey.Ctrl) != 0) parts.Add("Ctrl");
        if ((h.Modifiers & Hotkey.Alt) != 0) parts.Add("Alt");
        if ((h.Modifiers & Hotkey.Shift) != 0) parts.Add("Shift");
        if ((h.Modifiers & Hotkey.Win) != 0) parts.Add("Win");
        parts.Add(h.VirtualKey switch
        {
            >= 0x70 and <= 0x87 => "F" + (h.VirtualKey - 0x70 + 1),
            0x20 => "Space",
            0x0D => "Enter",
            0x09 => "Tab",
            0x1B => "Esc",
            _ => ((char)h.VirtualKey).ToString(),
        });
        return string.Join('+', parts);
    }
}
