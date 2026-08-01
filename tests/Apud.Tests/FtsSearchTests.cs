using Apud.Data;
using Marc.Core;
using Marc.Core.Mrk;

namespace Apud.Tests;

/// <summary>
/// The search contract (Module 5a): pushed records are findable, drafts are not,
/// accents never matter ("fisica" finds "Física"), and the index follows every
/// status change, edit, and delete.
/// </summary>
public class FtsSearchTests : IDisposable
{
    private readonly ApudDatabase _db = ApudDatabase.OpenInMemory();
    private RecordRepository Repo => new(_db);

    public void Dispose() => _db.Dispose();

    private static MarcRecord Parse(string mrk) => MrkReader.Read(mrk).Records[0];

    private static string Bib(string cn, string title, string author = "Moreno, Matías", string subject = "Física nuclear") =>
        $"=LDR  00000nam a2200000 i 4500\n=001  {cn}\n" +
        $"=100  1\\$a{author}\n=245  10$a{title}\n=650  \\4$a{subject}\n";

    private StoredRecord InsertPushed(string mrk, string @base = "BIB")
    {
        var stored = new StoredRecord(@base, Parse(mrk));
        Repo.Insert(stored);
        stored.Status = RecordStatus.Pushed;
        Repo.Update(stored);
        return stored;
    }

    [Fact]
    public void Pushed_records_are_searchable_drafts_are_not()
    {
        var pushed = InsertPushed(Bib("1", "El sincrotrón mexicano"));
        var draft = new StoredRecord("BIB", Parse(Bib("2", "Sincrotrones del mundo")));
        Repo.Insert(draft);

        var ids = Repo.Search("BIB", "sincrotron");
        Assert.Contains(pushed.Id, ids);
        Assert.DoesNotContain(draft.Id, ids);
    }

    [Fact]
    public void Accents_do_not_matter_in_either_direction()
    {
        var rec = InsertPushed(Bib("1", "Grandes proyectos", subject: "Física nuclear"));

        Assert.Contains(rec.Id, Repo.Search("BIB", "fisica"));   // plain finds accented
        Assert.Contains(rec.Id, Repo.Search("BIB", "física"));   // accented finds accented
        Assert.Contains(rec.Id, Repo.Search("BIB", "matias"));   // author, plain
    }

    [Fact]
    public void Search_matches_control_number_and_multiple_words()
    {
        var rec = InsertPushed(Bib("42", "Historia de la ciencia en México"));

        Assert.Contains(rec.Id, Repo.Search("BIB", "42"));
        Assert.Contains(rec.Id, Repo.Search("BIB", "historia mexico"));
        Assert.Empty(Repo.Search("BIB", "historia inexistente"));
    }

    [Fact]
    public void Search_is_scoped_to_the_base()
    {
        var bib = InsertPushed(Bib("1", "Física para todos"));
        InsertPushed("=LDR  00000nz  a2200000n  4500\n=001  1\n=100  1\\$aFísica\n", "AUT");

        var ids = Repo.Search("BIB", "fisica");
        Assert.Equal(new[] { bib.Id }, ids);
    }

    [Fact]
    public void Editing_a_pushed_record_reindexes_it()
    {
        var rec = InsertPushed(Bib("1", "Título viejo"));

        rec.Record.FieldsWithTag("245").First().Subfields[0].Value = "Título nuevo";
        Repo.Update(rec);

        Assert.Empty(Repo.Search("BIB", "viejo"));
        Assert.Contains(rec.Id, Repo.Search("BIB", "nuevo"));
    }

    [Fact]
    public void Demoting_to_draft_removes_from_search_and_deleting_does_too()
    {
        var rec = InsertPushed(Bib("1", "Efímero"));
        Assert.Contains(rec.Id, Repo.Search("BIB", "efimero"));

        rec.Status = RecordStatus.Draft;
        Repo.Update(rec);
        Assert.Empty(Repo.Search("BIB", "efimero"));

        rec.Status = RecordStatus.Pushed;
        Repo.Update(rec);
        Repo.Delete(rec.Id);
        Assert.Empty(Repo.Search("BIB", "efimero"));
    }

