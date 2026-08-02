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
    public void Call_number_and_notes_are_their_own_scopes()
    {
        // 090 = local call number (050–099 block); 500 = general note (5XX).
        var rec = InsertPushed(
            "=LDR  00000nam a2200000 i 4500\n=001  7\n" +
            "=090  \\\\$aQC793 .M67 2020\n" +
            "=245  10$aFisica de particulas\n" +
            "=500  \\\\$aIncludes bibliographical references and index.\n");

        // Call Number scope matches the class mark; Notes/Title do not.
        Assert.Contains(rec.Id, Repo.Search("BIB", "QC793", SearchScope.CallNumber));
        Assert.Contains(rec.Id, Repo.Search("BIB", "QC793", SearchScope.All));   // still in anytext
        Assert.Empty(Repo.Search("BIB", "QC793", SearchScope.Title));
        Assert.Empty(Repo.Search("BIB", "QC793", SearchScope.Notes));

        // Notes scope matches the 500 text; Title/CallNumber do not.
        Assert.Contains(rec.Id, Repo.Search("BIB", "bibliographical", SearchScope.Notes));
        Assert.Empty(Repo.Search("BIB", "bibliographical", SearchScope.Title));
        Assert.Empty(Repo.Search("BIB", "bibliographical", SearchScope.CallNumber));
    }

    [Fact]
    public void Bib_series_publisher_and_isbn_are_their_own_scopes()
    {
        var rec = InsertPushed(
            "=LDR  00000nam a2200000 i 4500\n=001  5\n" +
            "=020  \\\\$a9788478884457\n" +
            "=245  10$aAlgo interesante\n" +
            "=264  \\1$aMadrid :$bAlianza Editorial,$c2020.\n" +
            "=490  1\\$aBiblioteca de autores clasicos\n");

        Assert.Contains(rec.Id, Repo.Search("BIB", "9788478884457", SearchScope.Isbn));
        Assert.Contains(rec.Id, Repo.Search("BIB", "alianza", SearchScope.Publisher));
        Assert.Empty(Repo.Search("BIB", "madrid", SearchScope.Publisher));   // $a place, not $b
        Assert.Contains(rec.Id, Repo.Search("BIB", "biblioteca clasicos", SearchScope.Series));
        Assert.Empty(Repo.Search("BIB", "alianza", SearchScope.Title));
    }

    [Fact]
    public void Aut_heading_types_are_separate_scopes()
    {
        var person = InsertPushed("=LDR  00000nz  a2200000n  4500\n=001  1\n=100  1\\$aTwain, Mark\n", "AUT");
        var uniform = InsertPushed("=LDR  00000nz  a2200000n  4500\n=001  2\n=130  \\0$aBible.$pNew Testament\n", "AUT");
        var topical = InsertPushed("=LDR  00000nz  a2200000n  4500\n=001  3\n=150  \\\\$aPhysics\n", "AUT");
        var corp = InsertPushed("=LDR  00000nz  a2200000n  4500\n=001  4\n=110  2\\$aUnited Nations\n", "AUT");

        Assert.Contains(person.Id, Repo.Search("AUT", "twain", SearchScope.HeadingPersonal));
        Assert.Empty(Repo.Search("AUT", "twain", SearchScope.HeadingUniform));   // a 130 lookup is not a 100 lookup

        Assert.Contains(uniform.Id, Repo.Search("AUT", "bible", SearchScope.HeadingUniform));
        Assert.Empty(Repo.Search("AUT", "bible", SearchScope.HeadingPersonal));

        Assert.Contains(topical.Id, Repo.Search("AUT", "physics", SearchScope.HeadingTopical));
        Assert.Contains(corp.Id, Repo.Search("AUT", "nations", SearchScope.HeadingCorporate));

        Assert.Contains(person.Id, Repo.Search("AUT", "twain", SearchScope.All)); // All fields still spans them
    }

    [Fact]
    public void Aut_tracings_and_sources_are_scoped_and_5xx_is_not_notes()
    {
        var rec = InsertPushed(
            "=LDR  00000nz  a2200000n  4500\n=001  10\n" +
            "=100  1\\$aClemens, Samuel Langhorne\n" +
            "=400  1\\$aTwain, Mark\n" +
            "=500  1\\$aWarner, Charles Dudley\n" +
            "=670  \\\\$aThe gilded age, 1873\n" +
            "=680  \\\\$aAmerican author and humorist\n", "AUT");

        Assert.Contains(rec.Id, Repo.Search("AUT", "twain", SearchScope.SeeFrom));    // 4XX
        Assert.Contains(rec.Id, Repo.Search("AUT", "warner", SearchScope.SeeAlso));   // 5XX
        Assert.Contains(rec.Id, Repo.Search("AUT", "gilded", SearchScope.Sources));   // 670

        // In authority records 5XX is a See-Also tracing, NOT a note (unlike BIB):
        Assert.Empty(Repo.Search("AUT", "warner", SearchScope.Notes));
        Assert.Contains(rec.Id, Repo.Search("AUT", "humorist", SearchScope.All)); // 680 still in anytext
    }

    [Fact]
    public void Match_expression_wraps_scoped_terms_in_a_column_filter()
    {
        Assert.Equal("\"fisica\"* \"nuclear\"*", RecordRepository.BuildMatchExpression("fisica nuclear"));
        Assert.Equal("title : (\"fisica\"*)", RecordRepository.BuildMatchExpression("fisica", SearchScope.Title));
        Assert.Equal("notes : (\"index\"*)", RecordRepository.BuildMatchExpression("index", SearchScope.Notes));
        Assert.Equal("callnumber : (\"qc793\"*)", RecordRepository.BuildMatchExpression("qc793", SearchScope.CallNumber));
        Assert.Equal("h_personal : (\"twain\"*)", RecordRepository.BuildMatchExpression("twain", SearchScope.HeadingPersonal));
        Assert.Equal("variant : (\"twain\"*)", RecordRepository.BuildMatchExpression("twain", SearchScope.SeeFrom));
        Assert.Equal("series : (\"clasicos\"*)", RecordRepository.BuildMatchExpression("clasicos", SearchScope.Series));
        Assert.Equal("identifier : (\"978\"*)", RecordRepository.BuildMatchExpression("978", SearchScope.Isbn));
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
