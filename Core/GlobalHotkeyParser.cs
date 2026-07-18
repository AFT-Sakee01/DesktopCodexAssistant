using System;
using System.Collections.Generic;
using System.Windows.Forms;

internal sealed class GlobalHotkeyBinding
{
    public string NormalizedText { get; set; }
    public uint Modifiers { get; set; }
    public uint VirtualKey { get; set; }
}

internal static class GlobalHotkeyParser
{
    internal const uint ModAlt = 0x0001;
    internal const uint ModControl = 0x0002;
    internal const uint ModShift = 0x0004;
    internal const uint ModWin = 0x0008;
    internal const uint ModNoRepeat = 0x4000;

    public static string Normalize(string text)
    {
        GlobalHotkeyBinding binding;
        return TryParse(text, out binding) ? binding.NormalizedText : string.Empty;
    }

    public static bool TryParse(string text, out GlobalHotkeyBinding binding)
    {
        binding = null;
        string raw = (text ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return false;
        }

        string[] parts = raw.Split(new[] { '+' }, StringSplitOptions.None);
        uint modifiers = 0;
        Keys key = Keys.None;
        for (int i = 0; i < parts.Length; i++)
        {
            string token = parts[i].Trim();
            if (token.Length == 0)
            {
                return false;
            }

            uint modifier = ParseModifier(token);
            if (modifier != 0)
            {
                if ((modifiers & modifier) != 0)
                {
                    return false;
                }

                modifiers |= modifier;
                continue;
            }

            Keys parsedKey;
            if (key != Keys.None || !TryParseKey(token, out parsedKey))
            {
                return false;
            }

            key = parsedKey;
        }

        // A modifier is required so a global binding cannot steal ordinary typing from every app.
        if (modifiers == 0 || key == Keys.None)
        {
            return false;
        }

        binding = new GlobalHotkeyBinding
        {
            NormalizedText = FormatNormalized(modifiers, key),
            Modifiers = modifiers | ModNoRepeat,
            VirtualKey = (uint)(key & Keys.KeyCode)
        };
        return true;
    }

    internal static void RunSelfTest()
    {
        GlobalHotkeyBinding binding;
        Assert(TryParse(" alt + ctrl + h ", out binding), "valid Ctrl+Alt letter");
        Assert(binding.NormalizedText == "Ctrl+Alt+H", "modifier normalization order");
        Assert(binding.Modifiers == (ModControl | ModAlt | ModNoRepeat), "modifier flags + no-repeat");
        Assert(binding.VirtualKey == (uint)Keys.H, "letter virtual key");
        Assert(Normalize("Win+Shift+F12") == "Shift+Win+F12", "function-key normalization");
        Assert(Normalize("Ctrl+1") == "Ctrl+1", "digit normalization");
        Assert(Normalize("H").Length == 0, "bare key rejected");
        Assert(Normalize("Ctrl+Alt").Length == 0, "modifier-only rejected");
        Assert(Normalize("Ctrl+Ctrl+H").Length == 0, "duplicate modifier rejected");
        Assert(Normalize("Ctrl++H").Length == 0, "empty token rejected");
        Assert(Normalize("Ctrl+Alt+H+J").Length == 0, "multiple keys rejected");
        Assert(Normalize("Ctrl+NoSuchKey").Length == 0, "unknown key rejected");
        Console.WriteLine("Global hotkey parser: PASS normalize invalid->empty no-repeat");
    }

    private static uint ParseModifier(string token)
    {
        if (string.Equals(token, "Ctrl", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(token, "Control", StringComparison.OrdinalIgnoreCase))
        {
            return ModControl;
        }

        if (string.Equals(token, "Alt", StringComparison.OrdinalIgnoreCase))
        {
            return ModAlt;
        }

        if (string.Equals(token, "Shift", StringComparison.OrdinalIgnoreCase))
        {
            return ModShift;
        }

        return string.Equals(token, "Win", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(token, "Windows", StringComparison.OrdinalIgnoreCase)
            ? ModWin
            : 0;
    }

    private static bool TryParseKey(string token, out Keys key)
    {
        key = Keys.None;
        string value = (token ?? string.Empty).Trim();
        if (value.Length == 1)
        {
            char c = char.ToUpperInvariant(value[0]);
            if (c >= 'A' && c <= 'Z')
            {
                key = (Keys)((int)Keys.A + c - 'A');
                return true;
            }

            if (c >= '0' && c <= '9')
            {
                key = (Keys)((int)Keys.D0 + c - '0');
                return true;
            }
        }

        Dictionary<string, Keys> aliases = new Dictionary<string, Keys>(StringComparer.OrdinalIgnoreCase)
        {
            { "Esc", Keys.Escape },
            { "Del", Keys.Delete },
            { "Ins", Keys.Insert },
            { "PgUp", Keys.PageUp },
            { "PgDn", Keys.PageDown },
            { "Return", Keys.Enter }
        };
        if (aliases.TryGetValue(value, out key))
        {
            return true;
        }

        try
        {
            key = (Keys)Enum.Parse(typeof(Keys), value, true);
            key &= Keys.KeyCode;
            int virtualKey = (int)key;
            return key != Keys.None && virtualKey > 0 && virtualKey <= 0xFF &&
                key != Keys.ControlKey && key != Keys.Menu && key != Keys.ShiftKey &&
                key != Keys.LWin && key != Keys.RWin;
        }
        catch
        {
            key = Keys.None;
            return false;
        }
    }

    private static string FormatNormalized(uint modifiers, Keys key)
    {
        List<string> parts = new List<string>();
        if ((modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & ModShift) != 0) parts.Add("Shift");
        if ((modifiers & ModWin) != 0) parts.Add("Win");
        int keyCode = (int)(key & Keys.KeyCode);
        if (keyCode >= (int)Keys.D0 && keyCode <= (int)Keys.D9)
        {
            parts.Add(((char)('0' + keyCode - (int)Keys.D0)).ToString());
        }
        else if (keyCode >= (int)Keys.A && keyCode <= (int)Keys.Z)
        {
            parts.Add(((char)('A' + keyCode - (int)Keys.A)).ToString());
        }
        else
        {
            parts.Add(key.ToString());
        }

        return string.Join("+", parts.ToArray());
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Global hotkey parser self-test failed: " + message);
        }
    }
}
