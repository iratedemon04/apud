using Marc.Core.FixedFields;
using Xunit;

namespace Apud.Tests;

/// <summary>
/// Module 7 — the fixed-field layout engine (Marc.Core, no UI/DB). The layouts
/// are the single source of truth the F8 dialog and (Module 9) the validator
/// share, so their integrity is tested directly on the model.
/// </summary>
public class FixedFieldTests
{
    // ----- layout integrity -----

    [Fact]
    public void Every_layout_covers_every_position_contiguously()
    {
        Assert.NotEmpty(FixedFieldLayouts.All);
        foreach (var layout in FixedFieldLayouts.All.Values)
        {
            var positions = layout.Positions.OrderBy(p => p.Off).ToList();
            Assert.Equal(0, positions[0].Off);
            for (int i = 1; i < positions.Count; i++)
                Assert.Equal(positions[i - 1].End + 1, positions[i].Off); // no gap, no overlap
            Assert.Equal(layout.Length - 1, positions[^1].End);          // covers the whole field
            Assert.All(positions, p => Assert.True(p.Len >= 1));
        }
    }

    [Theory]
    [InlineData("LDR", "bib", 24)]
    [InlineData("LDR", "authority", 24)]
    [InlineData("008", "BK", 40)]
    [InlineData("008", "CR", 40)]
    [InlineData("008", "MP", 40)]
    [InlineData("008", "MU", 40)]
    [InlineData("008", "VM", 40)]
    [InlineData("008", "CF", 40)]
    [InlineData("008", "MX", 40)]
    [InlineData("008", "authority", 40)]
    public void All_shipped_layouts_load_with_the_right_length(string field, string material, int length)
    {
        var layout = FixedFieldLayouts.Get(field, material);
        Assert.NotNull(layout);
        Assert.Equal(length, layout!.Length);
    }

    // ----- material determination (config of the 008) -----

    [Theory]
    [InlineData('a', 'm', "BK")] // language material, monograph
    [InlineData('t', 'm', "BK")] // manuscript language material
    [InlineData('a', 's', "CR")] // language material, serial
    [InlineData('a', 'i', "CR")] // integrating resource
    [InlineData('a', 'b', "CR")] // serial component part
    [InlineData('e', 'm', "MP")] // cartographic
    [InlineData('c', 'm', "MU")] // notated music
    [InlineData('j', 'm', "MU")] // musical sound recording
    [InlineData('g', 'm', "VM")] // projected medium
    [InlineData('r', 'm', "VM")] // 3-D artifact
    [InlineData('m', 'm', "CF")] // computer file
    [InlineData('p', 'c', "MX")] // mixed materials
    [InlineData('z', ' ', "authority")]
    public void Material008_is_derived_from_the_leader(char type, char level, string expected)
    {
        var leader = new string(' ', 24).ToCharArray();
        leader[6] = type;
        leader[7] = level;
        Assert.Equal(expected, FixedFieldLayouts.Material008(new string(leader)));
        Assert.NotNull(FixedFieldLayouts.For008(new string(leader)));
    }

    [Fact]
    public void Leader_layout_is_bib_unless_LDR06_is_z()
    {
        Assert.Equal("bib", FixedFieldLayouts.Leader(MarcLeaderWith(6, 'a'))!.Material);
        Assert.Equal("authority", FixedFieldLayouts.Leader(MarcLeaderWith(6, 'z'))!.Material);
    }

    // ----- read / assemble (FixedFieldData) -----

    [Fact]
    public void Existing_008_bytes_survive_a_round_trip_unchanged()
    {
        var layout = FixedFieldLayouts.Get("008", "BK")!;
        string original = "240115" + "s" + "2024" + new string(' ', 4) + "mx "
            + new string(' ', 15) + "0" + " " + "spa" + " d"; // a plausible 40-char BK 008
        Assert.Equal(40, original.Length);
        var data = new FixedFieldData(layout, original);
        Assert.Equal(original, data.ToString());
    }

    [Fact]
    public void An_008_built_entirely_from_positions_matches_a_hand_written_reference()
    {
        // M4 acceptance: the dialog assembles the same bytes a cataloguer would
        // type by counting spaces — proven here without WinForms.
        var layout = FixedFieldLayouts.Get("008", "BK")!;
        var data = new FixedFieldData(layout, new string(' ', 40));

        Set(data, layout, 0, "240115");  // date entered
        Set(data, layout, 6, "s");       // type of date
        Set(data, layout, 7, "2024");    // date 1
        Set(data, layout, 15, "mx");     // place (padded to "mx ")
        Set(data, layout, 33, "0");      // literary form
        Set(data, layout, 35, "spa");    // language

        // 0-5 date, 6 type, 7-10 date1, 11-14 blank, 15-17 "mx ", 18-32 blank,
        // 33 literary form, 34 blank, 35-37 lang, 38-39 blank.
        string expected = "240115" + "s" + "2024" + new string(' ', 4) + "mx "
            + new string(' ', 15) + "0" + " " + "spa" + new string(' ', 2);
        Assert.Equal(40, expected.Length);
        Assert.Equal(expected, data.ToString());
    }

    [Fact]
    public void Set_truncates_over_length_and_pads_short_input_with_spaces()
    {
        var layout = FixedFieldLayouts.Get("008", "BK")!;
        var data = new FixedFieldData(layout, new string(' ', 40));
        var place = layout.Positions.First(p => p.Off == 15); // 3 chars

        data.Set(place, "xxxxx");            // over length
        Assert.Equal("xxx", data.Slice(place));
        data.Set(place, "z");                // short
        Assert.Equal("z  ", data.Slice(place));
    }

    // ----- value maps -----

    [Fact]
    public void Coded_positions_carry_their_meaning_table()
    {
        var layout = FixedFieldLayouts.Get("008", "BK")!;
        var typeOfDate = layout.Positions.First(p => p.Off == 6);
        Assert.NotNull(typeOfDate.Values);
        Assert.Contains("Single", typeOfDate.Values!["s"]);
        Assert.True(layout.Positions.First(p => p.Off == 0).Auto == "yymmdd");
    }

    [Fact]
    public void Lookup_positions_are_tagged_for_the_validators_code_tables()
    {
        var layout = FixedFieldLayouts.Get("008", "BK")!;
        Assert.Equal("marc-countries", layout.Positions.First(p => p.Off == 15).Lookup);
        Assert.Equal("marc-languages", layout.Positions.First(p => p.Off == 35).Lookup);
    }

    private static void Set(FixedFieldData data, FixedFieldLayout layout, int off, string text) =>
        data.Set(layout.Positions.First(p => p.Off == off), text);

    private static string MarcLeaderWith(int pos, char c)
    {
        var leader = "00000nam a2200000 i 4500".ToCharArray();
        leader[pos] = c;
        return new string(leader);
    }
}
