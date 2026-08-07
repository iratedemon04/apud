using System.Text;
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
///
/// Undo/redo (user, 2026-07-31): every mutation runs through <see cref="Apply"/>,
/// which snapshots the record before the change and keeps the snapshot only if
/// something actually changed — so Ctrl+Z reverts anything and Ctrl+Y reapplies
/// it, with no per-operation inverse logic to get wrong. Records are a few dozen
/// fields, so whole-record snapshots are the simplest correct design.
/// </summary>
public sealed class EditorDocument
{
    public StoredRecord Stored { get; }
    public MarcRecord Record => Stored.Record;

    private readonly Stack<Memento> _undo = new();
    private readonly Stack<Memento> _redo = new();

    /// <summary>Signature of the last saved state; null means "never saved"
    /// (a brand-new or copied record is dirty until its first save).</summary>
    private string? _savedSignature;

    public EditorDocument(StoredRecord stored, bool dirty = false)
    {
        Stored = stored;
        _savedSignature = dirty ? null : Sign();
    }

    /// <summary>True when the record differs from what the catalogue holds.
    /// Undoing back to the saved state clears it; branching away sets it again.</summary>
    public bool Dirty => _savedSignature is null || Sign() != _savedSignature;

    public void MarkSaved() => _savedSignature = Sign();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    // ---------- cell edits ----------

    /// <summary>Sets the leader; returns an error line when the text is not 24
    /// characters (the one structural rule the model enforces).</summary>
    public string? SetLeader(string text)
    {
        text = Uncaret(text);
        if (text.Length != MarcConstants.LeaderLength)
            return $"Leader must be exactly {MarcConstants.LeaderLength} characters (got {text.Length}) — not changed.";
        Apply(() => Record.Leader = text);
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
        var old = Record.Fields[fieldIndex];
        if (text == old.Tag) return null; // unchanged (e.g. tabbing past an untyped blank field) — silent
        text = text.Trim();
        if (text.Length != 3) return "A tag is exactly 3 characters — not changed.";
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

        Apply(() => Record.Fields[fieldIndex] = replacement);
        return null;
    }

    public void SetIndicators(int fieldIndex, string text)
    {
        var f = Record.Fields[fieldIndex];
        if (f.IsControl) return;
        string ind = (text.Replace("_", " ") + "  ")[..2];
        Apply(() => { f.Ind1 = ind[0]; f.Ind2 = ind[1]; });
    }

    public void SetControlData(int fieldIndex, string text)
    {
        var f = Record.Fields[fieldIndex];
        string value = Uncaret(text);
        Apply(() => f.ControlData = value);
    }

    /// <summary>First character of <paramref name="text"/> becomes the code; on
    /// an empty data field (subfieldIndex -1) this creates the subfield.</summary>
    public void SetSubfieldCode(int fieldIndex, int subfieldIndex, string text)
    {
        var f = Record.Fields[fieldIndex];
        if (f.IsControl) return;
        char code = text.Length > 0 ? text[0] : ' ';
        Apply(() =>
        {
            if (subfieldIndex < 0) f.Subfields.Add(new MarcSubfield(code, ""));
            else f.Subfields[subfieldIndex].Code = code;
        });
    }

    /// <summary>On an empty data field (subfieldIndex -1) typing a value
    /// creates the subfield as ‡a.</summary>
    public void SetSubfieldValue(int fieldIndex, int subfieldIndex, string text)
    {
        var f = Record.Fields[fieldIndex];
        if (f.IsControl) { SetControlData(fieldIndex, text); return; }
        Apply(() =>
        {
            if (subfieldIndex < 0)
            {
                if (text.Length > 0) f.Subfields.Add(new MarcSubfield('a', text));
            }
            else
            {
                f.Subfields[subfieldIndex].Value = text;
            }
        });
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
        Apply(() => Record.Fields.Insert(at, new MarcField("   ")));
        return at;
    }

    /// <summary>F7: inserts a blank ‡a after the given subfield (-1 = at the
    /// top) and returns its index; error line on a control field.</summary>
    public (int Index, string? Error) InsertSubfieldAfter(int fieldIndex, int subfieldIndex)
    {
        var f = Record.Fields[fieldIndex];
        if (f.IsControl) return (-1, $"{f.Tag} is a control field — it has no subfields.");
        int at = Math.Min(subfieldIndex + 1, f.Subfields.Count);
        Apply(() => f.Subfields.Insert(at, new MarcSubfield('a', "")));
        return (at, null);
    }

