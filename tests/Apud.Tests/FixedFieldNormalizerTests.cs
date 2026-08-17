using Marc.Core;
using Xunit;

namespace Apud.Tests;

/// <summary>
/// The fixed-field blank-placeholder normalizer (Marc.Core, no UI/DB): '\' and '^'
/// in the leader and 006/007/008 become real spaces, while the MARC fill character
/// '|', coded values, and every variable field are left alone.
/// </summary>
public class FixedFieldNormalizerTests
{
    private static MarcField Control(string tag, string data) =>
        new(tag) { ControlData = data };

    private static MarcField Data(string tag, char code, string value)
    {
        var f = new MarcField(tag);
        f.Subfields.Add(new MarcSubfield(code, value));
        return f;
    }

    [Fact]
    public void Backslash_and_caret_in_the_leader_become_spaces()
    {
        // A 24-char leader whose optional coded positions are drawn with '\' and '^'.
        var rec = new MarcRecord { Leader = @"00000na\\a2200000^i^4500" };

        int changed = FixedFieldNormalizer.Normalize(rec);

        Assert.Equal("00000na  a2200000 i 4500", rec.Leader);
        Assert.Equal(24, rec.Leader.Length);   // length is preserved
        Assert.Equal(4, changed);
    }

    [Fact]
    public void Placeholders_in_008_and_006_and_007_become_spaces()
    {
        // Inputs are canonical (space-filled) strings with the blanks drawn as '\' or
        // '^'; normalizing must recover the canonical form exactly — including length.
        const string canon006 = "m        d        ";
        const string canon007 = "cr  n";
        const string canon008 = "260415s2017    mx            000 0 spa d";

        var rec = new MarcRecord();
        rec.Fields.Add(Control("006", canon006.Replace(' ', '\\')));
        rec.Fields.Add(Control("007", canon007.Replace(' ', '^')));
        rec.Fields.Add(Control("008", canon008.Replace(' ', '\\'))); // whole 008 blanks → '\'

        FixedFieldNormalizer.Normalize(rec);

        Assert.Equal(canon006, rec.Fields[0].ControlData);
        Assert.Equal(canon007, rec.Fields[1].ControlData);
        Assert.Equal(canon008, rec.Fields[2].ControlData);
    }

    [Fact]
    public void The_marc_fill_character_and_coded_values_survive()
    {
        // '|' is a deliberate "no attempt to code" and must NOT be touched.
        var rec = new MarcRecord { Leader = "00000nam a2200000|i 4500" };
        rec.Fields.Add(Control("008", "||||||s2017||||mx||||||||||||000 0 spa d"));

        int changed = FixedFieldNormalizer.Normalize(rec);

        Assert.Equal("00000nam a2200000|i 4500", rec.Leader);
        Assert.Equal("||||||s2017||||mx||||||||||||000 0 spa d", rec.Fields[0].ControlData);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void Non_fixed_control_fields_and_variable_fields_are_left_alone()
    {
        var rec = new MarcRecord { Leader = "00000nam a2200000 i 4500" };
        // 001–005 are control fields but NOT coded fixed fields: a '\' there is data.
        rec.Fields.Add(Control("001", @"ab\cd"));
        rec.Fields.Add(Control("005", @"2017\\\\"));
        // In a variable field '\' is ordinary data, never a fixed-field blank.
        rec.Fields.Add(Data("245", 'a', @"C:\path\to\thing"));

        int changed = FixedFieldNormalizer.Normalize(rec);

        Assert.Equal(@"ab\cd", rec.Fields[0].ControlData);
        Assert.Equal(@"2017\\\\", rec.Fields[1].ControlData);
        Assert.Equal(@"C:\path\to\thing", rec.Fields[2].Subfield('a'));
        Assert.Equal(0, changed);
    }

    [Fact]
    public void A_clean_record_is_unchanged_and_reports_zero()
    {
        var rec = new MarcRecord { Leader = "00766nam a22002534i 4500" };
        rec.Fields.Add(Control("008", "260415s2017    mx            000 0 spa d"));

        Assert.Equal(0, FixedFieldNormalizer.Normalize(rec));
    }

    // ----- encoding normalization (LDR/09 -> 'a') -----

    [Fact]
    public void Blank_ldr09_becomes_unicode_a()
    {
        var rec = new MarcRecord { Leader = "00849nz   2200253n  4500" }; // LDR/09 blank (MARC-8)

        int changed = FixedFieldNormalizer.NormalizeEncoding(rec);

        Assert.Equal('a', rec.Leader[9]);
        Assert.Equal("00849nz  a2200253n  4500", rec.Leader);
        Assert.Equal(24, rec.Leader.Length);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Already_unicode_ldr09_is_left_alone()
    {
        var rec = new MarcRecord { Leader = "00766nam a22002534i 4500" }; // LDR/09 already 'a'
        Assert.Equal(0, FixedFieldNormalizer.NormalizeEncoding(rec));
    }

    [Fact]
    public void Encoding_normalization_touches_only_position_09()
    {
        var rec = new MarcRecord { Leader = "00849nz   2200253n  4500" };
        FixedFieldNormalizer.NormalizeEncoding(rec);
        // Every position except 09 is unchanged; 20-23 stays "4500", 10-11 stays "22".
        Assert.Equal("00849nz  ", rec.Leader[..9]);
        Assert.Equal("2200253n  4500", rec.Leader[10..]);
    }
}
