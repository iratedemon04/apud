using Marc.Core;
using Apud.Data;

namespace Apud.App;

/// <summary>
/// One open record and its editing operations (Module 6 step 3) — headless, so
/// every operation is testable without a grid. The editor is dumb by decree
/// (user, 2026-07-28): any tag may be typed, nothing is validated at entry
/// time, and fields are NEVER reordered here — ordering is Ctrl+L's job
/// (Module 9). Indices follow DisplayRow's convention: FieldIndex -1 = leader,
/// SubfieldIndex -1 = no subfield on that row.
///
/// Display text conventions are undone on the way in: '^' means blank in
/// LDR/control data, '_' means blank in indicators.
/// </summary>
public sealed class EditorDocument
{
    public StoredRecord Stored { get; }
    public MarcRecord Record => Stored.Record;

    /// <summary>True when the document differs from what the catalogue holds
    /// (including never having been saved at all).</summary>
    public bool Dirty { get; private set; }

    public EditorDocument(StoredRecord stored, bool dirty = false)
    {
        Stored = stored;
        Dirty = dirty;
    }

    public void MarkSaved() => Dirty = false;

    // ---------- cell edits ----------

    /// <summary>Sets the leader; returns an error line when the text is not 24
    /// characters (the one structural rule the model enforces).</summary>
    public string? SetLeader(string text)
    {
        text = Uncaret(text);
        if (text.Length != MarcConstants.LeaderLength)
            return $"Leader must be exactly {MarcConstants.LeaderLength} characters (got {text.Length}) — not changed.";
        if (Record.Leader != text) { Record.Leader = text; Dirty = true; }
        return null;
    }

    /// <summary>
    /// Retags a field. Any 3-character tag is accepted (judging tags is the
    /// Module 9 validator's job). Crossing the control/data boundary (00X ↔ the
    /// rest) is only possible while the field is empty — the two shapes share no
    /// content to carry over; a converted empty field gets Aleph's starting
    /// shape (data: one blank ‡a). Returns an error line when refused.
    /// </summary>
    public string? SetTag(int fieldIndex, string text)
    {
        text = text.Trim();
        if (text.Length != 3) return "A tag is exactly 3 characters — not changed.";
        var old = Record.Fields[fieldIndex];
        if (old.Tag == text) return null;

        var replacement = new MarcField(text) { AuthLinkId = old.AuthLinkId };
        if (replacement.IsControl == old.IsControl)
        {
            replacement.ControlData = old.ControlData;
            replacement.Ind1 = old.Ind1;
            replacement.Ind2 = old.Ind2;
            replacement.Subfields.AddRange(old.Subfields);
        }
        else if (HasContent(old))
        {
            return $"{old.Tag} → {text} crosses the control/data boundary — empty the field first, or delete it and add a new one.";
        }
        else if (!replacement.IsControl)
        {
            replacement.Subfields.Add(new MarcSubfield('a', ""));
        }

        Record.Fields[fieldIndex] = replacement;
        Dirty = true;
        return null;
    }

    public void SetIndicators(int fieldIndex, string text)
    {
        var f = Record.Fields[fieldIndex];
        if (f.IsControl) return;
        string ind = (text.Replace("_", " ") + "  ")[..2];
        if (f.Ind1 == ind[0] && f.Ind2 == ind[1]) return;
        f.Ind1 = ind[0];
        f.Ind2 = ind[1];
        Dirty = true;
    }

    public void SetControlData(int fieldIndex, string text)
    {
        var f = Record.Fields[fieldIndex];
        text = Uncaret(text);
        if (f.ControlData == text) return;
        f.ControlData = text;
        Dirty = true;
    }

