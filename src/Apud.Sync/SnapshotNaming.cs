using System.Globalization;
using System.Text.RegularExpressions;

namespace Apud.Sync;

/// <summary>
/// Snapshot file naming and retention — pure, no I/O, so it is exhaustively tested.
/// Names are <c>catalog-yyyyMMdd-HHmmss.db</c>: the zero-padded timestamp makes an
/// ordinary text sort a chronological sort, which is all retention pruning needs.
/// </summary>
public static class SnapshotNaming
{
    public const string Prefix = "catalog-";
    public const string Extension = ".db";

    private static readonly Regex Pattern =
        new(@"^catalog-(\d{8})-(\d{6})\.db$", RegexOptions.Compiled);

    public static string ForTimestamp(DateTime utc) =>
        $"{Prefix}{utc:yyyyMMdd-HHmmss}{Extension}";

    public static bool IsSnapshot(string name) => Pattern.IsMatch(name);

    /// <summary>The UTC instant encoded in a snapshot name, or <see cref="DateTime.MinValue"/>
    /// if the name is not a snapshot.</summary>
    public static DateTime TimestampOf(string name)
    {
        var m = Pattern.Match(name);
        if (!m.Success) return DateTime.MinValue;
        return DateTime.ParseExact(m.Groups[1].Value + m.Groups[2].Value, "yyyyMMddHHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    /// <summary>
    /// From a directory listing and a keep-count, the snapshot files to delete —
    /// everything past the newest <paramref name="keep"/>. Non-snapshot names (and the
    /// transient <c>.tmp</c> uploads) are ignored. <paramref name="keep"/> ≤ 0 prunes
    /// nothing: retention left blank must never be read as "delete all history".
    /// </summary>
    public static IReadOnlyList<string> ToPrune(IEnumerable<string> names, int keep)
    {
        var snapshots = names.Where(IsSnapshot)
                             .OrderByDescending(n => n, StringComparer.Ordinal)
                             .ToList();
        if (keep <= 0 || snapshots.Count <= keep) return Array.Empty<string>();
        return snapshots.Skip(keep).ToList();
    }
}
