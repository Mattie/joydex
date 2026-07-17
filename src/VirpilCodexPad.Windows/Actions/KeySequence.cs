namespace VirpilCodexPad.Windows.Actions;

/// <summary>A parsed Windows key chord in the order its keys should be pressed.</summary>
public sealed record KeyChord(IReadOnlyList<ushort> VirtualKeys, string NormalizedText);

/// <summary>One or more key chords dispatched in sequence.</summary>
public sealed record KeySequence(IReadOnlyList<KeyChord> Chords, string NormalizedText);

public static class KeySequenceParser
{
    public static bool TryParse(
        string? accelerator,
        bool allowBareModifiers,
        out KeySequence? sequence,
        out string? error)
    {
        sequence = null;
        error = null;
        if (string.IsNullOrWhiteSpace(accelerator))
        {
            error = "The binding is empty.";
            return false;
        }

        var stepTexts = accelerator.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var chords = new List<KeyChord>(stepTexts.Length);
        foreach (var stepText in stepTexts)
        {
            if (!TryParseChord(stepText, allowBareModifiers, out var chord, out error))
            {
                return false;
            }

            chords.Add(chord!);
        }

        sequence = new KeySequence(chords, string.Join(' ', chords.Select(chord => chord.NormalizedText)));
        return true;
    }

