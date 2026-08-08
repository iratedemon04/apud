using Marc.Core;

namespace Apud.App;

/// <summary>Which kind of box a <see cref="BoxSpec"/> is — drives the view's
/// column placement, font, and commit target.</summary>
public enum BoxPart { Leader, Tag, Ind, Code, Value, ControlData }

/// <summary>
/// One editable box in the record editor's textbox grid. A pure, WinForms-free
/// description of a control: which model element it edits, its text, its length
/// rule, and (on the row's first box only) the maroon field-name label to its left.
///
/// Index convention (shared with the editor/validator): <see cref="FieldIndex"/>
/// -1 = the leader row; <see cref="SubfieldIndex"/> -1 = a box with no subfield
/// (leader, control data, or the phantom code/value pair of an empty data field).
/// <see cref="MaxLength"/> 0 = unlimited.
/// </summary>
public sealed record BoxSpec(
    int Row,
    BoxPart Part,
    int FieldIndex,
    int SubfieldIndex,
    string Text,
    int MaxLength,
    bool ReadOnly,
    string? Name);

/// <summary>
/// Turns a <see cref="MarcRecord"/> into a flat, ordered list of <see cref="BoxSpec"/>
/// — one box per element, grouped into rows by <see cref="BoxSpec.Row"/>, one row per
/// subfield line. This is the editor's replacement for <see cref="RecordDisplay.Build"/>:
/// same Aleph layout (name | tag | ind | code | value), same blank conventions
/// ('^' in leader/control data, '_' in indicators), but expressed as real-textbox
/// specs the <c>RecordGrid</c> view materializes. Pure, so every shaping rule
/// (control vs data vs blank-as-data, the phantom subfield, continuation lines,
/// lengths) is unit-tested without a GUI.
/// </summary>
public static class RecordLayout
{
    /// <summary>The leader is always exactly this many characters.</summary>
    public const int LeaderLength = MarcConstants.LeaderLength;

    /// <summary>Sentinel <see cref="BoxSpec.MaxLength"/> for value/control-data boxes.</summary>
    public const int Unlimited = 0;

    public static IReadOnlyList<BoxSpec> Build(MarcRecord record)
    {
        var boxes = new List<BoxSpec>();
        int row = 0;

        // ----- Leader: one wide box, blanks as carets, name "Leader". -----
        boxes.Add(new BoxSpec(row++, BoxPart.Leader, -1, -1,
            Caret(record.Leader), LeaderLength, ReadOnly: false, Name: TagNames.For("LDR")));

        for (int fi = 0; fi < record.Fields.Count; fi++)
        {
            var f = record.Fields[fi];
            string name = TagNames.For(f.Tag);

            // A control field (00X) is name + tag + one wide control-data box — UNLESS
            // its tag is the blank placeholder "   " a just-inserted field carries.
            // That tag is control per MarcField.IsControl (it sorts below "010"), but it
            // must render as a DATA field so F6 -> type the tag -> Tab flows with no
            // rebuild. The data/control decision therefore lives HERE, not in the model.
            if (f.IsControl && !IsBlankTag(f.Tag))
            {
                boxes.Add(new BoxSpec(row, BoxPart.Tag, fi, -1, f.Tag, 3, ReadOnly: false, Name: name));
                boxes.Add(new BoxSpec(row, BoxPart.ControlData, fi, -1,
                    Caret(f.ControlData ?? ""), Unlimited, ReadOnly: false, Name: null));
                row++;
                continue;
            }

            string ind = Indicators(f);

            // Empty data field (no subfields yet) — including a brand-new blank field:
            // tag + ind + a phantom empty code/value pair (SubfieldIndex -1) so the whole
            // field is typed through in place with no structural rebuild until a real
            // subfield or a control tag appears.
            if (f.Subfields.Count == 0)
            {
                boxes.Add(new BoxSpec(row, BoxPart.Tag, fi, -1, f.Tag, 3, ReadOnly: false, Name: name));
                boxes.Add(new BoxSpec(row, BoxPart.Ind, fi, -1, ind, 2, ReadOnly: false, Name: null));
                boxes.Add(new BoxSpec(row, BoxPart.Code, fi, -1, "", 1, ReadOnly: false, Name: null));
                boxes.Add(new BoxSpec(row, BoxPart.Value, fi, -1, "", Unlimited, ReadOnly: false, Name: null));
                row++;
                continue;
            }

            // Data field: first subfield line carries name + tag + ind + code + value;
            // each continuation line is a blank name/tag/ind + code + value.
            for (int si = 0; si < f.Subfields.Count; si++)
            {
                var s = f.Subfields[si];
                if (si == 0)
                {
                    boxes.Add(new BoxSpec(row, BoxPart.Tag, fi, si, f.Tag, 3, ReadOnly: false, Name: name));
                    boxes.Add(new BoxSpec(row, BoxPart.Ind, fi, si, ind, 2, ReadOnly: false, Name: null));
                    boxes.Add(new BoxSpec(row, BoxPart.Code, fi, si, s.Code.ToString(), 1, ReadOnly: false, Name: null));
                    boxes.Add(new BoxSpec(row, BoxPart.Value, fi, si, s.Value, Unlimited, ReadOnly: false, Name: null));
                }
                else
                {
                    boxes.Add(new BoxSpec(row, BoxPart.Code, fi, si, s.Code.ToString(), 1, ReadOnly: false, Name: null));
                    boxes.Add(new BoxSpec(row, BoxPart.Value, fi, si, s.Value, Unlimited, ReadOnly: false, Name: null));
                }
                row++;
            }
        }

        return boxes;
    }

    /// <summary>The placeholder tag a freshly inserted field carries — all spaces
    /// (MarcField enforces a 3-char tag, so this is "   ").</summary>
    private static bool IsBlankTag(string tag) => string.IsNullOrWhiteSpace(tag);

    /// <summary>Indicators as displayed: a blank (space) indicator shows as '_'.</summary>
    private static string Indicators(MarcField f) =>
        new(new[] { f.Ind1 == ' ' ? '_' : f.Ind1, f.Ind2 == ' ' ? '_' : f.Ind2 });

    /// <summary>Blanks in the leader and control data show as '^' (same as RecordDisplay).</summary>
    private static string Caret(string s) => s.Replace(' ', '^');
}
