using Marc.Core;
using Microsoft.Data.Sqlite;

namespace Apud.Data;

/// <summary>
/// Maintains the heading_index browse table over the AUT base — the counterpart to
/// <see cref="FtsIndexer"/> for authority control (docs/PLAN.md §4, §6.3; Module 8).
/// Only PUSHED authority records are indexed: a heading is browsable once it is an
/// official part of the base, exactly as drafts stay out of keyword search. Each
/// authorized 1XX, see (4XX) and see-also (5XX) becomes one row carrying its
/// normalized key (for positioned browse) and its display string.
///
/// Unlike record_fts this is a plain table, so a record's rows are simply deleted
/// and re-inserted; the heading text is always taken from the caller's record, and
/// the extraction rules live once in <see cref="Headings.Extract"/>.
/// </summary>
internal static class HeadingIndexer
{
    /// <summary>Replaces an authority record's browse rows with a fresh set built
    /// from <paramref name="record"/>. Caller has decided the record is pushed AUT.</summary>
    internal static void Index(SqliteConnection conn, SqliteTransaction? tx, long authRecordId, MarcRecord record)
    {
        Remove(conn, tx, authRecordId);
        foreach (var e in Headings.Extract(record))
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO heading_index (auth_record_id, kind, tag, normalized, display)
                VALUES ($id, $kind, $tag, $norm, $disp);
                """;
            cmd.Parameters.AddWithValue("$id", authRecordId);
            cmd.Parameters.AddWithValue("$kind", KindText(e.Kind));
            cmd.Parameters.AddWithValue("$tag", e.Tag);
            cmd.Parameters.AddWithValue("$norm", e.Normalized);
            cmd.Parameters.AddWithValue("$disp", e.Display);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Drops one authority record's browse rows (before a rewrite, or when
    /// it stops being a pushed authority record).</summary>
    internal static void Remove(SqliteConnection conn, SqliteTransaction? tx, long authRecordId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM heading_index WHERE auth_record_id = $id;";
        cmd.Parameters.AddWithValue("$id", authRecordId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Rebuilds the whole browse index from every pushed AUT record. Used
    /// by the v2→v3 migration so a catalogue imported before Module 8 gains its
    /// authority index the first time this Apud opens it.</summary>
    internal static void Rebuild(SqliteConnection conn, RecordRepository repo)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM heading_index;";
            cmd.ExecuteNonQuery();
        }

        var ids = new List<long>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM record WHERE base = 'AUT' AND status = 'pushed';";
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
        }

        foreach (var id in ids)
            if (repo.Load(id) is { } stored)
                Index(conn, null, id, stored.Record);
    }

    internal static string KindText(HeadingKind kind) => kind switch
    {
        HeadingKind.Authorized => "auth",
        HeadingKind.See => "see",
        _ => "seealso",
    };

    internal static HeadingKind KindFrom(string text) => text switch
    {
        "auth" => HeadingKind.Authorized,
        "see" => HeadingKind.See,
        _ => HeadingKind.SeeAlso,
    };
}
