using System.Globalization;
using System.Text.RegularExpressions;

namespace Apud.Sync;

/// <summary>
/// Snapshot file naming and retention — pure, no I/O, so it is exhaustively tested.
/// Names are <c>&lt;catalogue&gt;-yyyyMMdd-HHmmss.db</c>: the base name is the catalogue's
/// own file name (a <c>CATALOGO.db</c> backs up as <c>CATALOGO-20260802-210034.db</c>), so a
/// restored snapshot keeps the catalogue's identity instead of a generic "catalog", and two
/// catalogues sharing a server root can never match — hence prune — each other's snapshots.
/// The zero-padded timestamp makes an ordinary text sort a chronological sort, which is all
/// retention pruning needs.
/// </summary>
public static class SnapshotNaming
{
    public const string Extension = ".db";

    /// <summary>Guards against an empty catalogue name so a name is always well-formed.</summary>
    public static string SafeBase(string? catalogueName) =>
        string.IsNullOrWhiteSpace(catalogueName) ? "catalog" : catalogueName.Trim();

    public static string ForTimestamp(string catalogueName, DateTime utc) =>
        $"{SafeBase(catalogueName)}-{utc:yyyyMMdd-HHmmss}{Extension}";

    private static Regex PatternFor(string catalogueName) =>
        new("^" + Regex.Escape(SafeBase(catalogueName)) + @"-(\d{8})-(\d{6})\.db$");

    public static bool IsSnapshot(string catalogueName, string name) =>
        PatternFor(catalogueName).IsMatch(name);

    /// <summary>The UTC instant encoded in a snapshot name, or <see cref="DateTime.MinValue"/>
    /// if the name is not a snapshot for this catalogue.</summary>
    public static DateTime TimestampOf(string catalogueName, string name)
    {
        var m = PatternFor(catalogueName).Match(name);
        if (!m.Success) return DateTime.MinValue;
        return DateTime.ParseExact(m.Groups[1].Value + m.Groups[2].Value, "yyyyMMddHHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    /// <summary>
    /// From a directory listing and a keep-count, the snapshot files to delete —
    /// everything past the newest <paramref name="keep"/>. Names that are not THIS
    /// catalogue's snapshots (and the transient <c>.tmp</c> uploads) are ignored, so
    /// pruning one catalogue never touches another's history. <paramref name="keep"/> ≤ 0
    /// prunes nothing: retention left blank must never be read as "delete all history".
    /// </summary>
    public static IReadOnlyList<string> ToPrune(string catalogueName, IEnumerable<string> names, int keep)
    {
        var snapshots = names.Where(n => IsSnapshot(catalogueName, n))
                             .OrderByDescending(n => n, StringComparer.Ordinal)
                             .ToList();
        if (keep <= 0 || snapshots.Count <= keep) return Array.Empty<string>();
        return snapshots.Skip(keep).ToList();
    }
}
