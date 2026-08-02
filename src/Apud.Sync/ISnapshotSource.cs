using Apud.Data;
using Marc.Core.Mrk;

namespace Apud.Sync;

/// <summary>One record's own <c>.mrk</c> file: name (e.g. <c>758.mrk</c>) and bytes.</summary>
public sealed record RecordFile(string Name, byte[] Content);

/// <summary>A published folder of per-record <c>.mrk</c> files — <c>bib</c> or <c>aut</c>,
/// mirroring the cataloguer's MARC_OUT layout (one file per control number).</summary>
public sealed record RecordFolder(string Name, IReadOnlyList<RecordFile> Files);

/// <summary>What a snapshot upload needs from the catalogue: a consistent database
/// copy plus the per-record <c>.mrk</c> folders to publish. Behind an interface so
/// SyncService's orchestration is testable without a real database.</summary>
public interface ISnapshotSource
{
    /// <summary>Writes a consistent database copy to <paramref name="destPath"/>.</summary>
    void WriteDatabaseCopy(string destPath);

    /// <summary>The per-record <c>.mrk</c> folders to publish under <c>latest/</c> —
    /// <c>bib/&lt;001&gt;.mrk</c> and <c>aut/&lt;001&gt;.mrk</c>, one file per pushed
    /// record. Empty when export publishing is off.</summary>
    IReadOnlyList<RecordFolder> RecordFolders();
}

/// <summary>
/// The real snapshot source: <c>VACUUM INTO</c> for the database copy, and — instead of
/// one concatenated <c>.mrk</c> — one file per record under <c>bib/</c> and <c>aut/</c>,
/// the same shape the cataloguer keeps in MARC_OUT (user request 2026-08-02). Only
/// records with a control number get a file (a draft without a 001 has no file name,
/// exactly as in MARC_OUT); the database copy still holds everything. A base with no
/// such records is skipped.
/// </summary>
public sealed class DbSnapshotSource : ISnapshotSource
{
    private readonly ApudDatabase _db;
    private readonly RecordRepository _repo;
    private readonly bool _includeExports;

    /// <summary>The server sub-folder each base publishes into, mirroring MARC_OUT /
    /// MARC_OUT_AUT: the bases are numbered independently, so bib 758.mrk and aut
    /// 758.mrk must live apart.</summary>
    public static readonly IReadOnlyList<(string Base, string Folder)> Folders =
        new[] { ("BIB", "bib"), ("AUT", "aut") };

    public DbSnapshotSource(ApudDatabase db, RecordRepository repo, bool includeExports)
    {
        _db = db;
        _repo = repo;
        _includeExports = includeExports;
    }

    public void WriteDatabaseCopy(string destPath) => _db.VacuumInto(destPath);

    public IReadOnlyList<RecordFolder> RecordFolders()
    {
        if (!_includeExports) return Array.Empty<RecordFolder>();
        var folders = new List<RecordFolder>();
        foreach (var (@base, folder) in Folders)
        {
            var files = new List<RecordFile>();
            foreach (var summary in _repo.List(@base))
            {
                if (string.IsNullOrEmpty(summary.ControlNumber)) continue; // no 001 → no file
                var stored = _repo.Load(summary.Id);
                if (stored is null) continue;
                files.Add(new RecordFile(FileName(summary.ControlNumber),
                    MrkWriter.ToBytes(new[] { stored.Record })));
            }
            if (files.Count > 0) folders.Add(new RecordFolder(folder, files));
        }
        return folders;
    }

    /// <summary>001s are plain integers; sanitise defensively so an unusual one can
    /// neither escape the folder nor break the write (mirrors RecordMirror).</summary>
    private static string FileName(string controlNumber)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(controlNumber.Select(c => invalid.Contains(c) ? '_' : c).ToArray()) + ".mrk";
    }
}
