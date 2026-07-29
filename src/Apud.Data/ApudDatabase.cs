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
    public const int SchemaVersion = 2;

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
        if (version == 1) { UpgradeV1ToV2(); version = 2; }

        Execute($"PRAGMA user_version = {SchemaVersion};");
    }

    /// <summary>
    /// v2 (Module 5a): record_fts gains the accent-folding tokenizer
    /// (unicode61 remove_diacritics 2) so "fisica" finds "Física". A contentless
    /// FTS table can't be altered, so it is dropped, recreated, and repopulated
    /// from the pushed records.
    /// </summary>
    private void UpgradeV1ToV2()
    {
        Execute("""
            DROP TABLE record_fts;
            CREATE VIRTUAL TABLE record_fts USING fts5(
              control_number, title, author, subjects, anytext,
              content='',
              tokenize='unicode61 remove_diacritics 2'
            );
            """);
        FtsIndexer.Rebuild(Connection);
    }

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
