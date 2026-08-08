using Apud.App;
using Marc.Core;
using Marc.Core.Mrk;

namespace Apud.Tests;

/// <summary>
/// The editor's box-layout contract (docs/UI-REWRITE-PLAN.md): one row per
/// subfield line, name | tag | ind | code | value, with real per-box length
/// rules and the blank-as-data / phantom-subfield shaping that lets F6 -> type
/// -> Tab flow with no rebuild. Pure, so the tricky "which boxes, what rules"
/// logic is proven here without a GUI.
/// </summary>
[Collection("TagNames statics")] // TagNames swaps a static override table; keep off parallel tracks
public class RecordLayoutTests
{
    private static MarcRecord Parse(string mrk) => MrkReader.Read(mrk).Records[0];

    private const string Monograph =
        "=LDR  00766nam a22002534i 4500\n" +
        "=001  42\n" +
        "=008  260415s2017    mx            000 0 spa d\n" +
        "=100  1\\$aMoreno, Matías$eautor\n" +
        "=245  10$aGrandes proyectos$bcientíficos\n" +
        "=650  \\4$aFísica nuclear$xInvestigación\n";

    private static IReadOnlyList<BoxSpec> Row(IReadOnlyList<BoxSpec> boxes, int row) =>
        boxes.Where(b => b.Row == row).ToList();

    [Fact]
    public void Leader_is_one_wide_box_with_caret_blanks_and_the_Leader_name()
    {
        var ldr = RecordLayout.Build(Parse(Monograph))[0];

        Assert.Equal(0, ldr.Row);
        Assert.Equal(BoxPart.Leader, ldr.Part);
        Assert.Equal(-1, ldr.FieldIndex);
        Assert.Equal(-1, ldr.SubfieldIndex);
        Assert.Equal(24, ldr.MaxLength);
        Assert.Equal("Leader", ldr.Name);
        Assert.Equal("00766nam^a22002534i^4500", ldr.Text);
    }

    [Fact]
    public void Control_field_is_name_plus_tag_plus_one_wide_control_data_box()
    {
        var boxes = RecordLayout.Build(Parse(Monograph));
        var tag = boxes.Single(b => b.Part == BoxPart.Tag && b.Text == "008");
        var row = Row(boxes, tag.Row);

        Assert.Equal(new[] { BoxPart.Tag, BoxPart.ControlData }, row.Select(b => b.Part).ToArray());
        Assert.Equal("Fixed Data", tag.Name);
        Assert.Equal(3, tag.MaxLength);

        var data = row.Single(b => b.Part == BoxPart.ControlData);
        Assert.Equal(-1, data.SubfieldIndex);
        Assert.Equal(RecordLayout.Unlimited, data.MaxLength);
        Assert.Null(data.Name);
        Assert.StartsWith("260415s2017^^^^mx", data.Text); // blanks as carets
    }

    [Fact]
    public void Data_field_first_line_is_tag_ind_code_value_with_the_name_on_the_tag_box()
    {
        var boxes = RecordLayout.Build(Parse(Monograph));
        var tag = boxes.Single(b => b.Part == BoxPart.Tag && b.Text == "650");
        var row = Row(boxes, tag.Row);

        Assert.Equal(new[] { BoxPart.Tag, BoxPart.Ind, BoxPart.Code, BoxPart.Value },
            row.Select(b => b.Part).ToArray());
        Assert.Equal("Subject--Topical", tag.Name);
        Assert.All(row, b => Assert.Equal(4, b.FieldIndex)); // 001,008,100,245,650 -> index 4
        Assert.All(row, b => Assert.Equal(0, b.SubfieldIndex));

        Assert.Equal("_4", row.Single(b => b.Part == BoxPart.Ind).Text);
        Assert.Equal("a", row.Single(b => b.Part == BoxPart.Code).Text);
        Assert.Equal("Física nuclear", row.Single(b => b.Part == BoxPart.Value).Text);

        Assert.Equal(3, row.Single(b => b.Part == BoxPart.Tag).MaxLength);
        Assert.Equal(2, row.Single(b => b.Part == BoxPart.Ind).MaxLength);
        Assert.Equal(1, row.Single(b => b.Part == BoxPart.Code).MaxLength);
        Assert.Equal(RecordLayout.Unlimited, row.Single(b => b.Part == BoxPart.Value).MaxLength);
    }

