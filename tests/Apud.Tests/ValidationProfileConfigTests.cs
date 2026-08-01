using Apud.App;
using Marc.Core.Validation;

namespace Apud.Tests;

/// <summary>
/// The user-editable profile files (Module 9) follow the keymap/tagnames
/// contract: a good file installs, a broken one is reported and the built-in
/// default stands, malformed entries are skipped and reported — never a crash.
/// </summary>
public class ValidationProfileConfigTests
{
    [Fact]
    public void A_valid_profile_is_applied()
    {
        var report = ValidationProfileConfig.ApplyJson(
            """{ "mandatory": [["245"],["100","110"]], "nonRepeatable": ["245"], "requiredSubfields": { "245": "ab" }, "singleHeading1xx": true }""",
            "BIB");

        Assert.Null(report);
        var p = ValidationProfileConfig.For("BIB");
        Assert.Equal(2, p.Mandatory.Count);
        Assert.Contains("245", p.NonRepeatable);
        Assert.Equal(new[] { 'a', 'b' }, p.RequiredSubfields["245"]);
        Assert.True(p.SingleHeading1xx);
    }

    [Fact]
    public void Broken_json_is_reported_and_the_default_stands()
    {
        var report = ValidationProfileConfig.ApplyJson("{ this is not json", "AUT");
        Assert.NotNull(report);
        // Falls back to the built-in AUT default (which requires a 1XX heading).
        var p = ValidationProfileConfig.For("AUT");
        Assert.Contains("100", p.Mandatory[0]);
        Assert.True(p.SingleHeading1xx);
    }

    [Fact]
    public void A_malformed_entry_is_skipped_and_reported()
    {
        var report = ValidationProfileConfig.ApplyJson(
            """{ "mandatory": [["245"]], "requiredSubfields": { "245": 7 } }""",
            "BIB");

        Assert.NotNull(report);
        Assert.Contains("requiredSubfields.245", report);
        // The good part still applied.
        Assert.Single(ValidationProfileConfig.For("BIB").Mandatory);
    }
}