    public void DeleteField(int fieldIndex) =>
        Apply(() => Record.Fields.RemoveAt(fieldIndex));

    /// <summary>
    /// Removes every field with no content — a data field whose subfields are all
    /// empty (whatever their codes or indicators) or that has no subfields at all,
    /// and a control field with no data. The leader is never a field, so it stays.
    /// Run at validate/push so a stray blank field neither blocks the push nor
    /// ships (user, task 17). Undoable in one step; returns how many were removed.
    /// </summary>
    public int StripEmptyFields()
    {
        var doomed = new List<int>();
        for (int i = 0; i < Record.Fields.Count; i++)
            if (!HasContent(Record.Fields[i])) doomed.Add(i);
        if (doomed.Count == 0) return 0;
        Apply(() => { for (int k = doomed.Count - 1; k >= 0; k--) Record.Fields.RemoveAt(doomed[k]); });
        return doomed.Count;
    }

    /// <summary>
    /// Enter (in the editor) orders the fields: a STABLE sort by tag, so repeated
    /// tags (three 650s, two 500s) keep exactly the order the cataloguer wrote
    /// them — subject order is real information. The same ordering Ctrl+L applies
    /// at push, offered here on demand (user request 2026-08-02). Undoable; the
    /// field objects themselves are reordered in place, so a caller can follow a
    /// field to its new position by reference. Returns true when something moved.
    /// </summary>
    public bool OrderFields()
    {
        string beforeSig = Sign();
        var ordered = Record.Fields
            .OrderBy(f => f.Tag, StringComparer.Ordinal) // LINQ OrderBy is a stable sort
            .ToList();
        Apply(() =>
        {
            Record.Fields.Clear();
            Record.Fields.AddRange(ordered);
        });
        return Sign() != beforeSig;
    }

    /// <summary>Deletes several fields in one undoable step (removed high-index
    /// first so the earlier indices stay valid). The leader (-1) is ignored.
    /// Used by "delete selected fields" when cleaning up a pasted-in record.</summary>
    public void DeleteFields(IEnumerable<int> fieldIndices)
    {
        var ordered = fieldIndices.Where(i => i >= 0).Distinct().OrderByDescending(i => i).ToList();
        if (ordered.Count == 0) return;
        Apply(() => { foreach (int i in ordered) Record.Fields.RemoveAt(i); });
    }

    /// <summary>Deletes one subfield; the field itself stays even at zero
    /// subfields (Ctrl+F5 is how a field goes away).</summary>
    public void DeleteSubfield(int fieldIndex, int subfieldIndex)
    {
        var f = Record.Fields[fieldIndex];
        if (f.IsControl || subfieldIndex < 0) return;
        Apply(() => f.Subfields.RemoveAt(subfieldIndex));
    }

    // ---------- copy / paste field & subfield (user request 2026-08-01) ----------

    /// <summary>A deep, independent copy of a field for the clipboard (read-only —
    /// no mutation, nothing on the undo stack). The leader is not a field and
    /// cannot be copied here.</summary>
    public MarcField CopyField(int fieldIndex) => CloneField(Record.Fields[fieldIndex]);

    /// <summary>Inserts a fresh clone of a copied field after the given one
    /// (-1 = at the top) and returns its index. Cloned on the way in, so pasting
    /// the same clipboard field twice yields two independent fields.</summary>
    public int PasteFieldAfter(int fieldIndex, MarcField field)
    {
        int at = Math.Min(fieldIndex + 1, Record.Fields.Count);
        Apply(() => Record.Fields.Insert(at, CloneField(field)));
        return at;
    }

    /// <summary>A deep, independent copy of a subfield for the clipboard.</summary>
    public MarcSubfield CopySubfield(int fieldIndex, int subfieldIndex)
    {
        var s = Record.Fields[fieldIndex].Subfields[subfieldIndex];
        return new MarcSubfield(s.Code, s.Value);
    }