    [Fact]
    public void Continuation_line_is_just_code_and_value_with_a_blank_name()
    {
        var boxes = RecordLayout.Build(Parse(Monograph));
        var tag = boxes.Single(b => b.Part == BoxPart.Tag && b.Text == "650");
        var cont = Row(boxes, tag.Row + 1);

        Assert.Equal(new[] { BoxPart.Code, BoxPart.Value }, cont.Select(b => b.Part).ToArray());
        Assert.All(cont, b => Assert.Null(b.Name));
        Assert.All(cont, b => Assert.Equal(4, b.FieldIndex));
        Assert.All(cont, b => Assert.Equal(1, b.SubfieldIndex));
        Assert.Equal("x", cont.Single(b => b.Part == BoxPart.Code).Text);
        Assert.Equal("Investigación", cont.Single(b => b.Part == BoxPart.Value).Text);
    }

    [Fact]
    public void Empty_data_field_gets_a_phantom_code_value_pair_at_subfield_minus_one()
    {
        var rec = Parse("=LDR  00000nam a2200000 i 4500\n=245  10$aX\n");
        rec.Fields.Add(new MarcField("500")); // a data field with no subfields yet

        var boxes = RecordLayout.Build(rec);
        var tag = boxes.Single(b => b.Part == BoxPart.Tag && b.Text == "500");
        var row = Row(boxes, tag.Row);

        Assert.Equal(new[] { BoxPart.Tag, BoxPart.Ind, BoxPart.Code, BoxPart.Value },
            row.Select(b => b.Part).ToArray());
        Assert.All(row, b => Assert.Equal(-1, b.SubfieldIndex)); // phantom, not a real subfield
        Assert.Equal("", row.Single(b => b.Part == BoxPart.Code).Text);
        Assert.Equal("", row.Single(b => b.Part == BoxPart.Value).Text);
    }

    [Fact]
    public void A_blank_placeholder_tag_renders_as_DATA_shape_though_the_model_calls_it_control()
    {
        var rec = Parse("=LDR  00000nam a2200000 i 4500\n");
        rec.Fields.Add(new MarcField("   ")); // the tag a freshly F6'd field carries

        Assert.True(rec.Fields[0].IsControl); // model: sorts below "010", so "control"

        var boxes = RecordLayout.Build(rec);
        var row = boxes.Where(b => b.FieldIndex == 0).ToList();

        // Data shape (tag/ind/code/value), NOT a single control-data box — this is what
        // lets the user type the tag then Tab onward with no structural rebuild.
        Assert.Equal(new[] { BoxPart.Tag, BoxPart.Ind, BoxPart.Code, BoxPart.Value },
            row.Select(b => b.Part).ToArray());
        Assert.DoesNotContain(row, b => b.Part == BoxPart.ControlData);
        Assert.Equal("   ", row.Single(b => b.Part == BoxPart.Tag).Text);
        Assert.Equal("__", row.Single(b => b.Part == BoxPart.Ind).Text); // blank indicators
    }

    [Fact]
    public void Blank_indicators_show_as_underscores_given_ones_literally()
    {
        var boxes = RecordLayout.Build(Parse(Monograph));

        string Ind(string tag) => boxes.Single(b => b.Part == BoxPart.Ind
            && boxes.Single(t => t.Row == b.Row && t.Part == BoxPart.Tag).Text == tag).Text;

        Assert.Equal("1_", Ind("100")); // 1 + blank
        Assert.Equal("10", Ind("245")); // both given
    }
}
