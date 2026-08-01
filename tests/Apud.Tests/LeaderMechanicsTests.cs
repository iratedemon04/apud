using Marc.Core;

namespace Apud.Tests;

/// <summary>
/// The leader bytes push derives (Module 9, docs/PLAN.md §8 stage 5): record
/// length (00-04) and base address (12-16) laid out exactly as ISO 2709 would,
/// plus the fixed structural constants. Compared against a hand-computed record.
/// </summary>
public class LeaderMechanicsTests
{
    [Fact]
    public void Recompute_fills_length_base_address_and_constants()
    {
        // One 40-char control field + one data field 245 ‡a"Hi" (value = 2 bytes).
        var r = new MarcRecord { Leader = "00000nam a2200000 i 4500" };
        r.Fields.Add(new MarcField("008") { ControlData = new string('x', 40) });
        var f245 = new MarcField("245");
        f245.Subfields.Add(new MarcSubfield('a', "Hi"));
        r.Fields.Add(f245);

        LeaderMechanics.Recompute(r);

        // base address = 24 (leader) + 2*12 (directory) + 1 (terminator) = 49.
        Assert.Equal("00049", r.Leader.Substring(12, 5));

        // field lengths: 008 = 40 + 1 = 41; 245 = 2 (indicators) + (1+1+2) + 1 = 7.
        // record length = 49 + 41 + 7 + 1 (record terminator) = 98.
        Assert.Equal("00098", r.Leader[..5]);

        Assert.Equal('2', r.Leader[10]);
        Assert.Equal('2', r.Leader[11]);
        Assert.Equal("4500", r.Leader.Substring(20, 4));
    }

    [Fact]
    public void Recompute_counts_utf8_bytes_not_characters()
    {
        // "ñá" is 2 characters but 4 UTF-8 bytes — the record length must reflect bytes.
        var ascii = new MarcRecord { Leader = "00000nam a2200000 i 4500" };
        var af = new MarcField("245"); af.Subfields.Add(new MarcSubfield('a', "aa"));
        ascii.Fields.Add(af);
        LeaderMechanics.Recompute(ascii);

        var accented = new MarcRecord { Leader = "00000nam a2200000 i 4500" };
        var nf = new MarcField("245"); nf.Subfields.Add(new MarcSubfield('a', "ñá"));
        accented.Fields.Add(nf);
        LeaderMechanics.Recompute(accented);

        int asciiLen = int.Parse(ascii.Leader[..5]);
        int accentedLen = int.Parse(accented.Leader[..5]);
        Assert.Equal(asciiLen + 2, accentedLen); // two extra continuation bytes
    }
}
