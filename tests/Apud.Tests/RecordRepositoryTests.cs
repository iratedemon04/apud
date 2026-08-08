using Apud.Data;
using Marc.Core;
using Marc.Core.Mrk;

namespace Apud.Tests;

/// <summary>
/// The DB layer's contract: what goes in comes out identical (proved via .mrk
/// serialization equality), and fields + heading links live or die together.
/// </summary>
public class RecordRepositoryTests : IDisposable
{
    private readonly ApudDatabase _db = ApudDatabase.OpenInMemory();
    private RecordRepository Repo => new(_db);

    public void Dispose() => _db.Dispose();

    private const string Monograph =
        "=LDR  00766nam a22002534i 4500\n" +
        "=001  1\n" +
        "=008  260415s2017    mx            000 0 spa d\n" +
        "=040  \\\\$aXX-XxLib$bspa$erda\n" +
        "=100  1\\$aMoreno, Matías$eautor\n" +
        "=245  10$aGrandes proyectos científicos: Sincrotrón\n" +
        "=264  \\1$aMéxico$bEl Colegio Nacional$c2017\n" +
        "=650  \\4$aFísica nuclear$xInvestigación$xMéxico\n" +
        "=650  \\4$aSincrotrón\n" +
        "=852  2\\$eBúfalo\n";

    private static MarcRecord Parse(string mrk) => MrkReader.Read(mrk).Records[0];

    [Fact]
    public void Save_then_load_reproduces_the_record_exactly()
    {
        var stored = new StoredRecord("BIB", Parse(Monograph));
        Repo.Insert(stored);

        var loaded = Repo.Load(stored.Id)!;

        Assert.Equal(Monograph, MrkWriter.Write(loaded.Record));
        Assert.Equal("BIB", loaded.Base);
        Assert.Equal(RecordStatus.Draft, loaded.Status);
        Assert.Equal("1", loaded.Record.ControlNumber);
    }

    [Fact]
    public void Update_rewrites_fields_and_preserves_authority_links()
    {
        var stored = new StoredRecord("BIB", Parse(Monograph));
        Repo.Insert(stored);

        // Simulate Module 8: the 100 field gets linked to an authority record.
        var authority = new StoredRecord("AUT", Parse("=LDR  00000nz  a2200000n  4500\n=001  9\n=100  1\\$aMoreno, Matías\n"));
        Repo.Insert(authority);
        stored.Record.FieldsWithTag("100").First().AuthLinkId = authority.Id;
        Repo.Update(stored);

        // Edit something unrelated and save again: the link must survive the rewrite.
        stored.Record.FieldsWithTag("245").First().Subfields[0].Value += " (2a edición)";
        Repo.Update(stored);

        var loaded = Repo.Load(stored.Id)!;
        Assert.Equal(authority.Id, loaded.Record.FieldsWithTag("100").First().AuthLinkId);
        Assert.Contains("(2a edición)", loaded.Record.FieldsWithTag("245").First().Subfield('a'));
        Assert.Equal(1, Repo.CountLinksTo(authority.Id));
    }

    [Fact]
    public void SaveDraft_inserts_a_new_record_out_of_search()
    {
        var stored = new StoredRecord("BIB", Parse(Monograph)) { Status = RecordStatus.Pushed };
        Repo.SaveDraft(stored); // Ctrl+S on a brand-new record

        Assert.NotEqual(0, stored.Id);
        Assert.Equal(RecordStatus.Draft, stored.Status);
        Assert.Equal(RecordStatus.Draft, Repo.Load(stored.Id)!.Status);
        Assert.DoesNotContain(stored.Id, Repo.Search("BIB", "sincrotron")); // drafts are invisible
    }

    [Fact]
    public void SaveDraft_demotes_a_pushed_record_and_pulls_it_from_search()
    {
        var stored = new StoredRecord("BIB", Parse(Monograph)) { Status = RecordStatus.Pushed };
        Repo.Insert(stored);
        Assert.Contains(stored.Id, Repo.Search("BIB", "sincrotron")); // pushed → searchable

        Repo.SaveDraft(stored); // editing then Ctrl+S demotes until re-pushed

        Assert.Equal(RecordStatus.Draft, Repo.Load(stored.Id)!.Status);
        Assert.DoesNotContain(stored.Id, Repo.Search("BIB", "sincrotron"));
    }

