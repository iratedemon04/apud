using Marc.Core;

namespace Apud.App;

/// <summary>One grid row of the record viewer. Data-field continuation rows
/// (second and later subfields) carry only Code + Value.</summary>
public sealed record DisplayRow(string FieldName, string Tag, string Indicators, string Code, string Value);

/// <summary>
/// Turns a MarcRecord into viewer rows, following the user's Aleph layout
/// (docs/ALEPH-WORKFLOW.md): one row per subfield with the code in its own
/// column — never '$' notation; blanks shown as '^' in LDR/control-field data
/// and as '_' in indicators. Values are displayed literally (a real dollar sign
/// is just a dollar sign; "{dollar}" is .mrk file syntax, not screen syntax).
/// The Module 6 editor reuses this shape.
/// </summary>
public static class RecordDisplay
{
    public static IReadOnlyList<DisplayRow> Build(MarcRecord record)
    {
        var rows = new List<DisplayRow>
        {
            new(TagNames.For("LDR"), "LDR", "", "", Caret(record.Leader)),
        };

        foreach (var f in record.Fields)
        {
            if (f.IsControl)
            {
                rows.Add(new DisplayRow(TagNames.For(f.Tag), f.Tag, "", "", Caret(f.ControlData ?? "")));
                continue;
            }

            string ind = new(new[]
            {
                f.Ind1 == ' ' ? '_' : f.Ind1,
                f.Ind2 == ' ' ? '_' : f.Ind2,
            });

            if (f.Subfields.Count == 0)
            {
                rows.Add(new DisplayRow(TagNames.For(f.Tag), f.Tag, ind, "", ""));
                continue;
            }

            for (int i = 0; i < f.Subfields.Count; i++)
            {
                var s = f.Subfields[i];
                rows.Add(i == 0
                    ? new DisplayRow(TagNames.For(f.Tag), f.Tag, ind, s.Code.ToString(), s.Value)
                    : new DisplayRow("", "", "", s.Code.ToString(), s.Value));
            }
        }

        return rows;
    }

    /// <summary>
    /// The always-visible header line above the viewer: base, 001, and the
    /// record's heading (245 for bib, 1XX for authority) — Aleph's red bar.
    /// </summary>
    public static string HeaderText(string @base, string? controlNumber, MarcRecord record)
    {
        string heading = record.Kind == RecordKind.Authority
            ? FirstValue(record, "100", "110", "111", "130", "150", "151")
            : FirstValue(record, "245");
        string number = controlNumber is null ? "(no 001)" : controlNumber;
        return $"{@base} {number}  {heading}".TrimEnd();
    }

    private static string FirstValue(MarcRecord record, params string[] tags)
    {
        foreach (var tag in tags)
        {
            var field = record.FieldsWithTag(tag).FirstOrDefault();
            if (field?.Subfields.Count > 0)
                return string.Join(" ", field.Subfields.Select(s => s.Value));
        }
        return "";
    }

    private static string Caret(string s) => s.Replace(' ', '^');
}