    /// <summary>Inserts a clone of a copied subfield after the given one (-1 = at
    /// the top) and returns its index; error line on a control field.</summary>
    public (int Index, string? Error) PasteSubfieldAfter(int fieldIndex, int subfieldIndex, MarcSubfield subfield)
    {
        var f = Record.Fields[fieldIndex];
        if (f.IsControl) return (-1, $"{f.Tag} is a control field — it has no subfields.");
        int at = Math.Min(subfieldIndex + 1, f.Subfields.Count);
        Apply(() => f.Subfields.Insert(at, new MarcSubfield(subfield.Code, subfield.Value)));
        return (at, null);
    }

    // ---------- undo / redo ----------

    /// <summary>Reverts the last change that actually altered the record.</summary>
    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Push(Capture());
        Restore(_undo.Pop());
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Push(Capture());
        Restore(_redo.Pop());
        return true;
    }

    /// <summary>Runs a mutation, keeping an undo snapshot only if it changed the
    /// record. Redo history is discarded the moment a new change lands.</summary>
    private void Apply(Action mutate)
    {
        var before = Capture();
        string beforeSig = Sign();
        mutate();
        if (Sign() != beforeSig)
        {
            _undo.Push(before);
            _redo.Clear();
        }
    }

    // ---------- authority linking (Ctrl+F4, Module 8) ----------

    /// <summary>
    /// Links a controlled field to an authority record: rewrites the field to the
    /// authorized heading (relators preserved, <see cref="Headings.ApplyAuthorizedHeading"/>)
    /// and stores the link. Routed through <see cref="Apply"/> so Ctrl+Z reverts
    /// both the heading text and the link in one step. Returns false when the
    /// authority record has no 1XX to copy.
    /// </summary>
    public bool LinkAuthority(int fieldIndex, long authRecordId, MarcRecord authRecord)
    {
        var field = Record.Fields[fieldIndex];
        bool applied = false;
        Apply(() =>
        {
            if (Headings.ApplyAuthorizedHeading(field, authRecord))
            {
                field.AuthLinkId = authRecordId;
                applied = true;
            }
        });
        return applied;
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
            copy.Fields.Add(CloneField(f));
        }
        return copy;
    }

    // ---------- snapshots ----------

    private sealed record Memento(string Leader, List<MarcField> Fields);

    private Memento Capture() => new(Record.Leader, Record.Fields.Select(CloneField).ToList());

    private void Restore(Memento m)
    {
        Record.Leader = m.Leader;
        Record.Fields.Clear();
        // Clone on the way out too, so the memento can be reused (redo) intact.
        Record.Fields.AddRange(m.Fields.Select(CloneField));
    }

    private static MarcField CloneField(MarcField f)
    {
        var c = new MarcField(f.Tag)
        {
            ControlData = f.ControlData,
            Ind1 = f.Ind1,
            Ind2 = f.Ind2,
            AuthLinkId = f.AuthLinkId,
        };
        c.Subfields.AddRange(f.Subfields.Select(s => new MarcSubfield(s.Code, s.Value)));
        return c;
    }

    /// <summary>A stable string that changes iff the record's content changes —
    /// used both for change detection and for the saved/dirty comparison. The
    /// separators are ASCII control codes that cannot occur in MARC data.</summary>
    private string Sign()
    {
        const char unit = '';   // between the parts of one field
        const char group = '';  // between fields
        var sb = new StringBuilder();
        sb.Append(Record.Leader).Append(group);
        foreach (var f in Record.Fields)
        {
            sb.Append(f.Tag).Append(f.Ind1).Append(f.Ind2).Append(unit)
              .Append(f.ControlData ?? "").Append(unit)
              .Append(f.AuthLinkId?.ToString() ?? "").Append(unit);
            foreach (var s in f.Subfields)
                sb.Append(unit).Append(s.Code).Append(s.Value);
            sb.Append(group);
        }
        return sb.ToString();
    }

    // ---------- helpers ----------

    private static bool HasContent(MarcField f) =>
        !string.IsNullOrEmpty(f.ControlData) || f.Subfields.Any(s => s.Value.Length > 0);

    private static string Uncaret(string s) => s.Replace('^', ' ');
}
