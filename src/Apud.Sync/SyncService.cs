namespace Apud.Sync;

public sealed record UploadResult(
    string RemoteSnapshot,
    IReadOnlyList<string> Exports,
    int Pruned,
    string? SeenFingerprint);

public sealed record SnapshotInfo(string Name, DateTime TimestampUtc);

/// <summary>
/// Backup / publish orchestration (docs/PLAN.md §9b) over an <see cref="ISftpTransport"/>.
/// Every server write goes tmp→rename so a dropped connection never leaves a corrupt
/// final file. Knows nothing about SSH: it is handed a factory that returns a connected,
/// host-key-verified transport, which is what lets the whole flow be tested with a fake.
///
/// Server layout under the configured root:
///   snapshots/catalog-YYYYMMDD-HHMMSS.db   (dated history, pruned to keep-N)
///   latest/catalog.db                      (stable path for server-side scripts)
///   latest/BIB.mrk, latest/AUT.mrk         (optional plain-text publication)
/// </summary>
public sealed class SyncService
{
    private readonly Func<ISftpTransport> _connect;

    /// <param name="connectedTransportFactory">Returns a freshly connected, host-key-verified
    /// transport each call; SyncService disposes it.</param>
    public SyncService(Func<ISftpTransport> connectedTransportFactory) =>
        _connect = connectedTransportFactory;

    public UploadResult Upload(ISnapshotSource source, SyncSettings settings, DateTime utcNow)
    {
        string root = settings.RemoteRoot.Trim().Trim('/');
        string snapshotsDir = Join(root, "snapshots");
        string latestDir = Join(root, "latest");
        string name = SnapshotNaming.ForTimestamp(utcNow);

        string work = Path.Combine(Path.GetTempPath(), "apud-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            // Produce the consistent copy locally first; only then touch the network.
            string localDb = Path.Combine(work, name);
            source.WriteDatabaseCopy(localDb);
            var exports = source.Exports();

            using var t = _connect();
            t.EnsureDirectory(snapshotsDir);
            t.EnsureDirectory(latestDir);

            string remoteSnapshot = Join(snapshotsDir, name);
            UploadAtomic(t, localDb, remoteSnapshot);
            UploadAtomic(t, localDb, Join(latestDir, "catalog.db"));

            var uploadedExports = new List<string>();
            foreach (var (exportName, content) in exports)
            {
                string localExport = Path.Combine(work, exportName);
                File.WriteAllBytes(localExport, content);
                UploadAtomic(t, localExport, Join(latestDir, exportName));
                uploadedExports.Add(exportName);
            }

            var prune = SnapshotNaming.ToPrune(t.List(snapshotsDir), settings.Retention);
            foreach (string old in prune) t.Delete(Join(snapshotsDir, old));

            return new UploadResult(remoteSnapshot, uploadedExports, prune.Count, t.SeenFingerprint);
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
            .Where(SnapshotNaming.IsSnapshot)
            .Select(n => new SnapshotInfo(n, SnapshotNaming.TimestampOf(n)))
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

    private static void UploadAtomic(ISftpTransport t, string local, string remoteFinal)
    {
        string tmp = remoteFinal + ".tmp";
        t.Upload(local, tmp);
        t.Rename(tmp, remoteFinal); // overwrites; readers only ever see the whole file
    }

    internal static string Join(string a, string b) => $"{a.TrimEnd('/')}/{b.TrimStart('/')}";
}
