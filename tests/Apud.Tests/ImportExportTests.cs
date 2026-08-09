using Apud.Data;
using Marc.Core.Mrk;
using System.Text;

namespace Apud.Tests;

/// <summary>
/// The import/export engine contract (Module 5a): per-file reports with line
/// numbers, duplicate-001 detection (within the run and against the catalogue),
/// all-or-nothing commits, sequence bumping, pushed-vs-drafts semantics, and a
/// byte-exact export round-trip.
/// </summary>
public class ImportExportTests : IDisposable
{
    private readonly ApudDatabase _db = ApudDatabase.OpenInMemory();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"apud-import-{Guid.NewGuid():N}");

    private RecordRepository Repo => new(_db);
    private ImportEngine Engine => new(Repo);

    public ImportExportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(content));
        return path;
    }

    private static string Bib(string cn, string title) =>
        $"=LDR  00766nam a22002534i 4500\n=001  {cn}\n" +
        "=008  260415s2017    mx            000 0 spa d\n" +
        $"=100  1\\$aMoreno, Matías$eautor\n=245  10$a{title}\n=650  \\4$aFísica nuclear\n";

    private static string Aut(string cn, string heading) =>
        $"=LDR  00000nz  a2200000n  4500\n=001  {cn}\n=100  1\\$a{heading}\n";

    // ---------- analysis ----------

    [Fact]
    public void Clean_files_report_counts_and_no_errors()
    {
        WriteFile("a.mrk", Bib("1", "Uno") + "\n" + Bib("2", "Dos"));
        WriteFile("b.mrk", Aut("1", "Moreno, Matías"));

        var plan = Engine.AnalyzeFolder(_dir);

        Assert.Equal(2, plan.Report.Files.Count);
        Assert.Equal(3, plan.Report.TotalRecords);
        Assert.True(plan.Report.CanCommitAsPushed);
        Assert.All(plan.Report.Files, f => Assert.False(f.HasErrors));
    }

    [Fact]
    public void Importing_a_single_authority_file_routes_it_to_AUT()
    {
        // File → Import Records path (user request 2026-08-01): the user picks one
        // file, not a folder, and its authority leader routes it to the AUT base.
        WriteFile("bib.mrk", Bib("1", "Uno"));         // deliberately NOT selected
        string one = WriteFile("auth.mrk", Aut("5", "Moreno, Matías"));

        var plan = Engine.Analyze(new[] { one });

        Assert.Single(plan.Report.Files);
        Assert.Equal(1, plan.Report.TotalRecords);     // only the chosen file, not the folder
        Engine.Commit(plan);

        Assert.Single(Repo.List("AUT"));
        Assert.Empty(Repo.List("BIB"));                // the unselected bib stayed out
    }

    [Fact]
    public void Commit_returns_the_ids_it_inserted()
    {
        // Backs the "single record opens immediately" UI path (task 1): the app
        // opens result.ImportedIds[0] when exactly one record was imported.
        WriteFile("a.mrk", Bib("1", "Uno") + "\n" + Bib("2", "Dos"));
        var plan = Engine.AnalyzeFolder(_dir);

        var result = Engine.Commit(plan);

        Assert.Equal(2, result.ImportedIds.Count);
        Assert.All(result.ImportedIds, id => Assert.NotNull(Repo.Load(id)));
    }

    [Fact]
    public void Broken_file_reports_errors_with_line_numbers_and_blocks_pushed_commit()
    {
        WriteFile("clean.mrk", Bib("1", "Uno"));
        WriteFile("broken.mrk", "=LDR  00766nam a22002534i 4500\n=245  10$aBien\njunk line\n=650  \\4\n");

        var plan = Engine.AnalyzeFolder(_dir);

        var broken = plan.Report.Files.Single(f => f.FilePath.EndsWith("broken.mrk"));
        Assert.True(broken.HasErrors);
        Assert.Contains(broken.Diagnostics, d => d.Line == 3); // "junk line"
        Assert.Contains(broken.Diagnostics, d => d.Line == 4); // 650 with no subfields

        Assert.False(plan.Report.CanCommitAsPushed);
        Assert.True(plan.Report.CanCommitAsDrafts); // parse problems don't block drafts
        Assert.Throws<InvalidOperationException>(() => Engine.Commit(plan));
    }

    [Fact]
    public void Duplicate_001_within_the_run_is_a_run_error()
    {
        WriteFile("a.mrk", Bib("7", "Primero"));
        WriteFile("b.mrk", Bib("7", "Segundo"));

        var plan = Engine.AnalyzeFolder(_dir);

        Assert.Single(plan.Report.RunErrors);
        Assert.Contains("7", plan.Report.RunErrors[0]);
        Assert.False(plan.Report.CanCommitAsPushed);
        Assert.True(plan.Report.CanCommitAsDrafts); // drafts open in the app, not the DB — 001s are fixed before push
    }

    [Fact]
    public void Same_001_in_different_bases_is_not_a_duplicate()
    {
        WriteFile("a.mrk", Bib("1", "Uno") + "\n" + Aut("1", "Moreno, Matías"));
        var plan = Engine.AnalyzeFolder(_dir);
        Assert.Empty(plan.Report.RunErrors);
    }

    [Fact]
    public void Duplicate_001_against_the_existing_catalogue_is_a_run_error()
    {
        var existing = new StoredRecord("BIB", MrkReader.Read(Bib("5", "Ya presente")).Records[0]);
        Repo.Insert(existing);

        WriteFile("a.mrk", Bib("5", "Recién llegado"));
        var plan = Engine.AnalyzeFolder(_dir);

        Assert.Single(plan.Report.RunErrors);
        Assert.Contains("already exists", plan.Report.RunErrors[0]);
    }

    // ---------- commit ----------

    [Fact]
    public void AsPushed_commit_keeps_001s_marks_pushed_and_indexes_for_search()
    {
        WriteFile("a.mrk", Bib("10", "Física cuántica") + "\n" + Aut("3", "Moreno, Matías"));

        var result = Engine.Commit(Engine.AnalyzeFolder(_dir));

        Assert.Equal(2, result.RecordsImported);
        Assert.Equal(1, result.BibCount);
        Assert.Equal(1, result.AutCount);

        var bib = Repo.List("BIB").Single();
        Assert.Equal("10", bib.ControlNumber);
        Assert.Equal(RecordStatus.Pushed, bib.Status);
        Assert.Contains(bib.Id, Repo.Search("BIB", "fisica cuantica"));

        // Sequence bumped past the highest 001 seen, per base.
        Assert.Equal(11, Repo.NextControlNumber("BIB"));
        Assert.Equal(4, Repo.NextControlNumber("AUT"));
    }

    [Fact]
    public void ParsedRecords_returns_the_records_without_touching_the_database()
    {
        // Import-as-drafts opens the records in the app as unsaved working drafts; it
        // writes NOTHING to the catalogue (they are cleaned up, then Ctrl+D/Ctrl+L).
        WriteFile("a.mrk", Bib("10", "Física cuántica") + "\n" + Aut("3", "Moreno, Matías"));
        var plan = Engine.AnalyzeFolder(_dir);

        var records = Engine.ParsedRecords(plan);

        Assert.Equal(2, records.Count);
        Assert.Contains(records, r => r.Base == "BIB" && r.Record.ControlNumber == "10");
        Assert.Contains(records, r => r.Base == "AUT" && r.Record.ControlNumber == "3");
        Assert.Empty(Repo.List("BIB")); // nothing was committed
        Assert.Empty(Repo.List("AUT"));
    }

    [Fact]
    public void Failed_commit_leaves_the_database_untouched()
    {
        // Passes analysis (uniqueness checked against a snapshot), then collides
        // mid-commit: a record inserted after Analyze holds the same 001.
        WriteFile("a.mrk", Bib("1", "Uno") + "\n" + Bib("2", "Dos"));
        var plan = Engine.AnalyzeFolder(_dir);

        var squatter = new StoredRecord("BIB", MrkReader.Read(Bib("2", "Colado")).Records[0]);
        Repo.Insert(squatter);

        Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(
            () => Engine.Commit(plan));

        // All-or-nothing: only the squatter remains, nothing half-imported.
        Assert.Single(Repo.List("BIB"));
        Assert.Equal(1, Repo.NextControlNumber("BIB")); // sequence untouched by the failed run
    }

    // ---------- export ----------

    [Fact]
    public void Export_round_trips_byte_for_byte()
    {
        string source = Bib("1", "Física nuclear: introducción") + "\n" + Bib("2", "El {dollar} y la economía");
        WriteFile("a.mrk", source);
        Engine.Commit(Engine.AnalyzeFolder(_dir));

        byte[] exported = new ExportEngine(Repo).ExportBase("BIB");

        Assert.Equal(new UTF8Encoding(false).GetBytes(source), exported);
    }

    [Fact]
    public void Export_by_id_selection_respects_the_given_order()
    {
        WriteFile("a.mrk", Bib("1", "Uno") + "\n" + Bib("2", "Dos"));
        Engine.Commit(Engine.AnalyzeFolder(_dir));
        var list = Repo.List("BIB");

        byte[] bytes = new ExportEngine(Repo).Export(new[] { list[1].Id, list[0].Id });
        string text = new UTF8Encoding(false).GetString(bytes);

        Assert.True(text.IndexOf("Dos") < text.IndexOf("Uno"));
    }
}
