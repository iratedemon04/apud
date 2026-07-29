using Marc.Core;
using Microsoft.Data.Sqlite;

namespace Apud.Data;

/// <summary>
/// Maintains the contentless record_fts table. Only PUSHED records are indexed —
/// drafts stay outside search until pushed (docs/STATE.md, Module 5a). Because the
/// table is contentless (content=''), a row can only be removed by a 'delete'
/// command carrying the exact column values that were inserted; those values are
/// therefore always rebuilt from the field rows in the database, never from an
/// in-memory record that may have drifted.
/// </summary>
internal static class FtsIndexer
{
    /// <summary>Indexes a record. Caller has already checked status == pushed.</summary>
    internal static void Add(SqliteConnection conn, SqliteTransaction? tx, long recordId)
    {
        var row = BuildRow(conn, tx, recordId);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO record_fts (rowid, control_number, title, author, subjects, anytext)
            VALUES ($id, $cn, $title, $author, $subjects, $anytext);
            """;
        AddRowParameters(cmd, recordId, row);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Un-indexes a record. Must run while its field rows still exist.</summary>
    internal static void Remove(SqliteConnection conn, SqliteTransaction? tx, long recordId)
    {
        var row = BuildRow(conn, tx, recordId);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO record_fts (record_fts, rowid, control_number, title, author, subjects, anytext)
            VALUES ('delete', $id, $cn, $title, $author, $subjects, $anytext);
            """;
        AddRowParameters(cmd, recordId, row);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Drops the whole index and re-indexes every pushed record.</summary>
    internal static void Rebuild(SqliteConnection conn)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO record_fts (record_fts) VALUES ('delete-all');";
            cmd.ExecuteNonQuery();
        }

        var ids = new List<long>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM record WHERE status = 'pushed';";
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
        }

        foreach (var id in ids)
            Add(conn, null, id);
    }

    private readonly record struct FtsRow(string ControlNumber, string Title, string Author, string Subjects, string Anytext);

    private static FtsRow BuildRow(SqliteConnection conn, SqliteTransaction? tx, long recordId)
    {
        string cn = "";
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT control_number FROM record WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", recordId);
            cn = cmd.ExecuteScalar() as string ?? "";
        }

        var title = new List<string>();
        var author = new List<string>();
        var subjects = new List<string>();
        var anytext = new List<string>();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT tag, content FROM field WHERE record_id = $id ORDER BY seq;";
            cmd.Parameters.AddWithValue("$id", recordId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string tag = r.GetString(0);
                if (string.CompareOrdinal(tag, "010") < 0) continue; // control fields carry no searchable prose

                string text = SubfieldText(r.GetString(1));
                if (text.Length == 0) continue;

                anytext.Add(text);
                if (tag == "245") title.Add(text);
                else if (tag[0] is '1' or '7') author.Add(text);
                else if (tag[0] == '6') subjects.Add(text);
            }
        }

        return new FtsRow(cn,
            string.Join(" ", title), string.Join(" ", author),
            string.Join(" ", subjects), string.Join(" ", anytext));
    }

    /// <summary>Packed field content → plain text: subfield codes dropped, values joined.</summary>
    private static string SubfieldText(string packed)
    {
        var chunks = packed.Split(MarcConstants.SubfieldDelimiter, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", chunks.Where(c => c.Length > 1).Select(c => c.Substring(1)));
    }

    private static void AddRowParameters(SqliteCommand cmd, long recordId, FtsRow row)
    {
        cmd.Parameters.AddWithValue("$id", recordId);
        cmd.Parameters.AddWithValue("$cn", row.ControlNumber);
        cmd.Parameters.AddWithValue("$title", row.Title);
        cmd.Parameters.AddWithValue("$author", row.Author);
        cmd.Parameters.AddWithValue("$subjects", row.Subjects);
        cmd.Parameters.AddWithValue("$anytext", row.Anytext);
    }
}
