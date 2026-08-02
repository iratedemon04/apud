using Apud.App;

namespace Apud.Tests;

/// <summary>
/// taghelp.json contract (Module 10, F1 field help) — the same shape as
/// tagnames.json: missing file or entry → built-in paragraph; a tag with no help
/// at all → a fallback naming the field; bad entry skipped and reported; broken
/// file reported and built-ins stand; never a crash.
/// </summary>
public class FieldHelpTests
{
    [Fact]
    public void Override_replaces_builtin_and_missing_entries_fall_back()
    {
        Assert.Null(FieldHelp.ApplyJson("""{ "245": "My house title help." }"""));
        Assert.Equal("My house title help.", FieldHelp.For("245"));
        Assert.Contains("Subject Added Entry", FieldHelp.For("650")); // not overridden → built-in
        FieldHelp.ApplyJson("{}");
        Assert.Contains("Title Statement", FieldHelp.For("245")); // override removed → built-in returns
    }

    [Fact]
    public void An_uncovered_tag_falls_back_to_its_name_and_never_empty()
    {
        string help = FieldHelp.For("099"); // no built-in help, but 090-ish local-ish name
        Assert.False(string.IsNullOrWhiteSpace(help));
        Assert.Contains("099", help);
    }

    [Fact]
    public void Local_9xx_block_gets_a_reserved_for_local_use_note()
    {
        string help = FieldHelp.For("945");
        Assert.Contains("945", help);
        Assert.Contains("local", help, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_tolerated()
    {
        Assert.Null(FieldHelp.ApplyJson("// header\n{ \"500\": \"Donor note help.\", }"));
        Assert.Equal("Donor note help.", FieldHelp.For("500"));
        FieldHelp.ApplyJson("{}");
    }

    [Fact]
    public void Broken_file_is_reported_and_builtins_stand()
    {
        string? report = FieldHelp.ApplyJson("{ this is not json");
        Assert.NotNull(report);
        Assert.Contains("built-in", report);
        Assert.Contains("Title Statement", FieldHelp.For("245"));
    }

    [Fact]
    public void Non_text_entry_is_skipped_and_reported_while_good_entries_apply()
    {
        string? report = FieldHelp.ApplyJson("""{ "245": "Good help.", "650": 7 }""");
        Assert.NotNull(report);
        Assert.Contains("650", report);
        Assert.Equal("Good help.", FieldHelp.For("245"));
        Assert.Contains("Subject Added Entry", FieldHelp.For("650")); // skipped → built-in
        FieldHelp.ApplyJson("{}");
    }

    [Fact]
    public void Missing_file_is_fine()
    {
        Assert.Null(FieldHelp.LoadFile(Path.Combine(Path.GetTempPath(), "apud-no-such-taghelp.json")));
        Assert.Contains("Title Statement", FieldHelp.For("245"));
    }

    [Fact]
    public void Shipped_default_file_parses_clean()
    {
        string shipped = Path.Combine(AppContext.BaseDirectory, FieldHelp.FileName);
        Assert.True(File.Exists(shipped), $"expected {FieldHelp.FileName} copied to test output");
        Assert.Null(FieldHelp.LoadFile(shipped));
        FieldHelp.ApplyJson("{}");
    }
}