    [Fact]
    public void Ranking_puts_the_better_match_first()
    {
        var about = InsertPushed(Bib("1", "Sincrotrón: el sincrotrón mexicano", subject: "Sincrotrón"));
        var mention = InsertPushed(Bib("2", "Miscelánea científica", subject: "Sincrotrón"));

        var ids = Repo.Search("BIB", "sincrotron");
        Assert.Equal(new[] { about.Id, mention.Id }, ids);
    }

    [Fact]
    public void Scoped_search_restricts_matching_to_one_column()
    {
        var rec = InsertPushed(Bib("1", "Historia de la ciencia", subject: "Física nuclear"));

        Assert.Contains(rec.Id, Repo.Search("BIB", "fisica", SearchScope.Subjects));
        Assert.Contains(rec.Id, Repo.Search("BIB", "fisica", SearchScope.All));
        Assert.Empty(Repo.Search("BIB", "fisica", SearchScope.Title));   // it's a subject, not a title
        Assert.Empty(Repo.Search("BIB", "historia", SearchScope.Author));

        Assert.Contains(rec.Id, Repo.Search("BIB", "1", SearchScope.ControlNumber));
        Assert.Empty(Repo.Search("BIB", "historia", SearchScope.ControlNumber));
    }

    [Fact]
    public void Scoped_search_still_folds_accents()
    {
        var rec = InsertPushed(Bib("1", "Grandes proyectos", author: "Gómez Íñiguez, José"));
        Assert.Contains(rec.Id, Repo.Search("BIB", "gomez iniguez", SearchScope.Author));
    }

    [Fact]
    public void Match_expression_wraps_scoped_terms_in_a_column_filter()
    {
        Assert.Equal("\"fisica\"* \"nuclear\"*", RecordRepository.BuildMatchExpression("fisica nuclear"));
        Assert.Equal("title : (\"fisica\"*)", RecordRepository.BuildMatchExpression("fisica", SearchScope.Title));
        Assert.Equal("", RecordRepository.BuildMatchExpression("   ", SearchScope.Title));
    }

    [Fact]
    public void Hostile_query_text_never_throws()
    {
        InsertPushed(Bib("1", "Normal"));

        // FTS5 operators, quotes, stray syntax — all neutralized by quoting,
        // in every scope.
        foreach (var q in new[] { "AND OR NOT", "\"unbalanced", "a*b(c)", "  ", "-x {y}" })
            foreach (SearchScope scope in Enum.GetValues<SearchScope>())
            {
                var ex = Xunit.Record.Exception(() => Repo.Search("BIB", q, scope));
                Assert.Null(ex);
            }
    }

    [Fact]
    public void Search_history_is_newest_first_and_keeps_repeats()
    {
        var h = new Apud.App.SearchHistory();
        h.Add(new Apud.App.SearchHistoryEntry("fuero politico", SearchScope.All, "BIB", 0));
        h.Add(new Apud.App.SearchHistoryEntry("fuero", SearchScope.All, "BIB", 26));
        h.Add(new Apud.App.SearchHistoryEntry("fuero", SearchScope.All, "BIB", 26));

        Assert.Equal(3, h.Entries.Count);
        Assert.Equal("fuero", h.Entries[0].Query);
        Assert.Equal("fuero politico", h.Entries[2].Query);
    }

    [Fact]
    public void Old_v1_schema_databases_are_migrated_and_reindexed()
    {
        // Simulate a v1 file: current schema but with the tokenizer-less FTS table.
        var path = Path.Combine(Path.GetTempPath(), $"apud-v1-{Guid.NewGuid():N}.db");
        try
        {
            using (var db = ApudDatabase.Open(path))
            {
                var repo = new RecordRepository(db);
                var rec = new StoredRecord("BIB", Parse(Bib("1", "Física persistente")));
                repo.Insert(rec);
                rec.Status = RecordStatus.Pushed;
                repo.Update(rec);

                db.Execute("""
                    DROP TABLE record_fts;
                    CREATE VIRTUAL TABLE record_fts USING fts5(
                      control_number, title, author, subjects, anytext, content='');
                    PRAGMA user_version = 1;
                    """);
            }

            using (var db = ApudDatabase.Open(path))
            {
                Assert.Equal((long)ApudDatabase.SchemaVersion, db.Scalar("PRAGMA user_version;"));
                Assert.Single(new RecordRepository(db).Search("BIB", "fisica"));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
