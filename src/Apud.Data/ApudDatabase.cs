using Microsoft.Data.Sqlite;

namespace Apud.Data;

/// <summary>
/// Owns the SQLite connection and the schema. One database file = one catalogue.
/// Single-connection by design: Apud is single-machine software (docs/PLAN.md,
/// Philosophy) and a desktop app needs no connection pool.
///
/// Forward migrations only: <see cref="SchemaVersion"/> is bumped when DDL changes,
/// with an upgrade branch per historical version. v1 creates the full planned schema
/// (including heading_link / heading_index / record_fts, used from Modules 5 and 8)
/// so record persistence never needs a schema change to gain those features.
/// </summary>
public sealed class ApudDatabase : IDisposable
{
    public const int SchemaVersion = 6;

    public SqliteConnection Connection { get; }

    /// <summary>Opens (creating and/or migrating if needed) a catalogue database file.</summary>
    public static ApudDatabase Open(string path) =>
        new(new SqliteConnectionStringBuilder { DataSource = path }.ToString());

    /// <summary>A private in-memory database — used by tests.</summary>
    public static ApudDatabase OpenInMemory() => new("Data Source=:memory:");

    private ApudDatabase(string connectionString)
    {
        Connection = new SqliteConnection(connectionString);
        Connection.Open();

        Execute("PRAGMA foreign_keys = ON;");
        if (!Connection.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
            Execute("PRAGMA journal_mode = WAL;");

        Migrate();
    }

    private void Migrate()
    {
        long version = (long)(Scalar("PRAGMA user_version;") ?? 0L);

        if (version > SchemaVersion)
            throw new InvalidOperationException(
                $"This catalogue was created by a newer Apud (schema v{version}; this Apud knows v{SchemaVersion}).");

        if (version == 0) { CreateSchemaV1(); version = 1; }
        if (version == 1) { RebuildFtsToCurrentShape(); version = 2; }  // v2: accent-folding tokenizer
        if (version == 2) { UpgradeV2ToV3(); version = 3; }             // v3: authority browse index
        if (version == 3) { RebuildFtsToCurrentShape(); version = 4; }  // v4: notes + call-number scopes
        if (version == 4) { RebuildFtsToCurrentShape(); version = 5; }  // v5: per-heading + series/publisher/isbn scopes
        if (version == 5) { RebuildFtsToCurrentShape(); version = 6; }  // v6: local 9XX scope

        Execute($"PRAGMA user_version = {SchemaVersion};");
    }

    /// <summary>
    /// The one current record_fts definition, built from <see cref="FtsIndexer.Columns"/>
    /// so the table shape and the indexer are literally the same list. record_fts is a
    /// contentless, fully rebuildable index — not user data — so every version bump that
    /// changes its columns just drops it, recreates it to the current shape, and reindexes
    /// (a catalogue from an earlier Apud gains the new scopes on first open, no re-import).
    /// One definition means a migration's reindex can never drift from the indexer, which
    /// once broke reindexing a populated v1 catalogue.
    /// </summary>
    private static readonly string CreateFtsTableSql =
        "CREATE VIRTUAL TABLE record_fts USING fts5(\n  " +
        string.Join(", ", FtsIndexer.Columns) +
        ",\n  content='',\n  tokenize='unicode61 remove_diacritics 2'\n);";

    private void RebuildFtsToCurrentShape()
    {
        Execute("DROP TABLE record_fts;\n" + CreateFtsTableSql);
        FtsIndexer.Rebuild(Connection);
    }

    /// <summary>
    /// v3 (Module 8): the heading_index table has existed since v1 but nothing
    /// populated it until authority browse arrived. Building it from every pushed
    /// AUT record here means a catalogue imported under an earlier Apud gains its
    /// authority browse index the first time this version opens it — no re-import.
    /// </summary>
    private void UpgradeV2ToV3() => HeadingIndexer.Rebuild(Connection, new RecordRepository(this));

    private void CreateSchemaV1()
    {
        Execute("""
            CREATE TABLE record (
              id             INTEGER PRIMARY KEY,
              base           TEXT NOT NULL CHECK (base IN ('BIB','AUT')),
              control_number TEXT,
              leader         TEXT NOT NULL,
              status         TEXT NOT NULL CHECK (status IN ('draft','pushed')),
              created_utc    TEXT NOT NULL,
              updated_utc    TEXT NOT NULL
            );

            -- 001 must be unique per base among records that actually have one.
            CREATE UNIQUE INDEX ux_record_control
              ON record(base, control_number) WHERE control_number IS NOT NULL;

            CREATE TABLE field (
              id        INTEGER PRIMARY KEY,
              record_id INTEGER NOT NULL REFERENCES record(id) ON DELETE CASCADE,
              seq       INTEGER NOT NULL,
              tag       TEXT    NOT NULL,
              ind1      TEXT,
              ind2      TEXT,
              content   TEXT    NOT NULL
            );
            CREATE INDEX ix_field_record ON field(record_id, seq);

            CREATE TABLE heading_link (
              field_id       INTEGER PRIMARY KEY REFERENCES field(id) ON DELETE CASCADE,
              auth_record_id INTEGER NOT NULL REFERENCES record(id)
            );
            CREATE INDEX ix_heading_link_auth ON heading_link(auth_record_id);

            CREATE TABLE heading_index (
              auth_record_id INTEGER NOT NULL REFERENCES record(id) ON DELETE CASCADE,
              kind        TEXT NOT NULL CHECK (kind IN ('auth','see','seealso')),
              tag         TEXT NOT NULL,
              normalized  TEXT NOT NULL,
              display     TEXT NOT NULL
            );
            CREATE INDEX ix_heading_norm ON heading_index(normalized);

            CREATE TABLE sequence (base TEXT PRIMARY KEY, next_value INTEGER NOT NULL);
            CREATE TABLE setting  (key TEXT PRIMARY KEY, value TEXT NOT NULL);

            CREATE VIRTUAL TABLE record_fts USING fts5(
              control_number, title, author, subjects, anytext,
              content=''
            );
            """);
    }

    /// <summary>
    /// Writes a transactionally consistent, compacted copy of this catalogue to
    /// <paramref name="destPath"/> via SQLite's <c>VACUUM INTO</c>. Consistent even
    /// mid-session with the connection open and WAL active — the copy never captures
    /// a half-written page. The Sync module (docs/PLAN.md §9b) uploads this copy so a
    /// backup is always a whole, openable database. Overwrites any file already there.
    /// </summary>
    public void VacuumInto(string destPath)
    {
        if (System.IO.File.Exists(destPath)) System.IO.File.Delete(destPath);
        using var cmd = Connection.CreateCommand();
        // The INTO argument is an SQL expression (SQLite ≥ 3.27), so it binds safely.
        cmd.CommandText = "VACUUM INTO $dest;";
        cmd.Parameters.AddWithValue("$dest", destPath);
        cmd.ExecuteNonQuery();
    }

    internal void Execute(string sql)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    internal object? Scalar(string sql)
    {
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public void Dispose() => Connection.Dispose();
}
