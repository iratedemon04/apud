using Marc.Core;
using Marc.Core.Mrk;

namespace Apud.App;

/// <summary>
/// Mirrors each pushed record to its own .mrk file in a MARC output folder: one
/// file per control number — e.g. <c>758.mrk</c> — so pushed records are usable
/// as plain MARC text on disk, not only inside the database. The database stays
/// the source of truth (design law #3); this is a one-way export written at push
/// time, and a record's file is removed when the record is deleted.
///
/// The folder is chosen by the cataloguer (File → Set MARC Output Folder…, stored
/// per catalogue in the <c>setting</c> table); when none is set it defaults to a
/// <c>MARC_OUT</c> subfolder beside the .db. This is a deliberate, user-approved
/// exception to the "creates nothing on its own" rule (Decisions): the folder is
/// made on demand and files are written without asking, because that is exactly
/// the workflow he asked for. It only ever touches that output folder.
/// </summary>
public static class RecordMirror
{
    public const string DefaultFolderName = "MARC_OUT";

    /// <summary>The default output folder for a catalogue — a MARC_OUT subfolder
    /// beside its .db — or null when the path is empty or in-memory.</summary>
    public static string? DefaultFolderFor(string? catalogPath)
    {
        if (string.IsNullOrEmpty(catalogPath) ||
            catalogPath.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
            return null;
        var dir = Path.GetDirectoryName(Path.GetFullPath(catalogPath));
        return dir is null ? null : Path.Combine(dir, DefaultFolderName);
    }

    /// <summary>Writes (or overwrites) &lt;folder&gt;\&lt;001&gt;.mrk for a pushed
    /// record. Skipped (returns null) when there is no folder or no control number.
    /// The folder is created if missing.</summary>
    public static string? Write(string? folder, MarcRecord record)
    {
        string? cn = record.ControlNumber;
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(cn)) return null;
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, FileName(cn));
        File.WriteAllBytes(file, MrkWriter.ToBytes(new[] { record }));
        return file;
    }

    /// <summary>Deletes &lt;folder&gt;\&lt;001&gt;.mrk if it is there; a missing
    /// file is simply nothing to do.</summary>
    public static void Delete(string? folder, string? controlNumber)
    {
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(controlNumber)) return;
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