    [Fact]
    public void DraftIds_returns_only_drafts_across_both_bases()
    {
        var pushed = new StoredRecord("BIB", Parse(Monograph)) { Status = RecordStatus.Pushed };
        Repo.Insert(pushed);
        var bibDraft = new StoredRecord("BIB", Parse(Monograph.Replace("=001  1\n", ""))); // a draft usually has no 001 yet
        Repo.SaveDraft(bibDraft);
        var autDraft = new StoredRecord("AUT", Parse("=LDR  00000nz  a2200000n  4500\n=100  1\\$aMoreno, Matías\n"));
        Repo.SaveDraft(autDraft);

        var ids = Repo.DraftIds();

        Assert.Equal(new[] { autDraft.Id, bibDraft.Id }.OrderBy(x => x), ids.OrderBy(x => x));
        Assert.DoesNotContain(pushed.Id, ids); // pushed records are not reopened
    }

    [Fact]
    public void Deleting_a_record_cascades_fields_and_links()
    {
        var stored = new StoredRecord("BIB", Parse(Monograph));
        Repo.Insert(stored);
        var authority = new StoredRecord("AUT", Parse("=LDR  00000nz  a2200000n  4500\n=100  1\\$aMoreno, Matías\n"));
        Repo.Insert(authority);
        stored.Record.FieldsWithTag("100").First().AuthLinkId = authority.Id;
        Repo.Update(stored);

        Repo.Delete(stored.Id);

        Assert.Null(Repo.Load(stored.Id));
        Assert.Equal(0, Repo.CountLinksTo(authority.Id));
        Assert.Equal(0L, _db.Scalar("SELECT COUNT(*) FROM field WHERE record_id = " + stored.Id));
    }

    [Fact]
    public void Duplicate_control_number_in_same_base_is_rejected()
    {
        Repo.Insert(new StoredRecord("BIB", Parse(Monograph)));
        var dup = new StoredRecord("BIB", Parse(Monograph));

        Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(() => Repo.Insert(dup));
    }

    [Fact]
    public void Same_control_number_in_different_bases_is_fine()
    {
        Repo.Insert(new StoredRecord("BIB", Parse(Monograph)));
        var aut = new StoredRecord("AUT", Parse("=LDR  00000nz  a2200000n  4500\n=001  1\n=100  1\\$aMoreno, Matías\n"));
        Repo.Insert(aut); // no throw
        Assert.NotEqual(0, aut.Id);
    }

    [Fact]
    public void Records_without_control_number_can_coexist_as_drafts()
    {
        var a = new StoredRecord("BIB", Parse("=LDR  00000nam a2200000 i 4500\n=245  10$aBorrador uno\n"));
        var b = new StoredRecord("BIB", Parse("=LDR  00000nam a2200000 i 4500\n=245  10$aBorrador dos\n"));
        Repo.Insert(a);
        Repo.Insert(b); // partial unique index: NULL 001s don't collide
        Assert.Equal(2, Repo.List("BIB").Count);
    }

    [Fact]
    public void List_shows_titles_and_orders_by_control_number()
    {
        var r10 = Parse(Monograph.Replace("=001  1\n", "=001  10\n"));
        var r2 = Parse(Monograph.Replace("=001  1\n", "=001  2\n").Replace("Grandes proyectos", "Otros proyectos"));
        Repo.Insert(new StoredRecord("BIB", r10));
        Repo.Insert(new StoredRecord("BIB", r2));

        var list = Repo.List("BIB");
        Assert.Equal(new[] { "2", "10" }, list.Select(s => s.ControlNumber).ToArray());
        Assert.StartsWith("Otros proyectos", list[0].Title);
        Assert.Equal("Moreno, Matías", list[0].Author); // 100, first subfield
        Assert.Equal("2017", list[0].Year);             // 264 $c "as written"
    }

    [Fact]
    public void Authority_summary_carries_the_full_heading_classification_and_source()
    {
        // 150 subject with $a$x, a 082 class number, a 670 source. The heading must
        // show ALL subfields (not just $a — task 5); Classification comes from 08X,
        // Source from the first 670 (task 2).
        var aut = Parse(
            "=LDR  00000nz  a2200000n  4500\n" +
            "=001  42\n" +
            "=082  \\4$a539.7\n" +
            "=150  \\\\$aFísica nuclear$xInvestigación\n" +
            "=670  \\\\$aGran enciclopedia, 2019\n");
        Repo.Insert(new StoredRecord("AUT", aut));

        var row = Repo.List("AUT").Single();
        Assert.Equal("42", row.ControlNumber);
        Assert.Equal("Física nuclear--Investigación", row.Title); // whole heading, subfields joined by "--"
        Assert.Equal("539.7", row.Classification);
        Assert.Equal("Gran enciclopedia, 2019", row.Source);
    }

