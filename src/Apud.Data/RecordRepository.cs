using Marc.Core;
using Microsoft.Data.Sqlite;

namespace Apud.Data;

/// <summary>
/// Persistence for MARC records. The one iron rule (docs/PLAN.md §4): fields and
/// their authority links are rewritten together in a single transaction — a save
/// can never orphan or silently drop a heading link. AuthLinkId travels on the
/// in-memory field; this class is what makes it durable.
///
/// Subfields are packed into field.content using the real MARC unit separator
/// (U+001F code value), so the packed form is unambiguous for any text that can
/// legally appear in a subfield.
/// </summary>
public sealed class RecordRepository
{
    private readonly ApudDatabase _db;

    public RecordRepository(ApudDatabase db) => _db = db;

    // ---------- save ----------

    /// <summary>Inserts a new record as a draft. Sets Id/timestamps on the instance.</summary>
    public void Insert(StoredRecord rec)
    {
        var now = DateTime.UtcNow;
        using var tx = _db.Connection.BeginTransaction();

        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO record (base, control_number, leader, status, created_utc, updated_utc)
                VALUES ($base, $cn, $leader, $status, $now, $now);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$base", rec.Base);
            cmd.Parameters.AddWithValue("$cn", (object?)rec.Record.ControlNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$leader", rec.Record.Leader);
            cmd.Parameters.AddWithValue("$status", StatusText(rec.Status));
            cmd.Parameters.AddWithValue("$now", Iso(now));
            rec.Id = (long)cmd.ExecuteScalar()!;
        }

        WriteFields(tx, rec);
        tx.Commit();

        rec.CreatedUtc = now;
        rec.UpdatedUtc = now;
    }