    /// <summary>First character of <paramref name="text"/> becomes the code; on
    /// an empty data field (subfieldIndex -1) this creates the subfield.</summary>
    public void SetSubfieldCode(int fieldIndex, int subfieldIndex, string text)
    {
        var f = Record.Fields[fieldIndex];
        if (f.IsControl) return;
        char code = text.Length > 0 ? text[0] : ' ';
        if (subfieldIndex < 0)
        {
            f.Subfields.Add(new MarcSubfield(code, ""));
        }
        else
        {
            if (f.Subfields[subfieldIndex].Code == code) return;
            f.Subfields[subfieldIndex].Code = code;
        }
        Dirty = true;
    }

    /// <summary>On an empty data field (subfieldIndex -1) typing a value
    /// creates the subfield as ‡a.</summary>
    public void SetSubfieldValue(int fieldIndex, int subfieldIndex, string text)
    {
        var f = Record.Fields[fieldIndex];
        if (f.IsControl) { SetControlData(fieldIndex, text); return; }
        if (subfieldIndex < 0)
        {
            if (text.Length == 0) return;
            f.Subfields.Add(new MarcSubfield('a', text));
        }
        else
        {
            if (f.Subfields[subfieldIndex].Value == text) return;
            f.Subfields[subfieldIndex].Value = text;
        }
        Dirty = true;
    }

    // ---------- structure ----------

    /// <summary>
    /// F6: inserts a blank field after the given one (-1 = after the leader,
    /// i.e. at the top) and returns its index. Blank means blank — the tag is
    /// three spaces until the cataloguer types one; nothing is suggested.
    /// </summary>
    public int InsertBlankFieldAfter(int fieldIndex)
    {
        int at = Math.Min(fieldIndex + 1, Record.Fields.Count);
        Record.Fields.Insert(at, new MarcField("   "));
        Dirty = true;
        return at;
    }

    /// <summary>F7: inserts a blank ‡a after the given subfield (-1 = at the
    /// top) and returns its index; error line on a control field.</summary>
    public (int Index, string? Error) InsertSubfieldAfter(int fieldIndex, int subfieldIndex)
    {
        var f = Record.Fields[fieldIndex];
        if (f.IsControl) return (-1, $"{f.Tag} is a control field — it has no subfields.");
        int at = Math.Min(subfieldIndex + 1, f.Subfields.Count);
        f.Subfields.Insert(at, new MarcSubfield('a', ""));
        Dirty = true;
        return (at, null);
    }

    public void DeleteField(int fieldIndex)
    {
        Record.Fields.RemoveAt(fieldIndex);
        Dirty = true;
    }

    /// <summary>Deletes one subfield; the field itself stays even at zero
    /// subfields (Ctrl+F5 is how a field goes away).</summary>
    public void DeleteSubfield(int fieldIndex, int subfieldIndex)
    {
        var f = Record.Fields[fieldIndex];
        if (f.IsControl || subfieldIndex < 0) return;
        f.Subfields.RemoveAt(subfieldIndex);
        Dirty = true;
    }

    // ---------- copies (Ctrl+N in a record) ----------

    /// <summary>
    /// Deep copy without any 001 (§6.2: copy-as-draft clears the control
    /// number; the sequence fills it at push). Authority links travel with
    /// their fields.
    /// </summary>
    public static MarcRecord CopyWithout001(MarcRecord source)
    {
        var copy = new MarcRecord { Leader = source.Leader };
        foreach (var f in source.Fields)
        {
            if (f.Tag == "001") continue;
            var c = new MarcField(f.Tag)
            {
                ControlData = f.ControlData,
                Ind1 = f.Ind1,
                Ind2 = f.Ind2,
                AuthLinkId = f.AuthLinkId,
            };
            c.Subfields.AddRange(f.Subfields.Select(s => new MarcSubfield(s.Code, s.Value)));
            copy.Fields.Add(c);
        }
        return copy;
    }

    // ---------- helpers ----------

    private static bool HasContent(MarcField f) =>
        !string.IsNullOrEmpty(f.ControlData) || f.Subfields.Any(s => s.Value.Length > 0);

    private static string Uncaret(string s) => s.Replace('^', ' ');
}
