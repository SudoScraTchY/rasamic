using SharpHook.Data;

namespace RasaMic.Server.App.Services;

public sealed record Hotkey(bool Ctrl, bool Alt, bool Shift, KeyCode Key)
{
    // KeyCode values are scancodes, not contiguous letters/digits.
    private static readonly (KeyCode Key, string Name)[] Keys =
    [
        (KeyCode.VcA, "A"), (KeyCode.VcB, "B"), (KeyCode.VcC, "C"), (KeyCode.VcD, "D"),
        (KeyCode.VcE, "E"), (KeyCode.VcF, "F"), (KeyCode.VcG, "G"), (KeyCode.VcH, "H"),
        (KeyCode.VcI, "I"), (KeyCode.VcJ, "J"), (KeyCode.VcK, "K"), (KeyCode.VcL, "L"),
        (KeyCode.VcM, "M"), (KeyCode.VcN, "N"), (KeyCode.VcO, "O"), (KeyCode.VcP, "P"),
        (KeyCode.VcQ, "Q"), (KeyCode.VcR, "R"), (KeyCode.VcS, "S"), (KeyCode.VcT, "T"),
        (KeyCode.VcU, "U"), (KeyCode.VcV, "V"), (KeyCode.VcW, "W"), (KeyCode.VcX, "X"),
        (KeyCode.VcY, "Y"), (KeyCode.VcZ, "Z"),
        (KeyCode.Vc0, "0"), (KeyCode.Vc1, "1"), (KeyCode.Vc2, "2"), (KeyCode.Vc3, "3"),
        (KeyCode.Vc4, "4"), (KeyCode.Vc5, "5"), (KeyCode.Vc6, "6"), (KeyCode.Vc7, "7"),
        (KeyCode.Vc8, "8"), (KeyCode.Vc9, "9"),
        (KeyCode.VcF1, "F1"), (KeyCode.VcF2, "F2"), (KeyCode.VcF3, "F3"), (KeyCode.VcF4, "F4"),
        (KeyCode.VcF5, "F5"), (KeyCode.VcF6, "F6"), (KeyCode.VcF7, "F7"), (KeyCode.VcF8, "F8"),
        (KeyCode.VcF9, "F9"), (KeyCode.VcF10, "F10"), (KeyCode.VcF11, "F11"), (KeyCode.VcF12, "F12"),
    ];

    public static Hotkey Default => new(true, true, false, KeyCode.VcM);

    public override string ToString()
    {
        string name = Keys.FirstOrDefault(k => k.Key == Key).Name ?? Key.ToString();
        return (Ctrl ? "Ctrl+" : "") + (Alt ? "Alt+" : "") + (Shift ? "Shift+" : "") + name;
    }

    public static bool TryParse(string text, out Hotkey hotkey)
    {
        hotkey = Default;
        bool ctrl = false, alt = false, shift = false;
        string[] parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return false;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                default: return false;
            }
        }
        if (!ctrl && !alt && !shift)
            return false;
        string keyName = parts[^1].ToUpperInvariant();
        var match = Keys.FirstOrDefault(k => k.Name == keyName);
        if (match.Name is null)
            return false;
        hotkey = new Hotkey(ctrl, alt, shift, match.Key);
        return true;
    }
}