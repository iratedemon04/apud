using Marc.Core;
using Marc.Core.Validation;

namespace Apud.Data;

/// <summary>Outcome of a validate/push. <see cref="Ok"/> false means errors
/// blocked the push and nothing was written; true means the record is pushed and
/// <see cref="Findings"/> holds any (non-blocking) warnings.</summary>
public sealed record PushResult(
    bool Ok, IReadOnlyList<ValidationFinding> Findings, string? ControlNumber, int RippledFields)
{
    public IEnumerable<ValidationFinding> Errors => Findings.Where(f => f.IsError);
    public IEnumerable<ValidationFinding> Warnings => Findings.Where(f => !f.IsError);
    public bool HasErrors => Findings.Any(f => f.IsError);
}

/// <summary>
/// The Ctrl+L / Ctrl+W pipeline's database-bound half (docs/PLAN.md §8): it runs
/// the record-only stages (RecordValidator, Marc.Core), then the two stages that
/// need the catalogue — authority-link verification and the duplicate-001 check —
/// and, for a clean record, derives the mechanical data and pushes it in one
/// Update. The record-only stages stay in Marc.Core so the whole error corpus is
/// tested there; this class adds only what genuinely needs the base.
/// </summary>
public sealed class PushService
{
    private readonly RecordRepository _repo;

    public PushService(RecordRepository repo) => _repo = repo;

    /// <summary>Ctrl+W: validate only, writing nothing. Every stage runs so the
    /// cataloguer sees warnings as well as the errors that would block a push.</summary>
    public List<ValidationFinding> Check(StoredRecord rec, ValidationProfile profile)
    {
        var findings = RecordValidator.Validate(rec.Record, rec.Base, profile);
        AuthorityStage(rec, findings);
        DuplicateControlNumberStage(rec, findings);
        return findings;
    }

    /// <summary>
    /// Ctrl+L: validate, then — only if nothing errored — derive the mechanical
    /// data (001/005, the 003 org code when set, stable field order, leader
    /// length/base address), promote
    /// the record to pushed, and write it. Pushing an authority record ripples its
    /// heading into every linked bib (§6.3.7). On any error nothing is written and
    /// <see cref="PushResult.Ok"/> is false.
    /// </summary>
    public PushResult Push(StoredRecord rec, ValidationProfile profile)
    {
        var findings = Check(rec, profile);
        if (findings.Any(f => f.IsError))
            return new PushResult(false, findings, rec.Record.ControlNumber, 0);

        AutoFill(rec);
        rec.Status = RecordStatus.Pushed;
        if (rec.Id == 0) _repo.Insert(rec); else _repo.Update(rec);

        int rippled = rec.Base == "AUT" ? _repo.RewriteLinkedBibHeadings(rec.Id) : 0;
        return new PushResult(true, findings, rec.Record.ControlNumber, rippled);
    }

    // ---------- stage 4: authority ----------

    /// <summary>A stored heading link that has rotted is an error the cataloguer
    /// must re-forge (Ctrl+F4): the linked authority was deleted, or its 1XX text
    /// drifted from what the bib field says. An UNLINKED controlled field is NOT
    /// flagged — authority control is aspirational, and blocking every unlinked
    /// 650 would make a real catalogue un-pushable (scope call, docs/DEFERRED.md).</summary>
    private void AuthorityStage(StoredRecord rec, List<ValidationFinding> findings)
    {
        if (rec.Base != "BIB") return;

        var fields = rec.Record.Fields;
        for (int i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            if (field.IsControl || field.AuthLinkId is not long authId) continue;

            var auth = _repo.Load(authId);
            var authField = auth is null ? null : Headings.AuthorizedField(auth.Record);
            if (authField is null)
            {
                findings.Add(new(Severity.Error, FieldRef.Field(i), "auth.missing",
                    $"Field {field.Tag} is linked to an authority record that no longer exists — re-link it (Ctrl+F4)."));
                continue;
            }

            if (HeadingNormalization.Normalize(Headings.HeadingText(field))
                != HeadingNormalization.Normalize(Headings.HeadingText(authField)))
                findings.Add(new(Severity.Error, FieldRef.Field(i), "auth.drift",
                    $"Field {field.Tag} no longer matches its linked authorized heading — re-link it (Ctrl+F4)."));
        }
    }

