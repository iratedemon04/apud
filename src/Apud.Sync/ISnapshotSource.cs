using Apud.Data;

namespace Apud.Sync;

/// <summary>What a snapshot upload needs from the catalogue: a consistent database
/// copy plus any plain-text exports to publish. Behind an interface so SyncService's
/// orchestration is testable without a real database.</summary>
public interface ISnapshotSource
{
    /// <summary>Writes a consistent database copy to <paramref name="destPath"/>.</summary>
    void WriteDatabaseCopy(string destPath);

    /// <summary>Exports to publish under <c>latest/</c> as (name, bytes) — e.g.
    /// <c>BIB.mrk</c>, <c>AUT.mrk</c>. Empty when export publishing is off.</summary>
    IReadOnlyList<(string Name, byte[] Content)> Exports();
}

/// <summary>
/// The real snapshot source: <c>VACUUM INTO</c> for the database copy and
/// <see cref="ExportEngine"/> for the per-base <c>.mrk</c> publications. A base with
/// no records is skipped (nothing to publish). Exports are cut (docs/PLAN.md decision:
/// .mrk is Apud's only format) so the server copy is consumable without SQLite.
/// </summary>
public sealed class DbSnapshotSource : ISnapshotSource
{
    private readonly ApudDatabase _db;
    private readonly RecordRepository _repo;
    private readonly bool _includeExports;

    public DbSnapshotSource(ApudDatabase db, RecordRepository repo, bool includeExports)
    {
        _db = db;
        _repo = repo;
        _includeExports = includeExports;
    }

    public void WriteDatabaseCopy(string destPath) => _db.VacuumInto(destPath);

    public IReadOnlyList<(string Name, byte[] Content)> Exports()
    {
        if (!_includeExports) return Array.Empty<(string, byte[])>();
        var engine = new ExportEngine(_repo);
        var result = new List<(string, byte[])>();
        foreach (string @base in new[] { "BIB", "AUT" })
            if (_repo.List(@base).Count > 0)
                result.Add(($"{@base}.mrk", engine.ExportBase(@base)));
        return result;
    }
}
