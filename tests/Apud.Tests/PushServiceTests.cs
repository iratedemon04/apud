using Apud.Data;
using Marc.Core;
using Marc.Core.Validation;

namespace Apud.Tests;

/// <summary>
/// The database-bound half of Ctrl+L (Module 9): the authority-link stage, the
/// duplicate-001 stage, and the auto-fill/push cycle — live MAX+1 control
/// numbers, the 005 stamp, stable field ordering, a recomputed leader, and
/// ripple-on-authority-push. Runs against an in-memory catalogue.
/// </summary>
public class PushServiceTests : IDisposable
{
    private readonly ApudDatabase _db = ApudDatabase.OpenInMemory();
    private RecordRepository Repo => new(_db);

    public void Dispose() => _db.Dispose();

    private static readonly string Good008 = "240101s2020    mx" + new string(' ', 18) + "spa d";

    private static MarcRecord CleanBib(params MarcField[] extra)
    {
        var r = new MarcRecord { Leader = "00000nam a2200000 i 4500" };
        r.Fields.Add(new MarcField("008") { ControlData = Good008 });
        r.Fields.Add(Data("245", '1', '0', ('a', "Física cuántica")));
        r.Fields.AddRange(extra);
        return r;
    }

    private PushResult Push(StoredRecord rec) =>
        new PushService(Repo).Push(rec, ValidationProfile.Default(rec.Base));

    // ---------- clean push + auto-fill ----------

    [Fact]
    public void A_clean_draft_pushes_and_earns_a_001()
    {
        var rec = new StoredRecord("BIB", CleanBib());
        var result = Push(rec);

        Assert.True(result.Ok);
        Assert.Equal("1", result.ControlNumber);      // first record in an empty base
        Assert.NotEqual(0, rec.Id);

        var loaded = Repo.Load(rec.Id)!;
        Assert.Equal(RecordStatus.Pushed, loaded.Status);
        Assert.Equal("1", loaded.Record.ControlNumber);
        Assert.Single(loaded.Record.FieldsWithTag("005"));
    }

    [Fact]
    public void Push_stamps_005_and_recomputes_the_leader()
    {
        var rec = new StoredRecord("BIB", CleanBib());
        Push(rec);
        var record = Repo.Load(rec.Id)!.Record;

        string stamp = record.FieldsWithTag("005").Single().ControlData!;
        Assert.Matches(@"^\d{14}\.\d$", stamp);        // yyyymmddhhmmss.f

        Assert.NotEqual("00000", record.Leader[..5]);  // record length filled
        Assert.Equal('2', record.Leader[10]);
        Assert.Equal('2', record.Leader[11]);
        Assert.Equal("4500", record.Leader.Substring(20, 4));
    }

    [Fact]
    public void Push_orders_fields_by_tag_and_keeps_repeated_tag_order()
    {
        // Written out of order, with two subjects whose order is real information.
        var rec = new StoredRecord("BIB", CleanBib(
            Data("650", ' ', '0', ('a', "First subject")),
            Data("100", '1', ' ', ('a', "Author")),
            Data("650", ' ', '0', ('a', "Second subject"))));
        Push(rec);

        var tags = Repo.Load(rec.Id)!.Record.Fields.Select(f => f.Tag).ToList();
        Assert.Equal(new[] { "001", "005", "008", "100", "245", "650", "650" }, tags);

        var subjects = Repo.Load(rec.Id)!.Record.FieldsWithTag("650")
            .Select(f => f.Subfield('a')).ToList();
        Assert.Equal(new[] { "First subject", "Second subject" }, subjects); // stable
    }

    // ---------- 001 discipline (Decisions: "001 SPECIALLY DUMB") ----------

    [Fact]
    public void An_empty_001_gets_live_max_plus_one_never_a_stored_counter()
    {
        // Seed the base with a hand-numbered record at 757; the next push must be 758.
        var seeded = new StoredRecord("BIB", CleanBib());
        seeded.Record.Fields.Insert(0, new MarcField("001") { ControlData = "757" });
        Push(seeded);

        var next = new StoredRecord("BIB", CleanBib());
        var result = Push(next);
        Assert.Equal("758", result.ControlNumber);
    }

    [Fact]
    public void A_hand_typed_001_is_kept_never_overwritten()
    {
        var rec = new StoredRecord("BIB", CleanBib());
        rec.Record.Fields.Insert(0, new MarcField("001") { ControlData = "40012" });
        var result = Push(rec);
        Assert.Equal("40012", result.ControlNumber);
    }

