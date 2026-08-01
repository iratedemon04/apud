namespace Marc.Core;

/// <summary>What a heading_index row represents: the authorized form (1XX), a
/// <em>see</em> reference (4XX, an unused form that points at the authorized one),
/// or a <em>see-also</em> reference (5XX, a related authorized heading).</summary>
public enum HeadingKind
{
    Authorized,
    See,
    SeeAlso,
}

/// <summary>One heading pulled from an authority record: its kind, the tag it came
/// from, the human-readable display string, and its normalized comparison key.</summary>
public sealed record HeadingEntry(HeadingKind Kind, string Tag, string Display, string Normalized);

/// <summary>
/// Reading headings out of authority records and writing an authorized heading
/// back into a bibliographic field (Module 8). Pure Marc.Core so the authority
/// logic is unit-tested without a database or WinForms — the browse index (data
/// layer) and the Ctrl+F4 dialog (app layer) are thin callers.
/// </summary>
public static class Headings
{
    /// <summary>The bib fields that carry a controlled heading and so answer to
    /// Ctrl+F4 (docs/PLAN.md §6.3.1). Names and uniform titles, subjects, added
    /// entries and series added entries.</summary>
    private static readonly HashSet<string> ControlledBibTags = new()
    {
        "100", "110", "111", "130", "240",
        "600", "610", "611", "630", "648", "650", "651", "653", "655", "656", "657", "662",
        "700", "710", "711", "730",
        "800", "810", "811", "830",
    };

    public static bool IsControlledBibTag(string tag) => ControlledBibTags.Contains(tag);

    /// <summary>The authorized-heading field of an authority record: its single
    /// 1XX. Null for a record that has none (a malformed authority — the caller
    /// reports it rather than crashing).</summary>
    public static MarcField? AuthorizedField(MarcRecord authRecord) =>
        authRecord.Fields.FirstOrDefault(f => !f.IsControl && f.Tag.StartsWith('1'));

    /// <summary>
    /// Every browsable heading in an authority record: its 1XX (authorized), each
    /// 4XX (see) and each 5XX (see-also). Display and normalized key are computed
    /// once here so the index and the browse list always agree.
    /// </summary>
    public static IEnumerable<HeadingEntry> Extract(MarcRecord authRecord)
    {
        foreach (var f in authRecord.Fields)
        {
            if (f.IsControl) continue;
            HeadingKind? kind = f.Tag[0] switch
            {
                '1' => HeadingKind.Authorized,
                '4' => HeadingKind.See,
                '5' => HeadingKind.SeeAlso,
                _ => null,
            };
            if (kind is not HeadingKind k) continue;

            string display = HeadingText(f);
            if (display.Length == 0) continue; // nothing to browse to
            yield return new HeadingEntry(k, f.Tag, display, HeadingNormalization.Normalize(display));
        }
    }

    /// <summary>
    /// A field's heading as one readable line: its subfield values joined with a
    /// space. Deliberately plain — Apud does not invent MARC display punctuation
    /// (the "-- " between subject subdivisions, etc.); the cataloguer's own data
    /// carries whatever punctuation it carries, and the normalized key (not this
    /// string) is what browse compares against. Relator subfields ($e author,
    /// $4 aut) are left out of the heading text — they are cataloguing role, not
    /// part of the name being browsed.
    /// </summary>
    public static string HeadingText(MarcField field)
    {
        if (field.IsControl) return (field.ControlData ?? "").Trim();
        return string.Join(" ", field.Subfields
            .Where(s => !IsRelator(s.Code))
            .Select(s => s.Value.Trim())
            .Where(v => v.Length > 0));
    }

    /// <summary>
    /// Writes an authority record's authorized heading into a bibliographic field
    /// (Ctrl+F4 Enter). The auth 1XX's subfields replace the field's heading
    /// subfields and its Ind1 (the name-form indicator belongs to the heading),
    /// while the cataloguer's relator subfields ($e/$4) and the field's Ind2 (for
    /// 6XX, the thesaurus source — the bib record's own choice) are preserved.
    /// Returns false when the authority record has no 1XX to copy.
    /// </summary>
    public static bool ApplyAuthorizedHeading(MarcField target, MarcRecord authRecord)
    {
        var source = AuthorizedField(authRecord);
        if (source is null || target.IsControl) return false;

        var relators = target.Subfields.Where(s => IsRelator(s.Code)).ToList();

        target.Ind1 = source.Ind1;
        target.Subfields.Clear();
        foreach (var s in source.Subfields)
            if (!IsRelator(s.Code))
                target.Subfields.Add(new MarcSubfield(s.Code, s.Value));
        target.Subfields.AddRange(relators); // the cataloguer's role subfields ride along, at the end
        return true;
    }

    private static bool IsRelator(char code) => code is 'e' or '4';
}
