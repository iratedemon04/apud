using Apud.Data;
using Apud.Sync;
using Marc.Core;

namespace Apud.Tests;

/// <summary>
/// Module 11 — the SFTP backup/publish engine (docs/PLAN.md §9b). The transport
/// (SSH.NET) needs a real server, so it is behind <see cref="ISftpTransport"/> and
/// exercised here through <see cref="FakeSftpTransport"/> — an in-memory server that
/// lets the whole snapshot → atomic upload → prune → restore flow be tested offline.
/// VacuumInto and the pure naming/retention/settings logic are tested directly.
/// </summary>
public class SyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apud-sync-" + Guid.NewGuid().ToString("N"));

    public SyncTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ---------- SnapshotNaming (pure) ----------

    [Fact]
    public void Snapshot_name_encodes_the_timestamp_and_round_trips()
    {
        var utc = new DateTime(2026, 8, 1, 9, 7, 3, DateTimeKind.Utc);
        string name = SnapshotNaming.ForTimestamp(utc);

        Assert.Equal("catalog-20260801-090703.db", name);
        Assert.True(SnapshotNaming.IsSnapshot(name));
        Assert.Equal(utc, SnapshotNaming.TimestampOf(name));
    }

    [Fact]
    public void Non_snapshot_names_are_rejected()
    {
        Assert.False(SnapshotNaming.IsSnapshot("catalog.db"));
        Assert.False(SnapshotNaming.IsSnapshot("catalog-20260801-090703.db.tmp"));
        Assert.False(SnapshotNaming.IsSnapshot("BIB.mrk"));
        Assert.Equal(DateTime.MinValue, SnapshotNaming.TimestampOf("nope"));
    }

    [Fact]
    public void ToPrune_keeps_the_newest_N_and_ignores_non_snapshots()
    {
        var names = new[]
        {
            "catalog-20260801-000001.db",
            "catalog-20260801-000003.db",
            "catalog-20260801-000002.db",
            "catalog.db",                     // latest/ style — never a dated snapshot
            "catalog-20260801-000004.db.tmp", // an interrupted upload — ignored
            "BIB.mrk",
        };

        var prune = SnapshotNaming.ToPrune(names, keep: 2);

        // Newest two (…0003, …0002) kept; the oldest (…0001) pruned; junk untouched.
        Assert.Equal(new[] { "catalog-20260801-000001.db" }, prune);
    }

    [Fact]
    public void ToPrune_of_zero_or_fewer_keeps_everything()
    {
        var names = new[] { "catalog-20260801-000001.db", "catalog-20260801-000002.db" };
        Assert.Empty(SnapshotNaming.ToPrune(names, keep: 0));
        Assert.Empty(SnapshotNaming.ToPrune(names, keep: -5));
        Assert.Empty(SnapshotNaming.ToPrune(names, keep: 2));  // exactly N → nothing to drop
    }

    // ---------- SyncSettings round-trip ----------

    [Fact]
    public void Settings_round_trip_through_the_setting_table()
    {
        using var db = ApudDatabase.OpenInMemory();
        var repo = new RecordRepository(db);

        var s = new SyncSettings
        {
            Host = "box.example.org",
            Port = 2222,
            User = "cat",
            KeyPath = @"C:\keys\id_ed25519",
            RemoteRoot = "catalogues/apud",
            Retention = 7,
            UploadExport = false,
            HostFingerprint = "SHA256:abc123",
        };
        s.Save(repo);

        var loaded = SyncSettings.Load(repo);
        Assert.Equal("box.example.org", loaded.Host);
        Assert.Equal(2222, loaded.Port);
        Assert.Equal("cat", loaded.User);
        Assert.Equal(@"C:\keys\id_ed25519", loaded.KeyPath);
        Assert.Equal("catalogues/apud", loaded.RemoteRoot);
        Assert.Equal(7, loaded.Retention);
        Assert.False(loaded.UploadExport);
        Assert.Equal("SHA256:abc123", loaded.HostFingerprint);
        Assert.True(loaded.IsConfigured);
    }

    [Fact]
    public void Fresh_settings_are_sensible_defaults_and_not_configured()
    {
        using var db = ApudDatabase.OpenInMemory();
        var loaded = SyncSettings.Load(new RecordRepository(db));

        Assert.Equal(22, loaded.Port);
        Assert.Equal("apud", loaded.RemoteRoot);
        Assert.Equal(10, loaded.Retention);
        Assert.True(loaded.UploadExport);
        Assert.Null(loaded.HostFingerprint);
        Assert.False(loaded.IsConfigured);
    }

    // ---------- VacuumInto (real SQLite, no network) ----------

    [Fact]
    public void VacuumInto_writes_a_consistent_openable_copy()
    {
        string src = Path.Combine(_dir, "catalog.db");
        using (var db = ApudDatabase.Open(src))
        {
            var repo = new RecordRepository(db);
            repo.Insert(Bib("1", "Física cuántica"));
            repo.Insert(Bib("2", "Álgebra"));

            string copy = Path.Combine(_dir, "snapshot.db");
            db.VacuumInto(copy);                       // mid-session, connection open
            Assert.True(File.Exists(copy));

            using var reopened = ApudDatabase.Open(copy);
            var copyRepo = new RecordRepository(reopened);
            Assert.Equal(2, copyRepo.List("BIB").Count);
        }
    }

    [Fact]
    public void VacuumInto_overwrites_an_existing_file()
    {
        using var db = ApudDatabase.Open(Path.Combine(_dir, "catalog.db"));
        string copy = Path.Combine(_dir, "snapshot.db");
        db.VacuumInto(copy);
        db.VacuumInto(copy);   // second time must not throw on the existing file
        Assert.True(File.Exists(copy));
    }

    // ---------- SyncService orchestration (fake transport) ----------

    [Fact]
    public void Upload_publishes_snapshot_plus_latest_and_prunes_old_history()
    {
        var server = new FakeSftpTransport();
        // Seed three old snapshots so a keep-2 upload leaves exactly two of the newest.
        server.Files["apud/snapshots/catalog-20260101-000001.db"] = new byte[] { 1 };
        server.Files["apud/snapshots/catalog-20260101-000002.db"] = new byte[] { 2 };
        server.Files["apud/snapshots/catalog-20260101-000003.db"] = new byte[] { 3 };

        var service = new SyncService(() => server);
        var settings = new SyncSettings { Host = "h", User = "u", KeyPath = "k", RemoteRoot = "apud", Retention = 2 };
        var source = new FakeSnapshotSource(dbBytes: new byte[] { 9, 9 },
            exports: new[] { ("BIB.mrk", new byte[] { 1 }), ("AUT.mrk", new byte[] { 2 }) });

        var utc = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var result = service.Upload(source, settings, utc);

        string newName = "catalog-20260801-120000.db";
        Assert.Equal($"apud/snapshots/{newName}", result.RemoteSnapshot);

        // The dated snapshot and both latest/ paths exist as FINAL files (no leftover .tmp).
        Assert.True(server.Files.ContainsKey($"apud/snapshots/{newName}"));
        Assert.True(server.Files.ContainsKey("apud/latest/catalog.db"));
        Assert.True(server.Files.ContainsKey("apud/latest/BIB.mrk"));
        Assert.True(server.Files.ContainsKey("apud/latest/AUT.mrk"));
        Assert.DoesNotContain(server.Files.Keys, k => k.EndsWith(".tmp"));

        // Retention: keep the newest 2 (the just-uploaded one + …0003), prune …0001 and …0002.
        Assert.Equal(2, result.Pruned);
        Assert.False(server.Files.ContainsKey("apud/snapshots/catalog-20260101-000001.db"));
        Assert.False(server.Files.ContainsKey("apud/snapshots/catalog-20260101-000002.db"));
        Assert.True(server.Files.ContainsKey("apud/snapshots/catalog-20260101-000003.db"));

        Assert.Equal(new[] { "BIB.mrk", "AUT.mrk" }, result.Exports);
    }

    [Fact]
    public void Upload_is_atomic_every_final_file_arrives_via_tmp_then_rename()
    {
        var server = new FakeSftpTransport();
        var service = new SyncService(() => server);
        var settings = new SyncSettings { Host = "h", User = "u", KeyPath = "k", RemoteRoot = "apud", Retention = 5 };

        service.Upload(new FakeSnapshotSource(new byte[] { 7 }, Array.Empty<(string, byte[])>()),
            settings, new DateTime(2026, 8, 1, 8, 0, 0, DateTimeKind.Utc));

        // Every write to a final path was preceded by a matching .tmp upload then a rename.
        foreach (string final in new[] { "apud/snapshots/catalog-20260801-080000.db", "apud/latest/catalog.db" })
        {
            Assert.Contains(final + ".tmp", server.UploadLog);
            Assert.Contains((final + ".tmp", final), server.RenameLog);
        }
    }

    [Fact]
    public void ListSnapshots_returns_only_snapshots_newest_first()
    {
        var server = new FakeSftpTransport();
        server.Files["apud/snapshots/catalog-20260801-000002.db"] = new byte[] { 2 };
        server.Files["apud/snapshots/catalog-20260801-000001.db"] = new byte[] { 1 };
        server.Files["apud/snapshots/notes.txt"] = new byte[] { 0 };

        var service = new SyncService(() => server);
        var settings = new SyncSettings { RemoteRoot = "apud" };

        var list = service.ListSnapshots(settings);

        Assert.Equal(2, list.Count);
        Assert.Equal("catalog-20260801-000002.db", list[0].Name); // newest first
        Assert.Equal("catalog-20260801-000001.db", list[1].Name);
    }

    [Fact]
    public void Download_writes_the_snapshot_bytes_locally()
    {
        var server = new FakeSftpTransport();
        server.Files["apud/snapshots/catalog-20260801-000001.db"] = new byte[] { 4, 2 };

        var service = new SyncService(() => server);
        string local = Path.Combine(_dir, "restored.db");
        service.Download(new SyncSettings { RemoteRoot = "apud" }, "catalog-20260801-000001.db", local);

        Assert.Equal(new byte[] { 4, 2 }, File.ReadAllBytes(local));
    }

    [Fact]
    public void Upload_reports_the_fingerprint_the_transport_saw()
    {
        var server = new FakeSftpTransport { SeenFingerprint = "SHA256:seen" };
        var service = new SyncService(() => server);
        var result = service.Upload(new FakeSnapshotSource(new byte[] { 1 }, Array.Empty<(string, byte[])>()),
            new SyncSettings { RemoteRoot = "apud", Retention = 5 },
            new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal("SHA256:seen", result.SeenFingerprint);
    }

    private static StoredRecord Bib(string cn, string title)
    {
        var r = new MarcRecord { Leader = "00000nam a2200000 i 4500" };
        r.Fields.Add(new MarcField("001") { ControlData = cn });
        var t = new MarcField("245");
        t.Subfields.Add(new MarcSubfield('a', title));
        r.Fields.Add(t);
        return new StoredRecord("BIB", r);
    }

    // ---------- fakes ----------

    private sealed class FakeSnapshotSource : ISnapshotSource
    {
        private readonly byte[] _dbBytes;
        private readonly IReadOnlyList<(string, byte[])> _exports;
        public FakeSnapshotSource(byte[] dbBytes, IReadOnlyList<(string, byte[])> exports)
        { _dbBytes = dbBytes; _exports = exports; }

        public void WriteDatabaseCopy(string destPath) => File.WriteAllBytes(destPath, _dbBytes);
        public IReadOnlyList<(string Name, byte[] Content)> Exports() =>
            _exports.Select(e => (e.Item1, e.Item2)).ToList();
    }

    /// <summary>An in-memory SFTP server: a path→bytes map plus logs so tests can
    /// assert the tmp→rename atomic protocol was actually followed.</summary>
    private sealed class FakeSftpTransport : ISftpTransport
    {
        public Dictionary<string, byte[]> Files { get; } = new();
        public List<string> UploadLog { get; } = new();
        public List<(string From, string To)> RenameLog { get; } = new();
        public string? SeenFingerprint { get; set; }

        public void EnsureDirectory(string remoteDir) { /* directories are implicit in the map */ }

        public void Upload(string localPath, string remotePath)
        {
            Files[remotePath] = File.ReadAllBytes(localPath);
            UploadLog.Add(remotePath);
        }

        public void Rename(string fromRemote, string toRemote)
        {
            if (!Files.TryGetValue(fromRemote, out var bytes))
                throw new InvalidOperationException($"rename of missing {fromRemote}");
            Files[toRemote] = bytes;          // overwrites, like the real transport
            Files.Remove(fromRemote);
            RenameLog.Add((fromRemote, toRemote));
        }

        public IReadOnlyList<string> List(string remoteDir)
        {
            string prefix = remoteDir.TrimEnd('/') + "/";
            return Files.Keys
                .Where(k => k.StartsWith(prefix) && !k.Substring(prefix.Length).Contains('/'))
                .Select(k => k.Substring(prefix.Length))
                .ToList();
        }

        public void Delete(string remotePath) => Files.Remove(remotePath);

        public void Download(string remotePath, string localPath) =>
            File.WriteAllBytes(localPath, Files[remotePath]);

        public void Dispose() { }
    }
}