    [Fact]
    public void A_duplicate_hand_typed_001_blocks_the_push()
    {
        var first = new StoredRecord("BIB", CleanBib());
        first.Record.Fields.Insert(0, new MarcField("001") { ControlData = "500" });
        Push(first);

        var clash = new StoredRecord("BIB", CleanBib());
        clash.Record.Fields.Insert(0, new MarcField("001") { ControlData = "500" });
        var result = Push(clash);

        Assert.False(result.Ok);
        Assert.Contains(result.Findings, f => f.Code == "001.duplicate");
        Assert.Equal(0, clash.Id); // nothing written
    }

    // ---------- blocking ----------

    [Fact]
    public void An_error_blocks_the_push_and_writes_nothing()
    {
        var rec = new StoredRecord("BIB", CleanBib());
        rec.Record.Fields.RemoveAll(f => f.Tag == "245"); // mandatory missing
        var result = Push(rec);

        Assert.False(result.Ok);
        Assert.Contains(result.Findings, f => f.Code == "profile.mandatory");
        Assert.Equal(0, rec.Id);
        Assert.Empty(Repo.List("BIB"));
    }

    [Fact]
    public void Check_is_a_dry_run_and_writes_nothing()
    {
        var rec = new StoredRecord("BIB", CleanBib());
        var findings = new PushService(Repo).Check(rec, ValidationProfile.Default("BIB"));

        Assert.DoesNotContain(findings, f => f.IsError);
        Assert.Equal(0, rec.Id);
        Assert.Empty(Repo.List("BIB"));
        Assert.Null(rec.Record.FieldsWithTag("005").FirstOrDefault()); // no auto-fill happened
    }

    // ---------- authority stage ----------

    [Fact]
    public void A_link_to_a_missing_authority_blocks_the_push()
    {
        var rec = new StoredRecord("BIB", CleanBib(
            Data("700", '1', ' ', ('a', "Somebody"))));
        rec.Record.Fields.Last(f => f.Tag == "700").AuthLinkId = 99999; // no such record

        var result = Push(rec);
        Assert.False(result.Ok);
        Assert.Contains(result.Findings, f => f.Code == "auth.missing");
    }

    [Fact]
    public void A_link_whose_heading_drifted_blocks_the_push()
    {
        var auth = InsertPushedAuthority("Moreno, Matías");
        var rec = new StoredRecord("BIB", CleanBib(
            Data("700", '1', ' ', ('a', "Somebody Else"))));
        rec.Record.Fields.Last(f => f.Tag == "700").AuthLinkId = auth.Id;

        var result = Push(rec);
        Assert.False(result.Ok);
        Assert.Contains(result.Findings, f => f.Code == "auth.drift");
    }

    [Fact]
    public void An_unlinked_controlled_field_does_not_block_the_push()
    {
        // Authority control is aspirational: an unlinked 650 must not stop a push.
        var rec = new StoredRecord("BIB", CleanBib(
            Data("650", ' ', '0', ('a', "Física"))));
        Assert.True(Push(rec).Ok);
    }

    // ---------- ripple on authority push ----------

    [Fact]
    public void Pushing_an_authority_ripples_its_new_heading_into_linked_bibs()
    {
        var auth = InsertPushedAuthority("Moreno, Matias"); // typo, no accent

        var bib = new StoredRecord("BIB", CleanBib(
            Data("700", '1', ' ', ('a', "Moreno, Matias"))));
        bib.Record.Fields.Last(f => f.Tag == "700").AuthLinkId = auth.Id;
        Assert.True(Push(bib).Ok);

        // Fix the authorized form and push the authority again.
        var reload = Repo.Load(auth.Id)!;
        reload.Record.FieldsWithTag("100").Single().Subfields[0].Value = "Moreno, Matías";
        var result = Push(reload);

        Assert.True(result.Ok);
        Assert.Equal(1, result.RippledFields);
        Assert.Equal("Moreno, Matías",
            Repo.Load(bib.Id)!.Record.FieldsWithTag("700").Single().Subfield('a'));
    }

    // ---------- helpers ----------

    private StoredRecord InsertPushedAuthority(string heading)
    {
        var r = new MarcRecord { Leader = "00000nz  a2200000n  4500" };
        r.Fields.Add(Data("100", '1', ' ', ('a', heading)));
        var stored = new StoredRecord("AUT", r);
        var repo = Repo;
        repo.Insert(stored);
        stored.Status = RecordStatus.Pushed;
        repo.Update(stored);
        return stored;
    }

    private static MarcField Data(string tag, char ind1, char ind2, params (char Code, string Value)[] subfields)
    {
        var f = new MarcField(tag) { Ind1 = ind1, Ind2 = ind2 };
        foreach (var (code, value) in subfields) f.Subfields.Add(new MarcSubfield(code, value));
        return f;
    }
}
