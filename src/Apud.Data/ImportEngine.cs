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
    /// AS-DRAFTS opens the records as unsaved working drafts in the app — never the
    /// database — so nothing blocks it. Bringing imperfect data in for fixing is the
    /// whole point, and duplicate or colliding 001s are resolved before the eventual
    /// push (user, 2026-08-08: import dirty LC records, clean them up, then push).
    /// </summary>
    public bool CanCommitAsDrafts => true;

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

public sealed record ImportResult(int RecordsImported, int BibCount, int AutCount, IReadOnlyList<long> ImportedIds);

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

    /// <summary>Commits an analyzed run into the catalogue AS PUSHED, in a single
    /// transaction (all-or-nothing) — the trusted-migration path. Import-as-drafts does
    /// NOT come here: drafts are opened as unsaved working records in the app and never
    /// touch the DB (user, 2026-08-08). Blocks if the run cannot commit as pushed.</summary>
    public ImportResult Commit(ImportPlan plan)
    {
        if (!plan.Report.CanCommitAsPushed)
            throw new InvalidOperationException("Import run has errors and cannot be committed AS-PUSHED; see the report.");

        var highest = new Dictionary<string, long> { ["BIB"] = 0, ["AUT"] = 0 };
        int bib = 0, aut = 0;
        var ids = new List<long>();
        var now = DateTime.UtcNow;

        using var tx = _repo.BeginTransaction(); // disposed uncommitted = rolled back
        foreach (var p in plan.Records)
        {
            var stored = new StoredRecord(p.Base, p.Record) { Status = RecordStatus.Pushed };
            _repo.InsertCore(tx, stored, now);
            ids.Add(stored.Id);

            if (p.Base == "BIB") bib++; else aut++;
            if (long.TryParse(p.Record.ControlNumber, out long n) && n > highest[p.Base])
                highest[p.Base] = n;
        }

        foreach (var (@base, top) in highest)
            if (top > 0)
                _repo.BumpSequencePast(tx, @base, top);

        tx.Commit();
        return new ImportResult(bib + aut, bib, aut, ids);
    }

    /// <summary>Normalizes the coded fixed fields (leader + 006/007/008) of every record
    /// in the plan, turning blank placeholders ('\' and '^' — the way LC and other sources
    /// draw an empty position) into real spaces. Mutates the plan's records in place, so it
    /// applies to both commit paths (AS-PUSHED and AS-DRAFTS); call it before Commit or
    /// ParsedRecords. 001 uniqueness is unaffected (001 is not a coded fixed field), so it
    /// is safe to run after Analyze. Returns the number of characters changed across the run
    /// (0 = every record was already clean).</summary>
    public static int Normalize(ImportPlan plan)
    {
        int changed = 0;
        foreach (var p in plan.Records)
            changed += FixedFieldNormalizer.Normalize(p.Record);
        return changed;
    }

    /// <summary>Forces LDR/09 (character coding scheme) to 'a' (Unicode) on every record in
    /// the plan, so the leader tells the truth about the UTF-8 data Apud stores. Mutates the
    /// plan's records in place; feeds both commit paths. Independent of <see cref="Normalize"/>
    /// — a caller applies either, both, or neither. Returns the count of records changed.</summary>
    public static int NormalizeEncoding(ImportPlan plan)
    {
        int changed = 0;
        foreach (var p in plan.Records)
            changed += FixedFieldNormalizer.NormalizeEncoding(p.Record);
        return changed;
    }

    /// <summary>The parsed records of a run, to open as unsaved working DRAFTS in the
    /// sidebar — the import-as-drafts path (dirty LC records to clean up before push).
    /// Nothing is written: a draft lives only in the session until Ctrl+D saves it to a
    /// draft file or Ctrl+L pushes it. Parse errors don't block — imperfect data is the
    /// whole point.</summary>
    public IReadOnlyList<(string Base, MarcRecord Record)> ParsedRecords(ImportPlan plan) =>
        plan.Records.Select(p => (p.Base, p.Record)).ToList();
}
