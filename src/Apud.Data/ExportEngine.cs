using Marc.Core;
using Marc.Core.Mrk;

namespace Apud.Data;

/// <summary>
/// Headless export: whole base or an id selection → one .mrk in the canonical
/// on-disk form (UTF-8 without BOM, LF — MrkWriter.ToBytes). A whole-base export
/// includes drafts too: export is backup/interchange, not publication.
/// </summary>
public sealed class ExportEngine
{
    private readonly RecordRepository _repo;

    public ExportEngine(RecordRepository repo) => _repo = repo;

    /// <summary>Every record of a base, in control-number order (the List order).</summary>
    public byte[] ExportBase(string @base) =>
        Export(_repo.List(@base).Select(s => s.Id));

    /// <summary>The selected records, in the order given.</summary>
    public byte[] Export(IEnumerable<long> ids)
    {
        var records = new List<MarcRecord>();
        foreach (long id in ids)
        {
            var stored = _repo.Load(id)
                ?? throw new InvalidOperationException($"Record {id} does not exist.");
            records.Add(stored.Record);
        }
        return MrkWriter.ToBytes(records);
    }

    public void ExportBaseToFile(string @base, string path) =>
        File.WriteAllBytes(path, ExportBase(@base));

    public void ExportToFile(IEnumerable<long> ids, string path) =>
        File.WriteAllBytes(path, Export(ids));
}
