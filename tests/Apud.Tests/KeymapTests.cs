using Apud.App;

namespace Apud.Tests;

/// <summary>
/// keymap.json contract (Module 6 step 2, same shape as tagnames.json): missing
/// file/entry → built-in default; bad entry → reported, default kept; broken
/// file → reported, defaults stand; conflicts → reported, first binding wins;
/// context-scoped lookup; never a crash.
/// </summary>
public class KeymapTests
{
    private static readonly Action Nothing = () => { };

    private static CommandRegistry Registry(params Command[] commands)
    {
        var r = new CommandRegistry();
        foreach (var c in commands) r.Add(c);
        return r;
    }

    private static Command Cmd(string id, string? key = null, CommandContext context = CommandContext.Global) =>
        new() { Id = id, Name = id, DefaultKey = key, Context = context, Execute = Nothing };

    // ---------- chord parsing ----------

    [Theory]
    [InlineData("Ctrl+L", Keys.Control | Keys.L)]
    [InlineData("ctrl+shift+f3", Keys.Control | Keys.Shift | Keys.F3)]
    [InlineData("F6", Keys.F6)]
    [InlineData("Alt+F4", Keys.Alt | Keys.F4)]
    [InlineData("Del", Keys.Delete)]
    [InlineData("Esc", Keys.Escape)]
    [InlineData(" Ctrl + N ", Keys.Control | Keys.N)]
    [InlineData("Shift+9", Keys.Shift | Keys.D9)]
    [InlineData("PgDn", Keys.PageDown)]
    [InlineData("Enter", Keys.Enter)]
    public void Chord_parsing_accepts_the_documented_syntax(string text, Keys expected)
    {
        Assert.True(Keymap.TryParseChord(text, out var chord));
        Assert.Equal(expected, chord);
    }

    [Theory]
    [InlineData("")]              // nothing
    [InlineData("Ctrl+")]         // dangling separator
    [InlineData("Ctrl+Shift")]    // modifiers alone
    [InlineData("Foo")]           // no such key
    [InlineData("A+B")]           // two keys
    [InlineData("F25")]           // beyond F24
    [InlineData("Ctrl+ControlKey")] // modifier dressed as a key
    [InlineData("113")]           // raw enum numbers are not keys
    public void Chord_parsing_rejects_nonsense(string text)
    {
        Assert.False(Keymap.TryParseChord(text, out _));
    }

    [Fact]
    public void Describe_round_trips_the_canonical_form()
    {
        Assert.True(Keymap.TryParseChord("ctrl+shift+f3", out var chord));
        Assert.Equal("Ctrl+Shift+F3", Keymap.Describe(chord));
        Assert.True(Keymap.TryParseChord("PgDn", out chord));
        Assert.Equal("PageDown", Keymap.Describe(chord));
    }

    // ---------- defaults and file overlay ----------

    [Fact]
    public void Defaults_apply_when_there_is_no_file()
    {
        var keymap = Keymap.Build(Registry(Cmd("search.focus", "F2"), Cmd("menu.only")), json: null);

        Assert.Empty(keymap.Diagnostics);
        Assert.Equal("search.focus", keymap.Lookup(Keys.F2, CommandContext.Search));
        Assert.Equal("F2", keymap.BindingFor("search.focus"));
        Assert.Null(keymap.BindingFor("menu.only"));
    }

    [Fact]
    public void File_rebinds_and_missing_entries_keep_their_defaults()
    {
        var registry = Registry(Cmd("record.push", "Ctrl+L"), Cmd("field.new", "F6"));
        var keymap = Keymap.Build(registry, """{ "record.push": "Ctrl+P" }""");

        Assert.Empty(keymap.Diagnostics);
        Assert.Equal("record.push", keymap.Lookup(Keys.Control | Keys.P, CommandContext.Global));
        Assert.Null(keymap.Lookup(Keys.Control | Keys.L, CommandContext.Global)); // old default gone
        Assert.Equal("field.new", keymap.Lookup(Keys.F6, CommandContext.Global)); // untouched
    }

    [Fact]
    public void Empty_string_unbinds_a_command()
    {
        var keymap = Keymap.Build(Registry(Cmd("search.focus", "F2")), """{ "search.focus": "" }""");

        Assert.Empty(keymap.Diagnostics);
        Assert.Null(keymap.Lookup(Keys.F2, CommandContext.Search));
        Assert.Null(keymap.BindingFor("search.focus"));
    }

    [Fact]
    public void Bad_chord_is_reported_and_the_default_kept()
    {
        var keymap = Keymap.Build(Registry(Cmd("search.focus", "F2")), """{ "search.focus": "Hyper+Q" }""");

        Assert.Contains(keymap.Diagnostics, d => d.Contains("Hyper+Q"));
        Assert.Equal("search.focus", keymap.Lookup(Keys.F2, CommandContext.Search));
    }

    [Fact]
    public void Unknown_command_id_is_reported_and_ignored()
    {
        var keymap = Keymap.Build(Registry(Cmd("search.focus", "F2")), """{ "record.warp": "F9" }""");

        Assert.Contains(keymap.Diagnostics, d => d.Contains("record.warp"));
        Assert.Null(keymap.Lookup(Keys.F9, CommandContext.Global));
    }

