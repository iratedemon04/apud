using Marc.Core;
using Marc.Core.Validation;

namespace Apud.Tests;

/// <summary>
/// The record-only validation stages (Module 9, docs/PLAN.md §8 stages 1-3):
/// the seeded error corpus — every real-world slip caught with the right field
/// reference and severity — plus the fixed-field and profile rules. Pure
/// Marc.Core, no database. The authority stage and auto-fill are exercised in
/// PushServiceTests. Records are built in code so the 008 is exactly 40 bytes.
/// </summary>
public class ValidationTests
{
    // A valid 40-character book 008 (date "s", place "mx", Spanish, source "d").
    private static readonly string Good008 = "240101s2020    mx" + new string(' ', 18) + "spa d";

    private static MarcRecord CleanBib()
    {
        var r = new MarcRecord { Leader = "00000nam a2200000 i 4500" };
        r.Fields.Add(new MarcField("008") { ControlData = Good008 });
        r.Fields.Add(Data("245", '1', '0', ('a', "Física cuántica")));
        return r;
    }

    private static List<ValidationFinding> Validate(MarcRecord r, string @base = "BIB") =>
        RecordValidator.Validate(r, @base, ValidationProfile.Default(@base));

    private static ValidationFinding? Code(IEnumerable<ValidationFinding> f, string code) =>
        f.FirstOrDefault(x => x.Code == code);

    [Fact]
    public void Good008_is_forty_characters() => Assert.Equal(40, Good008.Length);

    [Fact]
    public void A_clean_record_has_no_errors() =>
        Assert.DoesNotContain(Validate(CleanBib()), f => f.IsError);

    [Fact]
    public void A_clean_record_has_no_findings_at_all() =>
        Assert.Empty(Validate(CleanBib()));

    [Fact]
    public void Missing_mandatory_245_is_an_error()
    {
        var r = CleanBib();
        r.Fields.RemoveAll(f => f.Tag == "245");
        var finding = Code(Validate(r), "profile.mandatory");
        Assert.NotNull(finding);
        Assert.True(finding!.IsError);
    }

    [Fact]
    public void The_245_without_a_subfield_a_is_an_error_on_that_field()
    {
        var r = CleanBib();
        var f245 = r.Fields.Single(x => x.Tag == "245");
        f245.Subfields.Clear();
        f245.Subfields.Add(new MarcSubfield('b', "subtitle only"));

        var finding = Code(Validate(r), "profile.subfield");
        Assert.NotNull(finding);
        Assert.Equal(FieldRef.Field(r.Fields.IndexOf(f245)), finding!.Ref);
    }

    [Fact]
    public void A_dropped_delimiter_reads_as_an_uppercase_code_and_is_flagged()
    {
        // "$Preciado" (the classic slip for "$aPreciado") parses to code 'P'.
        var r = CleanBib();
        r.Fields.Add(Data("700", '1', ' ', ('P', "reciado, Amado")));

        var finding = Code(Validate(r), "subfield.code");
        Assert.NotNull(finding);
        Assert.True(finding!.IsError);
        Assert.Equal(0, finding.Ref!.Value.SubfieldIndex);
    }

    [Fact]
    public void An_empty_subfield_is_an_error()
    {
        var r = CleanBib();
        r.Fields.Add(Data("500", ' ', ' ', ('a', "")));
        Assert.True(Code(Validate(r), "subfield.empty")!.IsError);
    }

    [Fact]
    public void A_data_field_with_no_subfields_is_an_error()
    {
        var r = CleanBib();
        r.Fields.Add(new MarcField("500"));
        Assert.NotNull(Code(Validate(r), "field.no-subfields"));
    }

    [Fact]
    public void A_non_digit_tag_is_an_error()
    {
        var r = CleanBib();
        r.Fields.Add(new MarcField("   ")); // a blank field never given a tag
        Assert.NotNull(Code(Validate(r), "tag.invalid"));
    }

    [Fact]
    public void A_bad_indicator_is_an_error()
    {
        var r = CleanBib();
        r.Fields.Single(x => x.Tag == "245").Ind1 = 'x';
        Assert.NotNull(Code(Validate(r), "indicator.invalid"));
    }

    [Fact]
    public void An_empty_control_field_is_an_error_but_derived_ones_are_not()
    {
        var r = CleanBib();
        r.Fields.Add(new MarcField("007") { ControlData = "" });
        r.Fields.Add(new MarcField("001") { ControlData = "" }); // derived at push — fine
        var f = Validate(r);
        Assert.Contains(f, x => x.Code == "control.empty" && x.Message.Contains("007"));
        Assert.DoesNotContain(f, x => x.Code == "control.empty" && x.Message.Contains("001"));
    }

    [Fact]
    public void A_short_008_is_an_error()
    {
        var r = CleanBib();
        r.Fields.Single(x => x.Tag == "008").ControlData = "240101s2020";
        var finding = Code(Validate(r), "008.length");
        Assert.NotNull(finding);
        Assert.True(finding!.IsError);
    }

    [Fact]
    public void A_repeated_non_repeatable_field_is_an_error()
    {
        var r = CleanBib();
        r.Fields.Add(Data("245", '1', '0', ('a', "A second title statement")));
        Assert.NotNull(Code(Validate(r), "profile.repeat"));
    }

    [Fact]
    public void An_unrecognised_fixed_field_code_is_a_warning_not_an_error()
    {
        // 008/06 "Type of date" of 'Z' is not a defined code — informational only.
        var r = CleanBib();
        var bytes = Good008.ToCharArray();
        bytes[6] = 'Z';
        r.Fields.Single(x => x.Tag == "008").ControlData = new string(bytes);

        var finding = Code(Validate(r), "fixed.code");
        Assert.NotNull(finding);
        Assert.False(finding!.IsError);
    }

    [Fact]
    public void Fill_characters_in_a_coded_slot_are_not_flagged()
    {
        var r = CleanBib();
        var bytes = Good008.ToCharArray();
        bytes[6] = '|'; // fill: "no attempt to code"
        r.Fields.Single(x => x.Tag == "008").ControlData = new string(bytes);
        Assert.DoesNotContain(Validate(r), x => x.Code == "fixed.code");
    }

    [Fact]
    public void An_authority_record_needs_exactly_one_1XX()
    {
        var r = new MarcRecord { Leader = "00000nz  a2200000n  4500" };
        r.Fields.Add(Data("100", '1', ' ', ('a', "Moreno, Matías")));
        r.Fields.Add(Data("110", '2', ' ', ('a', "Instituto")));
        Assert.NotNull(Code(Validate(r, "AUT"), "profile.single-1xx"));
    }

    [Fact]
    public void An_authority_record_with_no_heading_is_missing_its_mandatory_1XX()
    {
        var r = new MarcRecord { Leader = "00000nz  a2200000n  4500" };
        r.Fields.Add(Data("670", ' ', ' ', ('a', "Source")));
        Assert.NotNull(Code(Validate(r, "AUT"), "profile.mandatory"));
    }

    private static MarcField Data(string tag, char ind1, char ind2, params (char Code, string Value)[] subfields)
    {
        var f = new MarcField(tag) { Ind1 = ind1, Ind2 = ind2 };
        foreach (var (code, value) in subfields) f.Subfields.Add(new MarcSubfield(code, value));
        return f;
    }
}