    [Fact]
    public void ListPage_and_Count_page_a_base_without_loading_it_all()
    {
        for (int cn = 1; cn <= 5; cn++)
            Repo.Insert(new StoredRecord("BIB", Parse(Monograph.Replace("=001  1\n", $"=001  {cn}\n"))));

        Assert.Equal(5, Repo.Count("BIB"));
        Assert.Equal(0, Repo.Count("AUT"));

        // Two pages of 2 (control-number order), then the last one.
        Assert.Equal(new[] { "1", "2" }, Repo.ListPage("BIB", 2, 0).Select(s => s.ControlNumber));
        Assert.Equal(new[] { "3", "4" }, Repo.ListPage("BIB", 2, 2).Select(s => s.ControlNumber));
        Assert.Equal(new[] { "5" }, Repo.ListPage("BIB", 2, 4).Select(s => s.ControlNumber));
        Assert.Empty(Repo.ListPage("BIB", 2, 5));
    }

    [Fact]
    public void ListByIds_hydrates_only_the_given_records()
    {
        var repo = Repo;
        for (int cn = 1; cn <= 4; cn++)
            repo.Insert(new StoredRecord("BIB", Parse(Monograph.Replace("=001  1\n", $"=001  {cn}\n"))));
        var all = repo.List("BIB");
        long id2 = all.Single(s => s.ControlNumber == "2").Id;
        long id4 = all.Single(s => s.ControlNumber == "4").Id;

        var got = repo.ListByIds(new[] { id4, id2 });
        Assert.Equal(2, got.Count);
        Assert.Equal(new[] { "2", "4" }, got.Select(s => s.ControlNumber).OrderBy(x => x)); // both, order unspecified
        Assert.Empty(repo.ListByIds(System.Array.Empty<long>()));
    }

    [Fact]
    public void List_year_comes_from_the_publication_field_never_the_008()
    {
        // Record 177's 008 held "[1960]" stuffed into its 4-char date slot and
        // showed as "[196"; the 264 $c has the real "[1960]". The displayed year
        // is the transcription, even when the 008 carries its own (here different)
        // date — the coded field is never consulted (user, 2026-07-31).
        var rec = Parse(
            "=LDR  00000nam a2200000 i 4500\n" +
            "=008  260415s1955    mx            000 0 spa d\n" + // 008 says 1955
            "=245  10$aFecha entre corchetes\n" +
            "=264  \\1$aMéxico :$bEl Colegio,$c[1960]\n" +       // $c says [1960]
            "=700  1\\$aPaz, Octavio\n");
        Repo.Insert(new StoredRecord("BIB", rec));

        var row = Repo.List("BIB").Single();
        Assert.Equal("[1960]", row.Year);         // the $c wins, brackets and all
        Assert.Equal("Paz, Octavio", row.Author); // no 1XX → first 7XX
    }

    [Fact]
    public void Sequence_counts_up_and_import_bump_is_respected()
    {
        Assert.Equal(1, Repo.NextControlNumber("BIB"));
        Assert.Equal(2, Repo.NextControlNumber("BIB"));

        Repo.BumpSequencePast("BIB", 654); // e.g. after importing a catalogue
        Assert.Equal(655, Repo.NextControlNumber("BIB"));

        Assert.Equal(1, Repo.NextControlNumber("AUT")); // bases are independent
    }

    [Fact]
    public void Settings_roundtrip_and_overwrite()
    {
        Assert.Null(Repo.GetSetting("org_code"));
        Repo.SetSetting("org_code", "XX-XxLib");
        Repo.SetSetting("org_code", "XX-XxLib2");
        Assert.Equal("XX-XxLib2", Repo.GetSetting("org_code"));
    }

    [Fact]
    public void Status_transitions_persist()
    {
        var stored = new StoredRecord("BIB", Parse(Monograph));
        Repo.Insert(stored);
        stored.Status = RecordStatus.Pushed;
        Repo.Update(stored);

        Assert.Equal(RecordStatus.Pushed, Repo.Load(stored.Id)!.Status);
    }

    [Fact]
    public void Spanish_text_survives_the_database_intact()
    {
        var stored = new StoredRecord("BIB", Parse(Monograph));
        Repo.Insert(stored);
        var loaded = Repo.Load(stored.Id)!;

        Assert.Equal("Física nuclear", loaded.Record.FieldsWithTag("650").First().Subfield('a'));
        Assert.Equal("Búfalo", loaded.Record.FieldsWithTag("852").First().Subfield('e'));
        Assert.Equal("Moreno, Matías", loaded.Record.FieldsWithTag("100").First().Subfield('a'));
    }
}
