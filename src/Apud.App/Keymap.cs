using System.Text.Json;

namespace Apud.App;

/// <summary>
/// keymap.json (Module 6 step 2): user-rebindable keys over the command table,
/// same contract as tagnames.json — missing file/entry → built-in default, bad
/// line → reported in the message bar and the default kept, never a crash.
/// Chords are standard WinForms <see cref="Keys"/> combinations parsed with the
/// enum's own names plus a few everyday aliases (Esc, Del, PgUp...); dispatch
/// happens in MainForm.ProcessCmdKey, the framework's shortcut hook.
/// </summary>
public sealed class Keymap
{
    public const string FileName = "keymap.json";

    private readonly Dictionary<(CommandContext, Keys), string> _map = new();
    private readonly Dictionary<string, Keys> _byCommand = new();
    private readonly List<string> _diagnostics = new();

    /// <summary>One line per problem found while loading; empty when clean.</summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    public static Keymap LoadFile(CommandRegistry registry, string path)
    {
        string? json = null;
        string? readError = null;
        if (File.Exists(path))
        {
            try { json = File.ReadAllText(path); }
            catch (Exception ex) { readError = $"{FileName} not read ({ex.Message}) — using built-in keys."; }
        }
        var keymap = Build(registry, json);
        if (readError != null) keymap._diagnostics.Insert(0, readError);
        return keymap;
    }

    /// <summary>Assembles bindings (defaults overlaid by the file's entries);
    /// separated from file I/O so tests can run it headless.</summary>
    internal static Keymap Build(CommandRegistry registry, string? json)
    {
        var keymap = new Keymap();
        var overrides = keymap.ParseOverrides(registry, json);

        foreach (var cmd in registry.Commands)
        {
            Keys chord = Keys.None;
            string source = "built-in";
            if (overrides.TryGetValue(cmd.Id, out var fromFile))
            {
                if (fromFile == Keys.None) continue; // "" in the file = unbound
                chord = fromFile;
                source = FileName;
            }
            else if (cmd.DefaultKey != null && TryParseChord(cmd.DefaultKey, out var def))
            {
                chord = def;
            }
            if (chord != Keys.None) keymap.Bind(cmd, chord, source);
        }
        return keymap;
    }

    private void Bind(Command cmd, Keys chord, string source)
    {
        foreach (var ((context, keys), otherId) in _map)
        {
            if (keys != chord) continue;
            // Search and Editor never show at once, so they may share a chord;
            // Global overlaps both.
            if (context == cmd.Context || context == CommandContext.Global || cmd.Context == CommandContext.Global)
            {
                _diagnostics.Add($"{Describe(chord)} is already bound to {otherId} — {source} binding for {cmd.Id} ignored.");
                return;
            }
        }
        _map[(cmd.Context, chord)] = cmd.Id;
        _byCommand[cmd.Id] = chord;
    }

    /// <summary>Command id for a pressed chord, or null. The active context's
    /// binding wins over a Global one.</summary>
    public string? Lookup(Keys keyData, CommandContext active)
    {
        if (_map.TryGetValue((active, keyData), out var id)) return id;
        if (active != CommandContext.Global &&
            _map.TryGetValue((CommandContext.Global, keyData), out id)) return id;
        return null;
    }

    /// <summary>Display text of a command's effective binding ("Ctrl+L"), or null when unbound.</summary>
    public string? BindingFor(string commandId) =>
        _byCommand.TryGetValue(commandId, out var chord) ? Describe(chord) : null;

    // ---------- file parsing ----------

    private Dictionary<string, Keys> ParseOverrides(CommandRegistry registry, string? json)
    {
        var result = new Dictionary<string, Keys>();
        if (json is null) return result;

        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        try
        {
            using var doc = JsonDocument.Parse(json, options);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                _diagnostics.Add($"{FileName} ignored (expected an object of \"command-id\": \"key\" pairs) — using built-in keys.");
                return result;
            }
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (registry.Find(prop.Name) is null)
                {
                    _diagnostics.Add($"{FileName}: unknown command \"{prop.Name}\" — entry ignored.");
                    continue;
                }
                if (prop.Value.ValueKind != JsonValueKind.String)
                {
                    _diagnostics.Add($"{FileName}: {prop.Name} must be a text chord like \"Ctrl+L\" — built-in key kept.");
                    continue;
                }
                string text = prop.Value.GetString()!.Trim();
                if (text.Length == 0)
                {
                    result[prop.Name] = Keys.None; // explicit unbind
                }
                else if (TryParseChord(text, out var chord))
                {
                    result[prop.Name] = chord;
                }
                else
                {
                    _diagnostics.Add($"{FileName}: \"{text}\" is not a key {prop.Name} can bind to — built-in key kept.");
                }
            }
        }
        catch (JsonException ex)
        {
            _diagnostics.Add($"{FileName} ignored (line {ex.LineNumber + 1}: not valid JSON) — using built-in keys.");
        }
        return result;
    }

    // ---------- chord text ↔ Keys ----------

    private static readonly Dictionary<string, Keys> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Esc"] = Keys.Escape,
        ["Del"] = Keys.Delete,
        ["Ins"] = Keys.Insert,
        ["Return"] = Keys.Enter,
        ["Backspace"] = Keys.Back,
        ["PgUp"] = Keys.PageUp,
        ["PgDn"] = Keys.PageDown,
        ["Spacebar"] = Keys.Space,
    };

    internal static bool TryParseChord(string text, out Keys chord)
    {
        chord = Keys.None;
        Keys modifiers = Keys.None;
        Keys key = Keys.None;

        foreach (var raw in text.Split('+'))
        {
            string token = raw.Trim();
            if (token.Length == 0) return false;

            if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("Control", StringComparison.OrdinalIgnoreCase)) { modifiers |= Keys.Control; continue; }
            if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase)) { modifiers |= Keys.Alt; continue; }
            if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase)) { modifiers |= Keys.Shift; continue; }

            if (key != Keys.None) return false; // two non-modifier keys
            if (!TryParseKey(token, out key)) return false;
        }

        if (key == Keys.None) return false; // modifiers alone bind nothing
        chord = modifiers | key;
        return true;
    }

    private static bool TryParseKey(string token, out Keys key)
    {
        key = Keys.None;
        // Digits first: Enum.TryParse would read "9" as the raw enum value 9.
        if (token.Length == 1 && char.IsAsciiDigit(token[0]))
        {
            key = Keys.D0 + (token[0] - '0');
            return true;
        }
        if (Aliases.TryGetValue(token, out key)) return true;
        if (char.IsAsciiDigit(token[0])) return false; // no other numeric forms
        if (!Enum.TryParse(token, ignoreCase: true, out key)) return false;
        // Refuse tokens that are modifiers-by-another-name or flag combinations.
        return key is not (Keys.None or Keys.ControlKey or Keys.ShiftKey or Keys.Menu
                           or Keys.Control or Keys.Shift or Keys.Alt or Keys.Modifiers or Keys.KeyCode)
               && (key & ~Keys.KeyCode) == 0;
    }

    public static string Describe(Keys chord)
    {
        var parts = new List<string>(4);
        if (chord.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (chord.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (chord.HasFlag(Keys.Shift)) parts.Add("Shift");
        var code = chord & Keys.KeyCode;
        parts.Add(code switch
        {
            >= Keys.D0 and <= Keys.D9 => ((char)('0' + (code - Keys.D0))).ToString(),
            Keys.Back => "Backspace",
            Keys.Next => "PageDown", // enum's ToString picks the older name
            _ => code.ToString(),
        });
        return string.Join("+", parts);
    }
}
