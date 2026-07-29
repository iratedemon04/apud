using Marc.Core;
using Marc.Core.Mrk;

namespace Apud.Data;

public enum ImportMode
{
    /// <summary>Trusted migration: records arrive as pushed, existing 001s kept.</summary>
    AsPushed,

    /// <summary>Records arrive as drafts (outside search) for review before pushing.</summary>
    AsDrafts,
}

/// <summary>What one .mrk file contributed to an import run.</summary>
public sealed class ImportFileReport
{
    public string FilePath { get; }
    public int RecordCount { get; }
    public IReadOnlyList<MrkDiagnostic> Diagnostics { get; }

    public bool HasErrors => Diagnostics.Any(d => d.Severity == MrkSeverity.Error);

    internal ImportFileReport(string filePath, int recordCount, IReadOnlyList<MrkDiagnostic> diagnostics)
    {
        FilePath = filePath;
        RecordCount = recordCount;
        Diagnostics = diagnostics;
    }
}

/// <summary>
/// The full pre-commit report of an import run: per-file parse results plus
/// run-level errors (duplicate 001s within the run or against the catalogue).
/// </summary>
public sealed class ImportReport
{
    public IReadOnlyList<ImportFileReport> Files { get; }
    public IReadOnlyList<string> RunErrors { get; }
    public int TotalRecords { get; }

    public bool HasParseErrors => Files.Any(f => f.HasErrors);

    /// <summary>AS-PUSHED is a trusted migration: any error anywhere blocks the whole run.</summary>
    public bool CanCommitAsPushed => !HasParseErrors && RunErrors.Count == 0;

    /// <summary>
    /// AS-DRAFTS exists to bring imperfect data in for fixing, so parse errors don't
    /// block it — but duplicate 001s do (the database can't hold them either way).
    /// </summary>
    public bool CanCommitAsDrafts => RunErrors.Count == 0;

    internal ImportReport(IReadOnlyList<ImportFileReport> files, IReadOnlyList<string> runErrors, int totalRecords)
    {
        Files = files;
        RunErrors = runErrors;
        TotalRecords = totalRecords;
    }
}

/// <summary>An analyzed, ready-to-commit import run: the report plus the parsed records.</summary>
public sealed class ImportPlan
{
    public ImportReport Report { get; }
    internal IReadOnlyList<PlannedRecord> Records { get; }

    internal ImportPlan(ImportReport report, IReadOnlyList<PlannedRecord> records)
    {
        Report = report;
        Records = records;
    }
}

internal sealed record PlannedRecord(string FilePath, string Base, MarcRecord Record);

public sealed record ImportResult(int RecordsImported, int BibCount, int AutCount);

/// <summary>
/// Headless import: a list of .mrk files (or a folder) is parsed and reported on,
/// then committed in ONE transaction — either everything lands or nothing does.
/// Records are routed to BIB or AUT by their leader (LDR/06 = 'z' → authority).
/// After a commit the 001 sequence of each base is bumped past the highest numeric
/// 001 seen, so new records can never collide with imported ones.
/// </summary>
public sealed class ImportEngine
{
    private readonly RecordRepository _repo;

    public ImportEngine(RecordRepository repo) => _repo = repo;

    /// <summary>All .mrk files under a folder (recursive), in stable name order.</summary>
    public static IReadOnlyList<string> FindMrkFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*.mrk", SearchOption.AllDirectories)
                 .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                 .ToList();

    public ImportPlan AnalyzeFolder(string folder) => Analyze(FindMrkFiles(folder));

    /// <summary>Parses every file and checks 001 uniqueness. Touches nothing.</summary>
    public ImportPlan Analyze(IEnumerable<string> mrkFiles)
    {
        var fileReports = new List<ImportFileReport>();
        var planned = new List<PlannedRecord>();
        var runErrors = new List<string>();

        // 001 → where it was first seen in this run, per base.
        var seen = new Dictionary<string, Dictionary<string, string>>
        {
            ["BIB"] = new(), ["AUT"] = new(),
        };
        var existing = new Dictionary<string, HashSet<string>>
        {
            ["BIB"] = _repo.ExistingControlNumbers("BIB"),
            ["AUT"] = _repo.ExistingControlNumbers("AUT"),
        };

        foreach (var path in mrkFiles)
        {
            var result = MrkReader.Read(File.ReadAllText(path));
            fileReports.Add(new ImportFileReport(path, result.Records.Count, result.Diagnostics));

            foreach (var rec in result.Records)
            {
                string @base = rec.Kind == RecordKind.Authority ? "AUT" : "BIB";
                planned.Add(new PlannedRecord(path, @base, rec));

                string? cn = rec.ControlNumber;
                if (string.IsNullOrEmpty(cn)) continue;

                if (existing[@base].Contains(cn))
                    runErrors.Add($"{@base} 001 \"{cn}\" ({Path.GetFileName(path)}) already exists in the catalogue.");
                else if (seen[@base].TryGetValue(cn, out var firstFile))
                    runErrors.Add($"Duplicate {@base} 001 \"{cn}\": {Path.GetFileName(firstFile)} and {Path.GetFileName(path)}.");
                else
                    seen[@base][cn] = path;
            }
        }

        return new ImportPlan(new ImportReport(fileReports, runErrors, planned.Count), planned);
    }

    /// <summary>Commits an analyzed run in a single transaction (all-or-nothing).</summary>
    public ImportResult Commit(ImportPlan plan, ImportMode mode)
    {
        bool allowed = mode == ImportMode.AsPushed
            ? plan.Report.CanCommitAsPushed
            : plan.Report.CanCommitAsDrafts;
        if (!allowed)
            throw new InvalidOperationException(
                $"Import run has errors and cannot be committed {(mode == ImportMode.AsPushed ? "AS-PUSHED" : "AS-DRAFTS")}; see the report.");

        var status = mode == ImportMode.AsPushed ? RecordStatus.Pushed : RecordStatus.Draft;
        var highest = new Dictionary<string, long> { ["BIB"] = 0, ["AUT"] = 0 };
        int bib = 0, aut = 0;
        var now = DateTime.UtcNow;

        using var tx = _repo.BeginTransaction(); // disposed uncommitted = rolled back
        foreach (var p in plan.Records)
        {
            var stored = new StoredRecord(p.Base, p.Record) { Status = status };
            _repo.InsertCore(tx, stored, now);

            if (p.Base == "BIB") bib++; else aut++;
            if (long.TryParse(p.Record.ControlNumber, out long n) && n > highest[p.Base])
                highest[p.Base] = n;
        }

        foreach (var (@base, top) in highest)
            if (top > 0)
                _repo.BumpSequencePast(tx, @base, top);

        tx.Commit();
        return new ImportResult(bib + aut, bib, aut);
    }
}