    private static bool TryParseChord(
        string text,
        bool allowBareModifiers,
        out KeyChord? chord,
        out string? error)
    {
        chord = null;
        error = null;
        var modifiers = new HashSet<ushort>();
        ushort? primaryKey = null;

        var tokens = text == "+"
            ? ["+"]
            : text.EndsWith("++", StringComparison.Ordinal)
                ? [.. text[..^1].Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), "+"]
                : text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var rawToken in tokens)
        {
            if (TryGetModifier(rawToken, out var modifier))
            {
                modifiers.Add(modifier);
                continue;
            }

            if (primaryKey is not null)
            {
                error = $"The chord '{text}' contains more than one non-modifier key.";
                return false;
            }

            if (!TryGetPrimaryKey(rawToken, out var key, out var requiresShift))
            {
                error = $"The key token '{rawToken}' cannot be sent through Windows SendInput.";
                return false;
            }

            primaryKey = key;
            if (requiresShift)
            {
                modifiers.Add(VirtualKey.Shift);
            }
        }

        if (primaryKey is null && (!allowBareModifiers || modifiers.Count == 0))
        {
            error = $"The chord '{text}' does not contain a key.";
            return false;
        }

        var ordered = new List<ushort>(modifiers.Count + (primaryKey is null ? 0 : 1));
        foreach (var modifier in ModifierOrder)
        {
            if (modifiers.Contains(modifier.Key))
            {
                ordered.Add(modifier.Key);
            }
        }

        if (primaryKey is not null)
        {
            ordered.Add(primaryKey.Value);
        }

        var normalized = ordered.Select(GetKeyName).ToArray();
        chord = new KeyChord(ordered, string.Join('+', normalized));
        return true;
    }

    private static bool TryGetModifier(string token, out ushort key)
    {
        key = token.ToLowerInvariant() switch
        {
            "cmdorctrl" or "control" or "ctrl" => VirtualKey.Control,
            "alt" or "option" => VirtualKey.Alt,
            "shift" => VirtualKey.Shift,
            "command" or "cmd" or "super" or "win" or "meta" => VirtualKey.LeftWindows,
            _ => 0,
        };
        return key != 0;
    }

    private static bool TryGetPrimaryKey(string token, out ushort key, out bool requiresShift)
    {
        key = 0;
        requiresShift = false;
        if (token.Length == 1)
        {
            var character = char.ToUpperInvariant(token[0]);
            if (character is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                key = character;
                return true;
            }

            if (PunctuationKeys.TryGetValue(token, out key))
            {
                return true;
            }

            if (ShiftedPunctuationKeys.TryGetValue(token, out key))
            {
                requiresShift = true;
                return true;
            }
        }

        if (string.Equals(token, "Plus", StringComparison.OrdinalIgnoreCase))
        {
            key = VirtualKey.Plus;
            requiresShift = true;
            return true;
        }

        if (token.Length is >= 2 and <= 3
            && token[0] is 'F' or 'f'
            && int.TryParse(token.AsSpan(1), out var functionNumber)
            && functionNumber is >= 1 and <= 24)
        {
            key = checked((ushort)(VirtualKey.F1 + functionNumber - 1));
            return true;
        }

        return NamedKeys.TryGetValue(token, out key);
    }

    private static string GetKeyName(ushort key)
    {
        var modifier = ModifierOrder.FirstOrDefault(pair => pair.Key == key);
        if (modifier.Key != 0)
        {
            return modifier.Name;
        }

        if (key is >= VirtualKey.F1 and <= VirtualKey.F24)
        {
            return $"F{key - VirtualKey.F1 + 1}";
        }

        if (key is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            return ((char)key).ToString();
        }

        return KeyNames.TryGetValue(key, out var name) ? name : $"VK_{key:X2}";
    }

    private static readonly (ushort Key, string Name)[] ModifierOrder =
    [
        (VirtualKey.Control, "Ctrl"),
        (VirtualKey.Alt, "Alt"),
        (VirtualKey.Shift, "Shift"),
        (VirtualKey.LeftWindows, "Win"),
    ];

    private static readonly Dictionary<string, ushort> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Backspace"] = VirtualKey.Backspace,
        ["Tab"] = VirtualKey.Tab,
        ["Enter"] = VirtualKey.Enter,
        ["Return"] = VirtualKey.Enter,
        ["CapsLock"] = VirtualKey.CapsLock,
        ["Escape"] = VirtualKey.Escape,
        ["Esc"] = VirtualKey.Escape,
        ["Space"] = VirtualKey.Space,
        ["PageUp"] = VirtualKey.PageUp,
        ["PageDown"] = VirtualKey.PageDown,
        ["End"] = VirtualKey.End,
        ["Home"] = VirtualKey.Home,
        ["Left"] = VirtualKey.Left,
        ["Up"] = VirtualKey.Up,
        ["Right"] = VirtualKey.Right,
        ["Down"] = VirtualKey.Down,
        ["Insert"] = VirtualKey.Insert,
        ["Delete"] = VirtualKey.Delete,
        ["Numpad0"] = VirtualKey.Numpad0,
        ["Numpad1"] = VirtualKey.Numpad1,
        ["Numpad2"] = VirtualKey.Numpad2,
        ["Numpad3"] = VirtualKey.Numpad3,
        ["Numpad4"] = VirtualKey.Numpad4,
        ["Numpad5"] = VirtualKey.Numpad5,
        ["Numpad6"] = VirtualKey.Numpad6,
        ["Numpad7"] = VirtualKey.Numpad7,
        ["Numpad8"] = VirtualKey.Numpad8,
        ["Numpad9"] = VirtualKey.Numpad9,
        ["NumpadMultiply"] = VirtualKey.Multiply,
        ["NumpadAdd"] = VirtualKey.Add,
        ["NumpadSubtract"] = VirtualKey.Subtract,
        ["NumpadDecimal"] = VirtualKey.Decimal,
        ["NumpadDivide"] = VirtualKey.Divide,
        ["Plus"] = VirtualKey.Plus,
        ["Minus"] = VirtualKey.Minus,
        ["LeftBracket"] = VirtualKey.LeftBracket,
        ["RightBracket"] = VirtualKey.RightBracket,
        ["Backslash"] = VirtualKey.Backslash,
        ["Semicolon"] = VirtualKey.Semicolon,
        ["Quote"] = VirtualKey.Quote,
        ["Comma"] = VirtualKey.Comma,
        ["Period"] = VirtualKey.Period,
        ["Slash"] = VirtualKey.Slash,
        ["Backquote"] = VirtualKey.Backquote,
    };

    private static readonly Dictionary<string, ushort> PunctuationKeys = new(StringComparer.Ordinal)
    {
        ["="] = VirtualKey.Plus,
        ["-"] = VirtualKey.Minus,
        ["["] = VirtualKey.LeftBracket,
        ["]"] = VirtualKey.RightBracket,
        ["\\"] = VirtualKey.Backslash,
        [";"] = VirtualKey.Semicolon,
        ["'"] = VirtualKey.Quote,
        [","] = VirtualKey.Comma,
        ["."] = VirtualKey.Period,
        ["/"] = VirtualKey.Slash,
        ["`"] = VirtualKey.Backquote,
    };

    private static readonly Dictionary<string, ushort> ShiftedPunctuationKeys = new(StringComparer.Ordinal)
    {
        ["!"] = '1',
        ["@"] = '2',
        ["#"] = '3',
        ["$"] = '4',
        ["%"] = '5',
        ["^"] = '6',
        ["&"] = '7',
        ["*"] = '8',
        ["("] = '9',
        [")"] = '0',
        ["+"] = VirtualKey.Plus,
        ["_"] = VirtualKey.Minus,
        ["{"] = VirtualKey.LeftBracket,
        ["}"] = VirtualKey.RightBracket,
        ["|"] = VirtualKey.Backslash,
        [":"] = VirtualKey.Semicolon,
        ["\""] = VirtualKey.Quote,
        ["<"] = VirtualKey.Comma,
        [">"] = VirtualKey.Period,
        ["?"] = VirtualKey.Slash,
        ["~"] = VirtualKey.Backquote,
    };

    private static readonly Dictionary<ushort, string> KeyNames = NamedKeys
        .GroupBy(pair => pair.Value)
        .ToDictionary(group => group.Key, group => group.First().Key);
}

