using Marc.Core.FixedFields;

namespace Marc.Core.Validation;

/// <summary>
/// The record-only half of the Ctrl+L / Ctrl+W pipeline (docs/PLAN.md §8 stages
/// 1-3): everything that can be judged from the MARC record itself, with no
/// database. Pure Marc.Core so the whole error corpus is unit-tested without a
/// catalogue or WinForms; the authority stage (which needs stored links) and the
/// auto-fill/push (which needs the base) live in Apud.Data's PushService and run
/// after these.
///
/// Faithful-but-dumb ethos (Decisions): validation REPORTS, it never rewrites.
/// Fixed-field bytes never block a push (they are warnings) because real
/// catalogues legitimately put brackets and fill characters into coded slots.
/// </summary>
public static class RecordValidator
{
    public static List<ValidationFinding> Validate(MarcRecord record, string @base, ValidationProfile profile)
    {
        var findings = new List<ValidationFinding>();
        Structural(record, findings);
        FixedFields(record, findings);
        Profile(record, @base, profile, findings);
        return findings;
    }

    // ---------- stage 1: structural ----------

    private static void Structural(MarcRecord record, List<ValidationFinding> f)
    {
        for (int i = 0; i < record.Fields.Count; i++)
        {
            var field = record.Fields[i];
            var here = FieldRef.Field(i);

            if (!IsValidTag(field.Tag))
            {
                f.Add(new(Severity.Error, here, "tag.invalid",
                    $"'{field.Tag.Trim()}' is not a valid MARC tag — a tag is three digits."));
                continue; // nothing else about a tagless field is meaningful
            }

            if (field.IsControl)
            {
                // 001/003/005 are derived at push (Decisions: 001 kept-or-computed,
                // 005/003 mechanical) — an empty one is normal and gets filled, not flagged.
                if (string.IsNullOrEmpty(field.ControlData) && field.Tag is not ("001" or "003" or "005"))
                    f.Add(new(Severity.Error, here, "control.empty",
                        $"Control field {field.Tag} is empty."));
                continue;
            }

            if (!IsIndicator(field.Ind1) || !IsIndicator(field.Ind2))
                f.Add(new(Severity.Error, here, "indicator.invalid",
                    $"Field {field.Tag} has an invalid indicator (each must be a digit or blank)."));

            if (field.Subfields.Count == 0)
            {
                f.Add(new(Severity.Error, here, "field.no-subfields",
                    $"Field {field.Tag} has no subfields."));
                continue;
            }

            for (int s = 0; s < field.Subfields.Count; s++)
            {
                var sf = field.Subfields[s];
                var at = FieldRef.Subfield(i, s);
                if (!IsSubfieldCode(sf.Code))
                    f.Add(new(Severity.Error, at, "subfield.code",
                        $"Field {field.Tag} has an invalid subfield code '{sf.Code}' (a-z or 0-9). " +
                        "A stray delimiter — e.g. $Preciado for $aPreciado — reads as this."));
                else if (sf.Value.Length == 0)
                    f.Add(new(Severity.Error, at, "subfield.empty",
                        $"Field {field.Tag} subfield ‡{sf.Code} is empty."));
            }
        }
    }

    // ---------- stage 2: fixed fields ----------

    private static void FixedFields(MarcRecord record, List<ValidationFinding> f)
    {
        if (FixedFieldLayouts.Leader(record.Leader) is { } ldr)
            CheckCoded(ldr, record.Leader, FieldRef.Leader, f);

        for (int i = 0; i < record.Fields.Count; i++)
        {
            var field = record.Fields[i];
            if (field.Tag != "008") continue;
            var here = FieldRef.Field(i);
            string data = field.ControlData ?? "";

            var layout = FixedFieldLayouts.For008(record.Leader);
            if (layout is null) continue;

            if (data.Length != layout.Length)
                f.Add(new(Severity.Error, here, "008.length",
                    $"008 must be {layout.Length} characters (got {data.Length})."));

            CheckCoded(layout, data, here, f);
        }
    }

    /// <summary>Coded positions (a <c>values</c> map) whose bytes are not a listed
    /// code → a WARNING (never blocks: brackets, fill '|', or a code we do not
    /// enumerate are all legitimate in real data). Blank and fill are skipped.</summary>
    private static void CheckCoded(FixedFieldLayout layout, string data, FieldRef @ref, List<ValidationFinding> f)
    {
        var fixedData = new FixedFieldData(layout, data);
        foreach (var p in layout.Positions)
        {
            if (p.Values is null || p.Protected) continue;
            string slice = fixedData.Slice(p);
            if (IsBlankOrFill(slice)) continue;
            if (!p.Values.ContainsKey(slice))
                f.Add(new(Severity.Warning, @ref, "fixed.code",
                    $"{layout.Field}/{p.Label}: '{slice}' is not a recognised code."));
        }
    }

    // ---------- stage 3: profile ----------

    private static void Profile(MarcRecord record, string @base, ValidationProfile profile, List<ValidationFinding> f)
    {
        foreach (var group in profile.Mandatory)
            if (!group.Any(tag => record.Fields.Any(x => x.Tag == tag)))
                f.Add(new(Severity.Error, null, "profile.mandatory",
                    group.Length == 1
                        ? $"Mandatory field {group[0]} is missing."
                        : $"One of {string.Join("/", group)} is required and none is present."));

        var seen = new HashSet<string>();
        for (int i = 0; i < record.Fields.Count; i++)
        {
            var field = record.Fields[i];
            if (profile.NonRepeatable.Contains(field.Tag) && !seen.Add(field.Tag))
                f.Add(new(Severity.Error, FieldRef.Field(i), "profile.repeat",
                    $"Field {field.Tag} may not be repeated."));

            if (!field.IsControl && profile.RequiredSubfields.TryGetValue(field.Tag, out var codes))
                foreach (var code in codes)
                    if (field.Subfields.All(s => s.Code != code))
                        f.Add(new(Severity.Error, FieldRef.Field(i), "profile.subfield",
                            $"Field {field.Tag} requires subfield ‡{code}."));
        }

        if (profile.SingleHeading1xx)
        {
            var oneXx = record.Fields
                .Select((x, i) => (x, i))
                .Where(t => !t.x.IsControl && t.x.Tag.StartsWith('1'))
                .ToList();
            for (int k = 1; k < oneXx.Count; k++)
                f.Add(new(Severity.Error, FieldRef.Field(oneXx[k].i), "profile.single-1xx",
                    "An authority record has exactly one 1XX heading; this is an extra one."));
        }
    }

    // ---------- predicates ----------

    private static bool IsValidTag(string tag) =>
        tag.Length == 3 && tag[0] is >= '0' and <= '9'
                        && tag[1] is >= '0' and <= '9'
                        && tag[2] is >= '0' and <= '9';

    private static bool IsIndicator(char c) => c == ' ' || c is >= '0' and <= '9';

    private static bool IsSubfieldCode(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool IsBlankOrFill(string slice) =>
        slice.All(c => c is ' ' or '|');
}
