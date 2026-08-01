using Marc.Core;
using Marc.Core.Mrk;

namespace Apud.App;

/// <summary>
/// Mirrors each pushed record to its own .mrk file in a <c>MARC_OUT</c> folder
/// beside the catalogue's .db (user request, 2026-07-31): one file per control
/// number — e.g. <c>MARC_OUT\758.mrk</c> — so pushed records are usable as plain
/// MARC text on disk, not only inside the database. The database stays the source
/// of truth (design law #3); this is a one-way export written at push time, and
/// a record's file is removed when the record is deleted.
///
/// This is a deliberate, user-approved exception to the "creates nothing on its
/// own" rule (Decisions): the folder is made on demand and files are written
/// without asking, because that is exactly the workflow he asked for. It only
/// ever touches the MARC_OUT folder — never his read-only source data.
/// </summary>
public static class RecordMirror
{
    public const string FolderName = "MARC_OUT";

    /// <summary>The MARC_OUT folder for a catalogue (a subfolder beside its .db),
    /// or null when the path is empty or in-memory.</summary>
    public static string? FolderFor(string? catalogPath)
    {
        if (string.IsNullOrEmpty(catalogPath) ||
            catalogPath.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
            return null;
        var dir = Path.GetDirectoryName(Path.GetFullPath(catalogPath));
        return dir is null ? null : Path.Combine(dir, FolderName);
    }

    /// <summary>Writes (or overwrites) MARC_OUT\&lt;001&gt;.mrk for a pushed record.
    /// A record without a control number is skipped. Returns the path written, or null.</summary>
    public static string? Write(string? catalogPath, MarcRecord record)
    {
        string? cn = record.ControlNumber;
        if (FolderFor(catalogPath) is not string folder || string.IsNullOrEmpty(cn)) return null;
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, FileName(cn));
        File.WriteAllBytes(file, MrkWriter.ToBytes(new[] { record }));
        return file;
    }

    /// <summary>Deletes MARC_OUT\&lt;001&gt;.mrk if it is there; a missing file is
    /// simply nothing to do (the record may have been pushed before mirroring
    /// existed, e.g. 758).</summary>
    public static void Delete(string? catalogPath, string? controlNumber)
    {
        if (FolderFor(catalogPath) is not string folder || string.IsNullOrEmpty(controlNumber)) return;
        string file = Path.Combine(folder, FileName(controlNumber));
        if (File.Exists(file)) File.Delete(file);
    }

    /// <summary>001s are plain integers here; sanitise defensively so an unusual
    /// one can neither escape the folder nor break the write.</summary>
    private static string FileName(string controlNumber)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(controlNumber.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return safe + ".mrk";
    }
}
