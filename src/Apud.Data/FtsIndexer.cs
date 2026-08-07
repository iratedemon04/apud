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
///
/// Columns back the search SCOPES. <c>anytext</c> is the catch-all ("All fields");
/// every other column backs one scope and is filled per base — a record is BIB or
/// AUT, never both — so BIB tags never bleed into AUT scopes or vice-versa (e.g. a
/// 5XX field is a bib NOTE but an authority SEE-ALSO tracing).
/// </summary>
internal static class FtsIndexer
{
    /// <summary>The record_fts columns, defined ONCE: this drives the INSERT and the
    /// parameter binding here, and <see cref="ApudDatabase"/> builds the CREATE
    /// VIRTUAL TABLE statement from the same list — so the table shape and the
    /// indexer can never drift apart (a drift once broke reindexing a populated v1
    /// catalogue). Add a column here + a scope→column case in
    /// <see cref="RecordRepository.BuildMatchExpression"/> and it is searchable.</summary>
    internal static readonly string[] Columns =
    {
        "control_number",
        // BIB
        "title", "author", "subjects", "series", "publisher", "notes", "callnumber", "identifier", "local",
        // AUT — one column per 1XX heading type, plus tracings and source notes
        "h_personal", "h_corporate", "h_meeting", "h_uniform", "h_topical", "h_geographic", "h_genre",
        "variant", "related", "sources",
        // catch-all
        "anytext",
    };

    /// <summary>Indexes a record. Caller has already checked status == pushed.</summary>
    internal static void Add(SqliteConnection conn, SqliteTransaction? tx, long recordId)
    {
        var row = BuildRow(conn, tx, recordId);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT INTO record_fts (rowid, {ColumnList}) VALUES ($id, {ValueList});";
        BindRow(cmd, recordId, row);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Un-indexes a record. Must run while its field rows still exist.</summary>
    internal static void Remove(SqliteConnection conn, SqliteTransaction? tx, long recordId)
    {
        var row = BuildRow(conn, tx, recordId);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT INTO record_fts (record_fts, rowid, {ColumnList}) VALUES ('delete', $id, {ValueList});";
        BindRow(cmd, recordId, row);
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

    private static string ColumnList => string.Join(", ", Columns);
    private static string ValueList => string.Join(", ", Columns.Select(c => "$" + c));

    private static Dictionary<string, List<string>> BuildRow(SqliteConnection conn, SqliteTransaction? tx, long recordId)
    {
        var cols = new Dictionary<string, List<string>>();
        foreach (var c in Columns) cols[c] = new List<string>();

        string @base = "BIB";
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT base, control_number FROM record WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", recordId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                @base = r.GetString(0);
                string cn = r.IsDBNull(1) ? "" : r.GetString(1);
                if (cn.Length > 0) cols["control_number"].Add(cn);
            }
        }

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

                string packed = r.GetString(1);
                string text = SubfieldText(packed);
                if (text.Length == 0) continue;

                cols["anytext"].Add(text);
                if (@base == "AUT") MapAut(cols, tag, text);
                else MapBib(cols, tag, packed, text);
            }
        }

        return cols;
    }

    private static void MapBib(Dictionary<string, List<string>> cols, string tag, string packed, string text)
    {
        if (tag == "245") cols["title"].Add(text);
        else if (tag[0] is '1' or '7') cols["author"].Add(text);
        else if (tag[0] == '6') cols["subjects"].Add(text);
        else if (tag[0] == '5') cols["notes"].Add(text);
        else if (tag[0] == '9') cols["local"].Add(text);   // 9XX local/institution-defined fields

        if (IsSeriesTag(tag)) cols["series"].Add(text);
        else if (tag is "260" or "264") { var pub = SubfieldText(packed, 'b'); if (pub.Length > 0) cols["publisher"].Add(pub); }
        else if (tag is "020" or "022") cols["identifier"].Add(text);
        else if (IsCallNumberTag(tag)) cols["callnumber"].Add(text);
    }

    private static void MapAut(Dictionary<string, List<string>> cols, string tag, string text)
    {
        switch (tag)
        {
            case "100": cols["h_personal"].Add(text); break;
            case "110": cols["h_corporate"].Add(text); break;
            case "111": cols["h_meeting"].Add(text); break;
            case "130": cols["h_uniform"].Add(text); break;
            case "150": cols["h_topical"].Add(text); break;
            case "151": cols["h_geographic"].Add(text); break;
            case "155": cols["h_genre"].Add(text); break;
            case "670": case "675": cols["sources"].Add(text); break;
            default:
                if (tag[0] == '4') cols["variant"].Add(text);       // See-from tracings
                else if (tag[0] == '5') cols["related"].Add(text);  // See-also tracings (NOT notes, unlike BIB)
                break;
        }
    }

    /// <summary>Series: the 490 statement + the 800/810/811/830 added entries.
    /// Deliberately NOT 85X (852 holdings / 856 links).</summary>
    private static bool IsSeriesTag(string tag) =>
        tag == "490" || (tag[0] == '8' && tag[1] is '0' or '1' or '3');

    /// <summary>Classification / call-number fields: the 050–099 block (LC 050/090,
    /// Dewey 082/092, UDC 080, other 084, local 099, …) plus the 852 holdings
    /// shelving location.</summary>
    private static bool IsCallNumberTag(string tag) =>
        (string.CompareOrdinal(tag, "050") >= 0 && string.CompareOrdinal(tag, "099") <= 0)
        || tag == "852";

    /// <summary>Packed field content → plain text: subfield codes dropped, values joined.</summary>
    private static string SubfieldText(string packed)
    {
        var chunks = packed.Split(MarcConstants.SubfieldDelimiter, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", chunks.Where(c => c.Length > 1).Select(c => c.Substring(1)));
    }

    /// <summary>Text of just one subfield code (e.g. publisher $b, ignoring $a place/$c date).</summary>
    private static string SubfieldText(string packed, char code)
    {
        var chunks = packed.Split(MarcConstants.SubfieldDelimiter, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", chunks.Where(c => c.Length > 1 && c[0] == code).Select(c => c.Substring(1)));
    }

    private static void BindRow(SqliteCommand cmd, long recordId, Dictionary<string, List<string>> row)
    {
        cmd.Parameters.AddWithValue("$id", recordId);
        foreach (var c in Columns)
            cmd.Parameters.AddWithValue("$" + c, string.Join(" ", row[c]));
    }
}
