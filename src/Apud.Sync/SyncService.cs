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

    /// <summary>Publishes each base's per-record files into <c>latest/&lt;folder&gt;/</c>,
    /// then deletes any server <c>.mrk</c> there that no longer has a record (so a
    /// deleted record's file does not linger). Returns the number of files uploaded.</summary>
    private static int PublishRecordFolders(
        ISftpTransport t, string work, string latestDir, IReadOnlyList<RecordFolder> folders)
    {
        int total = 0;
        foreach (var (_, folderName) in DbSnapshotSource.Folders)
        {
            var desired = folders.FirstOrDefault(f => f.Name == folderName)?.Files
                          ?? (IReadOnlyList<RecordFile>)Array.Empty<RecordFile>();
            string dir = Join(latestDir, folderName);
            var existing = t.List(dir);

            if (desired.Count > 0) t.EnsureDirectory(dir);
            foreach (var file in desired)
            {
                string local = Path.Combine(work, folderName + "-" + file.Name);
                File.WriteAllBytes(local, file.Content);
                UploadAtomic(t, local, Join(dir, file.Name));
                total++;
            }

            var keep = new HashSet<string>(desired.Select(f => f.Name), StringComparer.Ordinal);
            foreach (string name in existing)
                if (name.EndsWith(".mrk", StringComparison.OrdinalIgnoreCase) && !keep.Contains(name))
                    t.Delete(Join(dir, name));
        }
        return total;
    }

    private static void UploadAtomic(ISftpTransport t, string local, string remoteFinal)
    {
        string tmp = remoteFinal + ".tmp";
        t.Upload(local, tmp);
        t.Rename(tmp, remoteFinal); // overwrites; readers only ever see the whole file
    }

    internal static string Join(string a, string b) => $"{a.TrimEnd('/')}/{b.TrimStart('/')}";
}
