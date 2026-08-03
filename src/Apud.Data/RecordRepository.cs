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

    /// <summary>Inserts a new record. Sets Id/timestamps on the instance.</summary>
    public void Insert(StoredRecord rec)
    {
        using var tx = _db.Connection.BeginTransaction();
        InsertCore(tx, rec, DateTime.UtcNow);
        tx.Commit();
    }

    /// <summary>Insert within a caller-owned transaction (import runs are all-or-nothing).</summary>
    internal void InsertCore(SqliteTransaction tx, StoredRecord rec, DateTime now)
    {
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
        if (rec.Status == RecordStatus.Pushed)
            FtsIndexer.Add(_db.Connection, tx, rec.Id);
        if (rec.Base == "AUT" && rec.Status == RecordStatus.Pushed)
            HeadingIndexer.Index(_db.Connection, tx, rec.Id, rec.Record);

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

        // Un-index BEFORE the field rows are rewritten: the contentless FTS delete
        // needs the values exactly as they were indexed.
        if (WasPushed(tx, rec.Id))
            FtsIndexer.Remove(_db.Connection, tx, rec.Id);

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
        if (rec.Status == RecordStatus.Pushed)
            FtsIndexer.Add(_db.Connection, tx, rec.Id);
        // Keep the authority browse index in step (heading_index is keyed by
        // record, not by the field rows just rewritten, so it must be redone here).
        if (rec.Base == "AUT")
        {
            if (rec.Status == RecordStatus.Pushed)
                HeadingIndexer.Index(_db.Connection, tx, rec.Id, rec.Record);
            else
                HeadingIndexer.Remove(_db.Connection, tx, rec.Id);
        }
        tx.Commit();

        rec.UpdatedUtc = now;
    }

    /// <summary>
    /// Ctrl+S (Module 6): writes the record as a DRAFT — drafts are invisible
    /// to search and must earn their pushed status through Ctrl+L (Module 9),
    /// so editing a pushed record and saving demotes it until re-pushed. The
    /// status change lives here because status is the data layer's invariant.
    /// </summary>
    public void SaveDraft(StoredRecord rec)
    {
        rec.Status = RecordStatus.Draft;
        if (rec.Id == 0) Insert(rec); else Update(rec);
    }

    private bool WasPushed(SqliteTransaction tx, long id)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT status FROM record WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() as string == "pushed";
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

    // The summary projection shared by every list path. Title: 245$a for BIB, 1XX
    // first subfield for both bases. Author: 100/110/111 heading, else first
    // 700/710/711. Year: the 260/264 $c transcription only — never the coded 008
    // date (real catalogues stuff brackets/fill into its 4-char slot, so the
    // human-entered field is the one to trust). See YearOf.
    private const string SummarySelect = """
        SELECT r.id, r.base, r.control_number, r.status, r.updated_utc,
               (SELECT f.content FROM field f
                 WHERE f.record_id = r.id AND (f.tag = '245' OR f.tag LIKE '1__')
                 ORDER BY CASE WHEN f.tag = '245' THEN 0 ELSE 1 END, f.seq LIMIT 1),
               (SELECT f.content FROM field f
                 WHERE f.record_id = r.id AND f.tag IN ('100','110','111','700','710','711')
                 ORDER BY CASE WHEN f.tag LIKE '1__' THEN 0 ELSE 1 END, f.seq LIMIT 1),
               (SELECT f.content FROM field f
                 WHERE f.record_id = r.id AND f.tag IN ('260','264') ORDER BY f.seq LIMIT 1),
               (SELECT f.content FROM field f
                 WHERE f.record_id = r.id AND (f.tag = '065' OR f.tag LIKE '08_')
                 ORDER BY CASE WHEN f.tag = '065' THEN 0 ELSE 1 END, f.seq LIMIT 1),
               (SELECT f.content FROM field f
                 WHERE f.record_id = r.id AND f.tag = '670' ORDER BY f.seq LIMIT 1)
        FROM record r
        """;

    private RecordSummary ReadSummary(SqliteDataReader r)
    {
        string @base = r.GetString(1);
        string heading = r.IsDBNull(5) ? "" : r.GetString(5);
        return new(
            r.GetInt64(0), @base,
            r.IsDBNull(2) ? null : r.GetString(2),
            r.GetString(3) == "pushed" ? RecordStatus.Pushed : RecordStatus.Draft,
            // An authority heading shows in full (all subfields joined); a bib Title
            // is the 245 title proper, so it keeps just the first subfield.
            @base == "AUT" ? AllSubfieldValues(heading) : FirstSubfieldValue(heading),
            r.IsDBNull(6) ? "" : FirstSubfieldValue(r.GetString(6)),
            YearOf(r.IsDBNull(7) ? null : r.GetString(7)),
            DateTime.Parse(r.GetString(4)).ToUniversalTime(),
            r.IsDBNull(8) ? "" : FirstSubfieldValue(r.GetString(8)),
            r.IsDBNull(9) ? "" : FirstSubfieldValue(r.GetString(9)));
    }

    /// <summary>Every record in a base, control-number order. Whole-base — used by
    /// export/backup, which must touch all rows. UI paths should prefer
    /// <see cref="ListPage"/> / <see cref="ListByIds"/> so they stay O(page), not O(base).</summary>
    public List<RecordSummary> List(string @base)
    {
        var list = new List<RecordSummary>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = SummarySelect + " WHERE r.base = $base ORDER BY CAST(r.control_number AS INTEGER), r.id;";
        cmd.Parameters.AddWithValue("$base", @base);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadSummary(r));
        return list;
    }

    /// <summary>One page of a base in control-number order — the scalable "List All".</summary>
    public List<RecordSummary> ListPage(string @base, int limit, int offset)
    {
        var list = new List<RecordSummary>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = SummarySelect +
            " WHERE r.base = $base ORDER BY CAST(r.control_number AS INTEGER), r.id LIMIT $limit OFFSET $offset;";
        cmd.Parameters.AddWithValue("$base", @base);
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadSummary(r));
        return list;
    }

    /// <summary>Summaries for a specific set of ids (e.g. the ≤200 FTS hits) — so a search
    /// hydrates only its results, never the whole base. Order is unspecified; the caller
    /// re-imposes the ranking it wants.</summary>
    public List<RecordSummary> ListByIds(IReadOnlyCollection<long> ids)
    {
        var list = new List<RecordSummary>();
        if (ids.Count == 0) return list;
        using var cmd = _db.Connection.CreateCommand();
        var names = new List<string>(ids.Count);
        int i = 0;
        foreach (long id in ids)
        {
            string p = "$id" + i++;
            names.Add(p);
            cmd.Parameters.AddWithValue(p, id);
        }
        cmd.CommandText = SummarySelect + " WHERE r.id IN (" + string.Join(",", names) + ");";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadSummary(r));
        return list;
    }

    /// <summary>Count of records in a base — a cheap COUNT(*), not a full materialisation.</summary>
    public int Count(string @base)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM record WHERE base = $base;";
        cmd.Parameters.AddWithValue("$base", @base);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Deletes a record. An authority record that still has bib fields linked to
    /// it is REFUSED (docs/PLAN.md §6.3.7) — the caller shows the linked count and
    /// the links must be moved first; this protects authority control from
    /// dangling references. (heading_index rows cascade away on delete; heading_link
    /// rows deliberately do not, which is what makes this refusal possible.)
    /// </summary>
    public void Delete(long id)
    {
        long links = CountLinksTo(id);
        if (links > 0)
            throw new InvalidOperationException(
                $"This authority record is still linked from {links} bibliographic field(s). " +
                "Move or remove those links before deleting it.");

        using var tx = _db.Connection.BeginTransaction();
        if (WasPushed(tx, id))
            FtsIndexer.Remove(_db.Connection, tx, id); // while field rows still exist
        Execute(tx, "DELETE FROM record WHERE id = $id", ("$id", id));
        tx.Commit();
    }

    // ---------- search ----------

    /// <summary>
    /// Ranked full-text search over the pushed records of one base; returns record
    /// ids, best match first. The query is end-user text ("fisica nuclear"), turned
    /// into a prefix-match on every word; accents don't matter (remove_diacritics 2).
    /// A scope restricts matching to one indexed column (title/author/subjects/001).
    /// </summary>
    public List<long> Search(string @base, string query, SearchScope scope = SearchScope.All, int limit = 200)
    {
        string match = BuildMatchExpression(query, scope);
        var ids = new List<long>();
        if (match.Length == 0) return ids;

        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT f.rowid FROM record_fts f
            JOIN record r ON r.id = f.rowid
            WHERE record_fts MATCH $match AND r.base = $base
            ORDER BY rank LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$match", match);
        cmd.Parameters.AddWithValue("$base", @base);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids;
    }

    /// <summary>
    /// User text → FTS5 query: each whitespace-separated word becomes a quoted
    /// prefix term ("fisica"*), joined by implicit AND. Quoting neutralizes FTS5
    /// operators, so arbitrary typed text can never produce a syntax error.
    /// A non-All scope wraps the terms in an FTS5 column filter.
    /// </summary>
    internal static string BuildMatchExpression(string query, SearchScope scope = SearchScope.All)
    {
        string terms = string.Join(" ",
            query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                 .Select(w => "\"" + w.Replace("\"", "\"\"") + "\"*"));
        if (terms.Length == 0 || scope == SearchScope.All) return terms;

        string column = scope switch
        {
            SearchScope.Title => "title",
            SearchScope.Author => "author",
            SearchScope.Subjects => "subjects",
            SearchScope.Series => "series",
            SearchScope.Publisher => "publisher",
            SearchScope.Notes => "notes",
            SearchScope.CallNumber => "callnumber",
            SearchScope.Isbn => "identifier",
            SearchScope.HeadingPersonal => "h_personal",
            SearchScope.HeadingCorporate => "h_corporate",
            SearchScope.HeadingMeeting => "h_meeting",
            SearchScope.HeadingUniform => "h_uniform",
            SearchScope.HeadingTopical => "h_topical",
            SearchScope.HeadingGeographic => "h_geographic",
            SearchScope.HeadingGenre => "h_genre",
            SearchScope.SeeFrom => "variant",
            SearchScope.SeeAlso => "related",
            SearchScope.Sources => "sources",
            _ => "control_number",
        };
        return $"{column} : ({terms})";
    }

    /// <summary>Ids of BIB fields linked to the given authority record (ripple/refuse-delete support).</summary>
    public long CountLinksTo(long authRecordId) =>
        (long)_db.Scalar($"SELECT COUNT(*) FROM heading_link WHERE auth_record_id = {authRecordId}")!;

    // ---------- authority browse (Module 8) ----------

    /// <summary>
    /// The authority browse list positioned at <paramref name="normalizedStart"/>
    /// (the normalized text of the bib field the cursor is on): a block of entries
    /// at/after that point plus a little context above it, all across the AUT base's
    /// authorized, see and see-also headings in normalized order. Aleph-style — the
    /// cataloguer scrolls a single interleaved index. <see cref="BrowseResult.Position"/>
    /// is the row the cursor should land on (the first entry ≥ the start point).
    /// </summary>
    public BrowseResult BrowseHeadings(string normalizedStart, int before = 40, int after = 200)
    {
        var backward = ReadHeadings(
            "WHERE normalized < $start ORDER BY normalized DESC, display DESC, auth_record_id DESC LIMIT $limit",
            normalizedStart, before);
        backward.Reverse();
        var forward = ReadHeadings(
            "WHERE normalized >= $start ORDER BY normalized, display, auth_record_id LIMIT $limit",
            normalizedStart, after);

        var entries = new List<BrowseHeading>(backward.Count + forward.Count);
        entries.AddRange(backward);
        entries.AddRange(forward);
        return new BrowseResult(entries, backward.Count);
    }

    /// <summary>The authorized (1XX) display string of one authority record, read
    /// straight from the browse index. Lets the Ctrl+F4 list render a see-reference's
    /// "→ see: authorized form" target even when the authorized heading sorts far from
    /// the variant and so is not in the same positioned window. Null if the record has
    /// no indexed authorized heading.</summary>
    public string? AuthorizedDisplayFor(long authRecordId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText =
            "SELECT display FROM heading_index WHERE auth_record_id = $id AND kind = 'auth' LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", authRecordId);
        return cmd.ExecuteScalar() as string;
    }

    private List<BrowseHeading> ReadHeadings(string whereOrder, string start, int limit)
    {
        var list = new List<BrowseHeading>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText =
            "SELECT auth_record_id, kind, tag, normalized, display FROM heading_index " + whereOrder + ";";
        cmd.Parameters.AddWithValue("$start", start);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new BrowseHeading(
                r.GetInt64(0), HeadingIndexer.KindFrom(r.GetString(1)),
                r.GetString(2), r.GetString(3), r.GetString(4)));
        return list;
    }

    /// <summary>Record ids of the BIB records with at least one field linked to
    /// the given authority record — the audience of a ripple, and the list shown
    /// when a delete is refused.</summary>
    public IReadOnlyList<long> LinkedBibIds(long authRecordId)
    {
        var ids = new List<long>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT f.record_id FROM heading_link hl
            JOIN field f ON f.id = hl.field_id
            WHERE hl.auth_record_id = $a ORDER BY f.record_id;
            """;
        cmd.Parameters.AddWithValue("$a", authRecordId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids;
    }

    /// <summary>
    /// Ripple (docs/PLAN.md §6.3.7): after an authority record's heading changes,
    /// rewrite every linked bib field to the new authorized form (relators
    /// preserved) and return how many fields were rewritten. The heavy caller is
    /// Module 9's push cycle, which owns the surrounding transaction; the rewrite
    /// logic itself lives here so it is built and tested now.
    /// </summary>
    public int RewriteLinkedBibHeadings(long authRecordId)
    {
        var auth = Load(authRecordId);
        if (auth is null) return 0;

        int count = 0;
        foreach (var bibId in LinkedBibIds(authRecordId))
        {
            var bib = Load(bibId);
            if (bib is null) continue;

            bool changed = false;
            foreach (var f in bib.Record.Fields)
                if (f.AuthLinkId == authRecordId && Headings.ApplyAuthorizedHeading(f, auth.Record))
                {
                    changed = true;
                    count++;
                }
            if (changed) Update(bib);
        }
        return count;
    }

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

    /// <summary>
    /// The highest numeric 001 currently in a base, or 0 when the base has none.
    /// This is what Module 9's push reads to assign the next control number —
    /// live MAX+1 computed at the moment of Ctrl+L, never a stored counter
    /// (Decisions: "001 SPECIALLY DUMB" — a persistent counter drifts out of step
    /// with hand-numbered batches and later collides; MAX+1 always sits one past
    /// the top, so it cannot collide, reuse, or backfill his manual gaps).
    /// Non-numeric 001s cast to 0 in SQLite and so never raise the ceiling.
    /// </summary>
    public long MaxControlNumber(string @base) => MaxControlNumber(@base, 0);

    /// <summary>As above, but not counting the record with <paramref name="exceptId"/>
    /// (pass 0 to count them all). Used for AUT push, where the record being
    /// re-pushed must not raise its own ceiling: delete an authority's 001 in an
    /// otherwise-empty base and the next number restarts at 1, instead of the
    /// record's own old number + 1 (user, 2026-08-01).</summary>
    public long MaxControlNumber(string @base, long exceptId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText =
            "SELECT MAX(CAST(control_number AS INTEGER)) FROM record " +
            "WHERE base = $b AND control_number IS NOT NULL AND id <> $id";
        cmd.Parameters.AddWithValue("$b", @base);
        cmd.Parameters.AddWithValue("$id", exceptId);
        var result = cmd.ExecuteScalar();
        return result is long v ? v : 0;
    }

    /// <summary>True when another record in the base already owns this control
    /// number — a hand-typed duplicate 001, which is a validation error the
    /// cataloguer resolves (Apud never silently renumbers).</summary>
    public bool ControlNumberUsedElsewhere(string @base, string controlNumber, long exceptId)
    {
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM record WHERE base = $b AND control_number = $cn AND id <> $id";
        cmd.Parameters.AddWithValue("$b", @base);
        cmd.Parameters.AddWithValue("$cn", controlNumber);
        cmd.Parameters.AddWithValue("$id", exceptId);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    /// <summary>Ensures the sequence for a base will hand out numbers above <paramref name="highestSeen"/>.</summary>
    public void BumpSequencePast(string @base, long highestSeen) =>
        BumpSequencePast(null, @base, highestSeen);

    internal void BumpSequencePast(SqliteTransaction? tx, string @base, long highestSeen)
    {
        Execute(tx, """
            INSERT INTO sequence (base, next_value) VALUES ($base, $v)
            ON CONFLICT(base) DO UPDATE SET next_value = MAX(next_value, $v);
            """, ("$base", @base), ("$v", highestSeen + 1));
    }

    /// <summary>All 001s currently present in a base (import duplicate check).</summary>
    internal HashSet<string> ExistingControlNumbers(string @base)
    {
        var set = new HashSet<string>();
        using var cmd = _db.Connection.CreateCommand();
        cmd.CommandText = "SELECT control_number FROM record WHERE base = $b AND control_number IS NOT NULL";
        cmd.Parameters.AddWithValue("$b", @base);
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    internal SqliteTransaction BeginTransaction() => _db.Connection.BeginTransaction();

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

    /// <summary>
    /// Year for a list row: 008 Date 1 (bytes 07-10) when it carries anything
    /// (partial dates like "19uu" are real data — shown as-is); otherwise the
    /// 260/264 $c publication date. Empty when the record has neither.
    /// </summary>
    /// <summary>
    /// Year for the list, taken from the record's own transcribed data — the
    /// 260/264 $c "as written" (e.g. "[1960]"). The coded 008 date is deliberately
    /// NOT consulted: in real catalogues its fixed 4-character slot gets brackets
    /// and fill characters typed into it (record 177's 008 held "[1960]", which
    /// showed as "[196"), so the human-entered publication date is the only one
    /// trustworthy on screen (user, 2026-07-31).
    /// </summary>
    private static string YearOf(string? packedPub)
    {
        if (packedPub != null)
            foreach (var chunk in packedPub.Split(MarcConstants.SubfieldDelimiter, StringSplitOptions.RemoveEmptyEntries))
                if (chunk.Length > 0 && chunk[0] == 'c') return chunk.Substring(1).Trim();
        return "";
    }

    private static string FirstSubfieldValue(string packed)
    {
        var chunks = packed.Split(MarcConstants.SubfieldDelimiter, StringSplitOptions.RemoveEmptyEntries);
        return chunks.Length == 0 ? "" : chunks[0].Substring(1);
    }

    /// <summary>The full heading: every subfield value joined by "--", so an
    /// authority's subdivisions show as "Física--Investigación" (user, 2026-08-02).
    /// The separator is the same for every subfield type — topical, form, whatever —
    /// no per-code MARC punctuation is invented.</summary>
    private static string AllSubfieldValues(string packed)
    {
        var chunks = packed.Split(MarcConstants.SubfieldDelimiter, StringSplitOptions.RemoveEmptyEntries);
        return string.Join("--", chunks.Select(c => c.Substring(1)));
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
