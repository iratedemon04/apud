namespace Apud.Sync;

public sealed record UploadResult(
    string RemoteSnapshot,
    int RecordFiles,
    int Pruned,
    string? SeenFingerprint);

public sealed record SnapshotInfo(string Name, DateTime TimestampUtc);

/// <summary>How many per-record <c>.mrk</c> files a restore wrote, and where.</summary>
public sealed record RecordDownloadResult(int Files, string LocalRoot);

/// <summary>
/// Backup / publish orchestration (docs/PLAN.md §9b) over an <see cref="ISftpTransport"/>.
/// Every server write goes tmp→rename so a dropped connection never leaves a corrupt
/// final file. Knows nothing about SSH: it is handed a factory that returns a connected,
/// host-key-verified transport, which is what lets the whole flow be tested with a fake.
///
/// Server layout under the configured root (&lt;catalogue&gt; = the catalogue's own file name):
///   snapshots/&lt;catalogue&gt;-YYYYMMDD-HHMMSS.db   (dated history, pruned to keep-N)
///   latest/&lt;catalogue&gt;.db                      (stable path for server-side scripts)
///   latest/BIB.mrk, latest/AUT.mrk             (optional plain-text publication)
/// </summary>
public sealed class SyncService
{
    private readonly Func<ISftpTransport> _connect;
    private readonly string _catalogueName;

    /// <param name="connectedTransportFactory">Returns a freshly connected, host-key-verified
    /// transport each call; SyncService disposes it.</param>
    /// <param name="catalogueName">The open catalogue's file name (no extension) — drives the
    /// snapshot / latest names so a backup keeps the catalogue's identity.</param>
    public SyncService(Func<ISftpTransport> connectedTransportFactory, string catalogueName)
    {
        _connect = connectedTransportFactory;
        _catalogueName = SnapshotNaming.SafeBase(catalogueName);
    }