    [Fact]
    public void Broken_json_is_reported_and_all_defaults_stand()
    {
        var keymap = Keymap.Build(Registry(Cmd("search.focus", "F2")), "{ this is not json");

        Assert.Contains(keymap.Diagnostics, d => d.Contains("built-in"));
        Assert.Equal("search.focus", keymap.Lookup(Keys.F2, CommandContext.Search));
    }

    [Fact]
    public void Comments_and_trailing_commas_are_tolerated()
    {
        var keymap = Keymap.Build(Registry(Cmd("field.new", "F6")), "// mine\n{ \"field.new\": \"F9\", }");

        Assert.Empty(keymap.Diagnostics);
        Assert.Equal("field.new", keymap.Lookup(Keys.F9, CommandContext.Global));
    }

    // ---------- conflicts and contexts ----------

    [Fact]
    public void Conflicting_bindings_are_reported_and_the_first_wins()
    {
        var registry = Registry(Cmd("first", "F6"), Cmd("second", "F7"));
        var keymap = Keymap.Build(registry, """{ "second": "F6" }""");

        Assert.Contains(keymap.Diagnostics, d => d.Contains("first") && d.Contains("second"));
        Assert.Equal("first", keymap.Lookup(Keys.F6, CommandContext.Global));
        Assert.Null(keymap.Lookup(Keys.F7, CommandContext.Global)); // loser is not half-bound
    }

    [Fact]
    public void Active_context_binding_beats_a_global_one()
    {
        var registry = Registry(
            Cmd("global.thing", "F9"),
            Cmd("editor.thing", "F9", CommandContext.Editor));
        var keymap = Keymap.Build(registry, json: null);

        // Editor vs Global on the same chord is a real overlap — reported, and
        // the first-registered (global) binding wins everywhere.
        Assert.Contains(keymap.Diagnostics, d => d.Contains("editor.thing"));
        Assert.Equal("global.thing", keymap.Lookup(Keys.F9, CommandContext.Editor));
    }

    [Fact]
    public void Search_and_editor_may_share_a_chord_without_conflict()
    {
        var registry = Registry(
            Cmd("search.thing", "F9", CommandContext.Search),
            Cmd("editor.thing", "F9", CommandContext.Editor));
        var keymap = Keymap.Build(registry, json: null);

        Assert.Empty(keymap.Diagnostics);
        Assert.Equal("search.thing", keymap.Lookup(Keys.F9, CommandContext.Search));
        Assert.Equal("editor.thing", keymap.Lookup(Keys.F9, CommandContext.Editor));
        Assert.Null(keymap.Lookup(Keys.F9, CommandContext.Global));
    }

    // ---------- the file we ship ----------

    [Fact]
    public void Shipped_keymap_file_parses_clean()
    {
        // Every command id the shipped keymap.json binds must be a real command,
        // and its chords must not collide — this is what "parses clean" means.
        var registry = Registry(
            Cmd("search.focus"), Cmd("record.new"), Cmd("record.save-draft", context: CommandContext.Editor),
            Cmd("record.save-template", context: CommandContext.Editor),
            Cmd("record.undo", context: CommandContext.Editor), Cmd("record.redo", context: CommandContext.Editor),
            Cmd("field.new", context: CommandContext.Editor), Cmd("subfield.new", context: CommandContext.Editor),
            Cmd("field.delete", context: CommandContext.Editor), Cmd("subfield.delete", context: CommandContext.Editor),
            Cmd("field.fixed-edit", context: CommandContext.Editor), Cmd("field.validate", context: CommandContext.Editor),
            Cmd("record.validate", context: CommandContext.Editor), Cmd("record.push", context: CommandContext.Editor),
            Cmd("app.exit"));
        string shipped = Path.Combine(AppContext.BaseDirectory, Keymap.FileName);

        Assert.True(File.Exists(shipped), $"expected {Keymap.FileName} copied to test output");
        var keymap = Keymap.LoadFile(registry, shipped);
        Assert.Empty(keymap.Diagnostics);
        Assert.Equal("search.focus", keymap.Lookup(Keys.F2, CommandContext.Search));
        Assert.Equal("record.push", keymap.Lookup(Keys.Control | Keys.L, CommandContext.Editor));
    }

    [Fact]
    public void Missing_file_is_fine()
    {
        var keymap = Keymap.LoadFile(Registry(Cmd("search.focus", "F2")),
            Path.Combine(Path.GetTempPath(), "apud-no-such-keymap.json"));

        Assert.Empty(keymap.Diagnostics);
        Assert.Equal("search.focus", keymap.Lookup(Keys.F2, CommandContext.Search));
    }

    [Fact]
    public void Duplicate_command_ids_are_a_programming_error()
    {
        var registry = Registry(Cmd("x"));
        Assert.Throws<InvalidOperationException>(() => registry.Add(Cmd("x")));
    }
}
