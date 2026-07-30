using Apud.App;

namespace Apud.Tests;

/// <summary>
/// tagnames.json contract (same shape keymap.json will follow): missing file or
/// entry → built-in default; bad entry skipped and reported; broken file
/// reported and built-ins stand; never a crash.
/// </summary>
[Collection("TagNames statics")] // shares TagNames' static override table with RecordDisplayTests
public class TagNamesTests
{
    [Fact]
    public void Override_replaces_builtin_and_missing_entries_fall_back()
    {
        Assert.Null(TagNames.ApplyJson("""{ "245": "Titulo" }"""));
        Assert.Equal("Titulo", TagNames.For("245"));
        Assert.Equal("Main Entry--Pers.", TagNames.For("100")); // not in file → default
        TagNames.ApplyJson("{}");
        Assert.Equal("Title Statement", TagNames.For("245")); // entry removed → default returns
    }

    [Fact]
    public void Unknown_tags_can_be_named_and_local_block_keeps_its_default()
    {
        TagNames.ApplyJson("""{ "999": "Barcode" }""");
        Assert.Equal("Barcode", TagNames.For("999"));
        Assert.Equal("Local", TagNames.For("901")); // 9XX default untouched
        TagNames.ApplyJson("{}");
    }

    [Fact]
    public void Comments_and_trailing_commas_are_tolerated()
    {
        Assert.Null(TagNames.ApplyJson("// header comment\n{ \"590\": \"Donor Note\", }"));
        Assert.Equal("Donor Note", TagNames.For("590"));
        TagNames.ApplyJson("{}");
    }

    [Fact]
    public void Broken_file_is_reported_and_builtins_stand()
    {
        string? report = TagNames.ApplyJson("{ this is not json");
        Assert.NotNull(report);
        Assert.Contains("built-in", report);
        Assert.Equal("Title Statement", TagNames.For("245"));
    }

    [Fact]
    public void Non_text_entry_is_skipped_and_reported_while_good_entries_apply()
    {
        string? report = TagNames.ApplyJson("""{ "245": "Titulo", "100": 7 }""");
        Assert.NotNull(report);
        Assert.Contains("100", report);
        Assert.Equal("Titulo", TagNames.For("245"));
        Assert.Equal("Main Entry--Pers.", TagNames.For("100")); // skipped → default
        TagNames.ApplyJson("{}");
    }

    [Fact]
    public void Missing_file_is_fine()
    {
        Assert.Null(TagNames.LoadFile(Path.Combine(Path.GetTempPath(), "apud-no-such-tagnames.json")));
        Assert.Equal("Title Statement", TagNames.For("245"));
    }

    [Fact]
    public void Shipped_default_file_parses_clean()
    {
        // The file we ship beside the exe must be a valid starting point.
        string shipped = Path.Combine(AppContext.BaseDirectory, TagNames.FileName);
        Assert.True(File.Exists(shipped), $"expected {TagNames.FileName} copied to test output");
        Assert.Null(TagNames.LoadFile(shipped));
        Assert.Equal("Title Statement", TagNames.For("245"));
        TagNames.ApplyJson("{}");
    }
}
