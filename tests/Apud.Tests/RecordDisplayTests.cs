using Apud.App;
using Marc.Core;
using Marc.Core.Mrk;

namespace Apud.Tests;

/// <summary>
/// The viewer's display contract (docs/ALEPH-WORKFLOW.md): one row per subfield,
/// codes in their own column, '^' for blanks in LDR/control data, '_' for blank
/// indicators, and never a '$' of .mrk notation on screen.
/// </summary>
public class RecordDisplayTests
{
    private static MarcRecord Parse(string mrk) => MrkReader.Read(mrk).Records[0];

    private const string Monograph =
        "=LDR  00766nam a22002534i 4500\n" +
        "=001  42\n" +
        "=008  260415s2017    mx            000 0 spa d\n" +
        "=100  1\\$aMoreno, Matías$eautor\n" +
        "=245  10$aGrandes proyectos$bcientíficos\n" +
        "=650  \\4$aFísica nuclear$xInvestigación\n";

    [Fact]
    public void Leader_and_control_fields_show_blanks_as_carets()
    {
        var rows = RecordDisplay.Build(Parse(Monograph));

        Assert.Equal(new DisplayRow("Leader", "LDR", "", "", "00766nam^a22002534i^4500"), rows[0]);
        var f008 = rows.Single(r => r.Tag == "008");
        Assert.Equal("260415s2017^^^^mx^^^^^^^^^^^^000^0^spa^d", f008.Value);
        Assert.Equal("Fixed Data", f008.FieldName);
    }

    [Fact]
    public void Each_subfield_is_its_own_row_and_continuations_are_blank_left_of_the_code()
    {
        var rows = RecordDisplay.Build(Parse(Monograph));

        var i650 = rows.ToList().FindIndex(r => r.Tag == "650");
        Assert.Equal(new DisplayRow("Subject--Topical", "650", "_4", "a", "Física nuclear"), rows[i650]);
        Assert.Equal(new DisplayRow("", "", "", "x", "Investigación"), rows[i650 + 1]);
    }

    [Fact]
    public void Blank_indicators_show_as_underscores_and_given_ones_literally()
    {
        var rows = RecordDisplay.Build(Parse(Monograph));

        Assert.Equal("1_", rows.Single(r => r.Tag == "100").Indicators);
        Assert.Equal("10", rows.Single(r => r.Tag == "245").Indicators);
    }

    [Fact]
    public void No_dollar_notation_appears_but_literal_dollars_in_data_do()
    {
        var rows = RecordDisplay.Build(Parse(
            "=LDR  00000nam a2200000 i 4500\n=245  10$aEl {dollar} fuerte$bhistoria del peso\n"));

        var title = rows.Single(r => r.Tag == "245");
        Assert.Equal("El $ fuerte", title.Value);          // literal $, shown as itself
        Assert.Equal("a", title.Code);                     // code lives in its own column
        Assert.DoesNotContain(rows, r => r.Value.Contains("$a") || r.Value.Contains("{dollar}"));
    }

    [Fact]
    public void Unknown_tags_get_an_empty_name_but_9XX_reads_Local()
    {
        var rows = RecordDisplay.Build(Parse(
            "=LDR  00000nam a2200000 i 4500\n=387  \\\\$aMisterioso\n=901  \\\\$aColección X\n=773  0\\$tRevista\n"));

        Assert.Equal("", rows.Single(r => r.Tag == "387").FieldName);
        Assert.Equal("Local", rows.Single(r => r.Tag == "901").FieldName);
        Assert.Equal("Host Item", rows.Single(r => r.Tag == "773").FieldName);
    }

    [Fact]
    public void Header_shows_base_001_and_title_for_bib()
    {
        var rec = Parse(Monograph);
        Assert.Equal("BIB 42  Grandes proyectos científicos",
            RecordDisplay.HeaderText("BIB", rec.ControlNumber, rec));
    }

    [Fact]
    public void Header_uses_the_1XX_heading_for_authorities_and_marks_missing_001()
    {
        var aut = Parse("=LDR  00000nz  a2200000n  4500\n=150  \\\\$aFísica nuclear\n");
        Assert.Equal("AUT (no 001)  Física nuclear",
            RecordDisplay.HeaderText("AUT", aut.ControlNumber, aut));
    }
}