    /// <summary>
    /// Saves the current state of an already-inserted record: header row updated,
    /// fields + heading links rewritten atomically.
    /// </summary>
    public void Update(StoredRecord rec)
    {
        if (rec.Id == 0) throw new InvalidOperationException("Record was never inserted.");
        var now = DateTime.UtcNow;
        using var tx = _db.Connection.BeginTransaction();

        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE record SET control_number = $cn, leader = $leader,
                                  status = $status, updated_utc = $now
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$cn", (object?)rec.Record.ControlNumber ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$leader", rec.Record.Leader);
            cmd.Parameters.AddWithValue("$status", StatusText(rec.Status));
            cmd.Parameters.AddWithValue("$now", Iso(now));
            cmd.Parameters.AddWithValue("$id", rec.Id);
            cmd.ExecuteNonQuery();
        }

        Execute(tx, "DELETE FROM field WHERE record_id = $id", ("$id", rec.Id)); // links cascade
        WriteFields(tx, rec);
        tx.Commit();

        rec.UpdatedUtc = now;
    }

    private void WriteFields(SqliteTransaction tx, StoredRecord rec)
    {
        int seq = 0;
        foreach (var f in rec.Record.Fields)
        {
            long fieldId;
            using (var cmd = _db.Connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO field (record_id, seq, tag, ind1, ind2, content)
                    VALUES ($rid, $seq, $tag, $i1, $i2, $content);
                    SELECT last_insert_rowid();
                    """;
                cmd.Parameters.AddWithValue("$rid", rec.Id);
                cmd.Parameters.AddWithValue("$seq", seq++);
                cmd.Parameters.AddWithValue("$tag", f.Tag);
                cmd.Parameters.AddWithValue("$i1", f.IsControl ? DBNull.Value : f.Ind1.ToString());
                cmd.Parameters.AddWithValue("$i2", f.IsControl ? DBNull.Value : f.Ind2.ToString());
                cmd.Parameters.AddWithValue("$content", f.IsControl ? f.ControlData ?? "" : PackSubfields(f));
                fieldId = (long)cmd.ExecuteScalar()!;
            }

            if (f.AuthLinkId is long auth)
            {
                Execute(tx, "INSERT INTO heading_link (field_id, auth_record_id) VALUES ($f, $a)",
                    ("$f", fieldId), ("$a", auth));
            }
        }
    }

    // ---------- load ----------

    public StoredRecord? Load(long id)
    {
        string @base; string leader; RecordStatus status; DateTime created, updated;

        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT base, leader, status, created_utc, updated_utc FROM record WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            @base = r.GetString(0);
            leader = r.GetString(1);
            status = r.GetString(2) == "pushed" ? RecordStatus.Pushed : RecordStatus.Draft;
            created = DateTime.Parse(r.GetString(3)).ToUniversalTime();
            updated = DateTime.Parse(r.GetString(4)).ToUniversalTime();
        }

        var record = new MarcRecord { Leader = leader };
        var stored = new StoredRecord(@base, record)
        {
            Id = id, Status = status, CreatedUtc = created, UpdatedUtc = updated,
        };

        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT f.tag, f.ind1, f.ind2, f.content, hl.auth_record_id
                FROM field f LEFT JOIN heading_link hl ON hl.field_id = f.id
                WHERE f.record_id = $id ORDER BY f.seq;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var field = new MarcField(r.GetString(0));
                if (field.IsControl)
                {
                    field.ControlData = r.GetString(3);
                }
                else
                {
                    field.Ind1 = r.IsDBNull(1) ? ' ' : r.GetString(1)[0];
                    field.Ind2 = r.IsDBNull(2) ? ' ' : r.GetString(2)[0];
                    UnpackSubfields(r.GetString(3), field);
                }
                if (!r.IsDBNull(4)) field.AuthLinkId = r.GetInt64(4);
                record.Fields.Add(field);
            }
        }

        return stored;
    }

    // ---------- list / delete ----------

    public List<RecordSummary> List(string @base)
    {
        var list = new List<RecordSummary>();
        using var cmd = _db.Connection.CreateCommand();
        // Title for the list pane: 245$a for BIB; 1XX first subfield works for both bases.
        cmd.CommandText = """
            SELECT r.id, r.base, r.control_number, r.status, r.updated_utc,
                   (SELECT f.content FROM field f
                     WHERE f.record_id = r.id AND (f.tag = '245' OR f.tag LIKE '1__')
                     ORDER BY CASE WHEN f.tag = '245' THEN 0 ELSE 1 END, f.seq LIMIT 1)
            FROM record r WHERE r.base = $base
            ORDER BY CAST(r.control_number AS INTEGER), r.id;
            """;
        cmd.Parameters.AddWithValue("$base", @base);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string title = r.IsDBNull(5) ? "" : FirstSubfieldValue(r.GetString(5));
            list.Add(new RecordSummary(
                r.GetInt64(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.GetString(3) == "pushed" ? RecordStatus.Pushed : RecordStatus.Draft,
                title,
                DateTime.Parse(r.GetString(4)).ToUniversalTime()));
        }
        return list;
    }

    public void Delete(long id) =>
        Execute(null, "DELETE FROM record WHERE id = $id", ("$id", id));

    /// <summary>Ids of BIB fields linked to the given authority record (ripple/refuse-delete support).</summary>
    public long CountLinksTo(long authRecordId) =>
        (long)_db.Scalar($"SELECT COUNT(*) FROM heading_link WHERE auth_record_id = {authRecordId}")!;

    // ---------- sequence & settings ----------

    /// <summary>Next 001 for a base. Never reuses; import bumps it past existing numbers.</summary>
    public long NextControlNumber(string @base)
    {
        using var tx = _db.Connection.BeginTransaction();
        long next;
        using (var cmd = _db.Connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO sequence (base, next_value) VALUES ($base, 2)
                ON CONFLICT(base) DO UPDATE SET next_value = next_value + 1
                RETURNING next_value - 1;
                """;
            cmd.Parameters.AddWithValue("$base", @base);
            next = (long)cmd.ExecuteScalar()!;
        }
        tx.Commit();
        return next;
    }

    /// <summary>Ensures the sequence for a base will hand out numbers above <paramref name="highestSeen"/>.</summary>
    public void BumpSequencePast(string @base, long highestSeen)
    {
        Execute(null, """
            INSERT INTO sequence (base, next_value) VALUES ($base, $v)
            ON CONFLICT(base) DO UPDATE SET next_value = MAX(next_value, $v);
            """, ("$base", @base), ("$v", highestSeen + 1));
    }

    public string? GetSetting(string key)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM setting WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value) =>
        Execute(null, """
            INSERT INTO setting (key, value) VALUES ($k, $v)
            ON CONFLICT(key) DO UPDATE SET value = $v;
            """, ("$k", key), ("$v", value));

    // ---------- helpers ----------

    private static string StatusText(RecordStatus s) => s == RecordStatus.Pushed ? "pushed" : "draft";

    private static string Iso(DateTime utc) => utc.ToString("o");

    private static string PackSubfields(MarcField f)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var s in f.Subfields)
            sb.Append(MarcConstants.SubfieldDelimiter).Append(s.Code).Append(s.Value);
        return sb.ToString();
    }

    private static void UnpackSubfields(string packed, MarcField field)
    {
        if (packed.Length == 0) return;
        foreach (var chunk in packed.Split(MarcConstants.SubfieldDelimiter, StringSplitOptions.RemoveEmptyEntries))
            field.Subfields.Add(new MarcSubfield(chunk[0], chunk.Substring(1)));
    }

    private static string FirstSubfieldValue(string packed)
    {
        var chunks = packed.Split(MarcConstants.SubfieldDelimiter, StringSplitOptions.RemoveEmptyEntries);
        return chunks.Length == 0 ? "" : chunks[0].Substring(1);
    }

    private void Execute(SqliteTransaction? tx, string sql, params (string, object)[] args)
    {
        using var cmd = _db.Connection.CreateCommand();
        if (tx != null) cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }
}