    // ---------- stage 4b: duplicate 001 ----------

    private void DuplicateControlNumberStage(StoredRecord rec, List<ValidationFinding> findings)
    {
        string? cn = rec.Record.ControlNumber;
        if (string.IsNullOrEmpty(cn)) return; // empty 001 gets assigned at push — no collision possible
        if (_repo.ControlNumberUsedElsewhere(rec.Base, cn, rec.Id))
            findings.Add(new(Severity.Error, ControlNumberRef(rec.Record), "001.duplicate",
                $"Control number {cn} is already used by another record in {rec.Base}."));
    }

    // ---------- stage 5: auto-fill ----------

    /// <summary>The three approved automatic writes plus field ordering (Decisions;
    /// docs/PLAN.md §8 stage 5). Runs only after every stage passed.</summary>
    private void AutoFill(StoredRecord rec)
    {
        var record = rec.Record;

        // 001: a hand-typed number is kept forever; an empty one gets live MAX+1
        // for the base, computed now (no stored counter that could drift). For AUT,
        // the record being pushed is excluded from that MAX (user, 2026-08-01), so
        // clearing an authority's 001 in an otherwise-empty base restarts at 1
        // rather than re-using its own old number + 1. BIB keeps its exact
        // hand-numbered discipline ("001 SPECIALLY DUMB").
        if (string.IsNullOrEmpty(record.ControlNumber))
        {
            long ceiling = rec.Base == "AUT"
                ? _repo.MaxControlNumber(rec.Base, rec.Id)
                : _repo.MaxControlNumber(rec.Base);
            UpsertControl(record, "001", (ceiling + 1).ToString());
        }

        // 005: transaction date-time, MARC form yyyymmddhhmmss.f.
        UpsertControl(record, "005", DateTime.Now.ToString("yyyyMMddHHmmss.f"));

        // 003: the cataloguing agency's MARC organization code. This IS wanted as an
        // auto-fill (user, 2026-08-01) — it is a per-catalogue constant, not per-record
        // content, so unlike language/classification (which stay in templates) Apud may
        // stamp it. It writes ONLY when an org code has been set (File → Set Organization
        // Code); when unset, nothing is written — Apud still invents nothing on its own.
        // Every other byte of the record remains the cataloguer's.
        string? org = _repo.GetSetting("org_code");
        if (!string.IsNullOrWhiteSpace(org))
            UpsertControl(record, "003", org.Trim());

        StableSortByTag(record);
        LeaderMechanics.Recompute(record);
    }

    /// <summary>Sets a control field's data, creating the field if absent. Position
    /// does not matter here — <see cref="StableSortByTag"/> runs afterwards.</summary>
    private static void UpsertControl(MarcRecord record, string tag, string value)
    {
        var existing = record.Fields.FirstOrDefault(f => f.Tag == tag);
        if (existing is not null) existing.ControlData = value;
        else record.Fields.Add(new MarcField(tag) { ControlData = value });
    }

    /// <summary>The editor never reorders fields; the push does, once (§6.2). A
    /// STABLE sort by tag: repeated tags (three 650s, two 500s) keep exactly the
    /// order the cataloguer wrote them — subject order is real information.</summary>
    private static void StableSortByTag(MarcRecord record)
    {
        var ordered = record.Fields
            .OrderBy(f => f.Tag, StringComparer.Ordinal) // LINQ OrderBy is a stable sort
            .ToList();
        record.Fields.Clear();
        record.Fields.AddRange(ordered);
    }

    private static FieldRef? ControlNumberRef(MarcRecord record)
    {
        int i = record.Fields.FindIndex(f => f.Tag == "001");
        return i >= 0 ? FieldRef.Field(i) : null;
    }
}