    public UploadResult Upload(ISnapshotSource source, SyncSettings settings, DateTime utcNow)
    {
        string root = settings.RemoteRoot.Trim().Trim('/');
        string snapshotsDir = Join(root, "snapshots");
        string latestDir = Join(root, "latest");
        string name = SnapshotNaming.ForTimestamp(_catalogueName, utcNow);

        string work = Path.Combine(Path.GetTempPath(), "apud-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            // Produce the consistent copy locally first; only then touch the network.
            string localDb = Path.Combine(work, name);
            source.WriteDatabaseCopy(localDb);
            var folders = source.RecordFolders();

            using var t = _connect();
            t.EnsureDirectory(snapshotsDir);
            t.EnsureDirectory(latestDir);

            string remoteSnapshot = Join(snapshotsDir, name);
            UploadAtomic(t, localDb, remoteSnapshot);
            UploadAtomic(t, localDb, Join(latestDir, _catalogueName + ".db"));

            // Legacy one-file exports from before the per-record layout — clear them so the
            // server never keeps a stale concatenated copy beside the new folders.
            foreach (var (@base, _) in DbSnapshotSource.Folders)
                t.Delete(Join(latestDir, @base + ".mrk"));

            int recordFiles = PublishRecordFolders(t, work, latestDir, folders);

            var prune = SnapshotNaming.ToPrune(_catalogueName, t.List(snapshotsDir), settings.Retention);
            foreach (string old in prune) t.Delete(Join(snapshotsDir, old));

            return new UploadResult(remoteSnapshot, recordFiles, prune.Count, t.SeenFingerprint);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* temp cleanup is best-effort */ }
        }
    }

    /// <summary>The dated snapshots on the server, newest first.</summary>
    public IReadOnlyList<SnapshotInfo> ListSnapshots(SyncSettings settings)
    {
        string snapshotsDir = Join(settings.RemoteRoot.Trim().Trim('/'), "snapshots");
        using var t = _connect();
        return t.List(snapshotsDir)
            .Where(n => SnapshotNaming.IsSnapshot(_catalogueName, n))
            .Select(n => new SnapshotInfo(n, SnapshotNaming.TimestampOf(_catalogueName, n)))
            .OrderByDescending(s => s.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Downloads one dated snapshot to a local path. Never touches the working
    /// catalogue — the caller decides what to do with the copy.</summary>
    public void Download(SyncSettings settings, string snapshotName, string localPath)
    {
        string remote = Join(Join(settings.RemoteRoot.Trim().Trim('/'), "snapshots"), snapshotName);
        using var t = _connect();
        t.Download(remote, localPath);
    }

    /// <summary>Downloads the published per-record <c>.mrk</c> folders (<c>latest/bib</c>,
    /// <c>latest/aut</c>) into <paramref name="localRoot"/> as <c>bib/</c> and <c>aut/</c>
    /// sub-folders — the physical-records counterpart of a snapshot download. Same logic
    /// as a snapshot download, applied per file; existing files of the same name are
    /// overwritten (the "replace" the caller opted into). Never touches the database.</summary>
    public RecordDownloadResult DownloadRecordFolders(SyncSettings settings, string localRoot)
    {
        string latestDir = Join(settings.RemoteRoot.Trim().Trim('/'), "latest");
        int total = 0;
        using var t = _connect();
        foreach (var (_, folderName) in DbSnapshotSource.Folders)
        {
            string remoteDir = Join(latestDir, folderName);
            var files = t.List(remoteDir)
                         .Where(n => n.EndsWith(".mrk", StringComparison.OrdinalIgnoreCase))
                         .ToList();
            if (files.Count == 0) continue;
            string localDir = Path.Combine(localRoot, folderName);
            Directory.CreateDirectory(localDir);
            foreach (string f in files)
            {
                t.Download(Join(remoteDir, f), Path.Combine(localDir, f));
                total++;
            }
        }
        return new RecordDownloadResult(total, localRoot);
    }

    /// <summary>The per-folder index of <c>filename → content-hash</c>; lets a backup skip
    /// records whose <c>.mrk</c> is unchanged since last time (only the first backup, or an
    /// edited record, actually uploads).</summary>
    private const string ManifestName = ".manifest";

    /// <summary>Publishes each base's per-record files into <c>latest/&lt;folder&gt;/</c>,
    /// incrementally: only files that are new or whose content changed are uploaded, files
    /// whose record is gone are deleted, and a manifest records the current hashes for next
    /// time. Record files upload directly (no tmp→rename): they are a mirror, so a re-run
    /// heals a partial one, and skipping the rename halves the per-file cost. Returns the
    /// number of files actually uploaded.</summary>
    private static int PublishRecordFolders(
        ISftpTransport t, string work, string latestDir, IReadOnlyList<RecordFolder> folders)
    {
        int uploaded = 0;
        foreach (var (_, folderName) in DbSnapshotSource.Folders)
        {
            var desired = folders.FirstOrDefault(f => f.Name == folderName)?.Files
                          ?? (IReadOnlyList<RecordFile>)Array.Empty<RecordFile>();
            string dir = Join(latestDir, folderName);
            var existing = new HashSet<string>(t.List(dir), StringComparer.Ordinal);
            if (desired.Count == 0 && existing.Count == 0) continue;

            var old = existing.Contains(ManifestName)
                ? ReadManifest(t, work, Join(dir, ManifestName))
                : new Dictionary<string, string>(StringComparer.Ordinal);

            if (desired.Count > 0) t.EnsureDirectory(dir);

            var current = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in desired)
            {
                string hash = Hash(file.Content);
                current[file.Name] = hash;
                bool unchanged = old.TryGetValue(file.Name, out var prev)
                                 && prev == hash && existing.Contains(file.Name);
                if (unchanged) continue;
                string local = Path.Combine(work, folderName + "-" + file.Name);
                File.WriteAllBytes(local, file.Content);
                t.Upload(local, Join(dir, file.Name)); // direct overwrite; a mirror needs no tmp→rename
                uploaded++;
            }

            // A record deleted since last backup: remove its stale server file.
            foreach (string name in existing)
                if (name.EndsWith(".mrk", StringComparison.OrdinalIgnoreCase) && !current.ContainsKey(name))
                    t.Delete(Join(dir, name));

            // Rewrite the manifest (atomic — the next backup trusts it), or drop it when empty.
            string manifestPath = Join(dir, ManifestName);
            if (current.Count > 0)
            {
                string localManifest = Path.Combine(work, folderName + ".manifest");
                File.WriteAllText(localManifest, SerializeManifest(current));
                UploadAtomic(t, localManifest, manifestPath);
            }
            else if (existing.Contains(ManifestName))
            {
                t.Delete(manifestPath);
            }
        }
        return uploaded;
    }

    private static Dictionary<string, string> ReadManifest(ISftpTransport t, string work, string remotePath)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string local = Path.Combine(work, "manifest-" + Guid.NewGuid().ToString("N"));
        try
        {
            t.Download(remotePath, local);
            foreach (string line in File.ReadAllLines(local))
            {
                int sp = line.IndexOf(' ');
                if (sp > 0) map[line[(sp + 1)..]] = line[..sp]; // "<hash> <filename>"
            }
        }
        catch { /* missing/unreadable manifest → treat as empty; a full re-upload heals it */ }
        finally { try { File.Delete(local); } catch { /* best-effort */ } }
        return map;
    }

    private static string SerializeManifest(IReadOnlyDictionary<string, string> m) =>
        string.Join("\n", m.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Value} {kv.Key}"));

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    private static void UploadAtomic(ISftpTransport t, string local, string remoteFinal)
    {
        string tmp = remoteFinal + ".tmp";
        t.Upload(local, tmp);
        t.Rename(tmp, remoteFinal); // overwrites; readers only ever see the whole file
    }

    internal static string Join(string a, string b) => $"{a.TrimEnd('/')}/{b.TrimStart('/')}";
}
