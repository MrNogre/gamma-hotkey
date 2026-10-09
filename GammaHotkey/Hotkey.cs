using System.Globalization;
using System.Windows.Input;

namespace GammaHotkey;

// Format: "Ctrl+Alt+Shift+Key", modifiers optional but in this order; Key is A-Z, 0-9 or F1-F12.
// Letters and digits need Ctrl or Alt so normal typing is never blocked.
internal static class Hotkey
{
    private static readonly (string Name, uint Flag)[] Modifiers = [("Ctrl", 0x2), ("Alt", 0x1), ("Shift", 0x4)];

    public static bool TryParse(string? text, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrEmpty(text)) return false;
        string[] parts = text.Split('+');
        int next = 0;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            int index = Array.FindIndex(Modifiers, next, m => m.Name == parts[i]);
            if (index < 0) return false;
            modifiers |= Modifiers[index].Flag;
            next = index + 1;
        }
        string key = parts[^1];
        if (key is [>= 'A' and <= 'Z' or >= '0' and <= '9'])
        {
            if ((modifiers & 0x3) == 0) return false;
            virtualKey = key[0];
            return true;
        }
        if (key.Length >= 2 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out int number) &&
            number is >= 1 and <= 12 && key == $"F{number}")
        {
            virtualKey = (uint)(0x70 + number - 1);
            return true;
        }
        return false;
    }

    public static string Prefix(ModifierKeys modifiers) =>
        (modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") + (modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") +
        (modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "");

    // Captured key press to shortcut text; on rejection returns null and the message to show.
    public static string? FromKeyPress(Key key, ModifierKeys modifiers, out string? error)
    {
        error = null;
        if (key is >= Key.NumPad0 and <= Key.NumPad9) { error = "Use the top-row digits, not the numpad."; return null; }
        string? value = key switch
        {
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(CultureInfo.InvariantCulture),
            >= Key.F1 and <= Key.F12 => $"F{key - Key.F1 + 1}",
            _ => null
        };
        if (value is null) { error = "Use a letter, digit, or F1–F12, with optional Ctrl, Alt or Shift."; return null; }
        string text = Prefix(modifiers) + value;
        if (!TryParse(text, out _, out _)) { error = "Letters and digits need Ctrl or Alt."; return null; }
        return text;
    }
}