internal static class VirtualKey
{
    public const ushort Backspace = 0x08;
    public const ushort Tab = 0x09;
    public const ushort Enter = 0x0D;
    public const ushort Shift = 0x10;
    public const ushort Control = 0x11;
    public const ushort Alt = 0x12;
    public const ushort CapsLock = 0x14;
    public const ushort Escape = 0x1B;
    public const ushort Space = 0x20;
    public const ushort PageUp = 0x21;
    public const ushort PageDown = 0x22;
    public const ushort End = 0x23;
    public const ushort Home = 0x24;
    public const ushort Left = 0x25;
    public const ushort Up = 0x26;
    public const ushort Right = 0x27;
    public const ushort Down = 0x28;
    public const ushort Insert = 0x2D;
    public const ushort Delete = 0x2E;
    public const ushort LeftWindows = 0x5B;
    public const ushort Numpad0 = 0x60;
    public const ushort Numpad1 = 0x61;
    public const ushort Numpad2 = 0x62;
    public const ushort Numpad3 = 0x63;
    public const ushort Numpad4 = 0x64;
    public const ushort Numpad5 = 0x65;
    public const ushort Numpad6 = 0x66;
    public const ushort Numpad7 = 0x67;
    public const ushort Numpad8 = 0x68;
    public const ushort Numpad9 = 0x69;
    public const ushort Multiply = 0x6A;
    public const ushort Add = 0x6B;
    public const ushort Subtract = 0x6D;
    public const ushort Decimal = 0x6E;
    public const ushort Divide = 0x6F;
    public const ushort F1 = 0x70;
    public const ushort F24 = 0x87;
    public const ushort Semicolon = 0xBA;
    public const ushort Plus = 0xBB;
    public const ushort Comma = 0xBC;
    public const ushort Minus = 0xBD;
    public const ushort Period = 0xBE;
    public const ushort Slash = 0xBF;
    public const ushort Backquote = 0xC0;
    public const ushort LeftBracket = 0xDB;
    public const ushort Backslash = 0xDC;
    public const ushort RightBracket = 0xDD;
    public const ushort Quote = 0xDE;
}
