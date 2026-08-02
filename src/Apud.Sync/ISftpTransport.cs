namespace Apud.Sync;

/// <summary>
/// The seam between Apud.Sync's orchestration and the SSH/SFTP library. Only the
/// concrete <see cref="SshNetSftpTransport"/> opens a socket (design law #4: this is
/// the one networked assembly); tests drive the whole upload / prune / restore flow
/// through a fake. A transport handed to <see cref="SyncService"/> is already
/// connected and its host key already verified.
/// </summary>
public interface ISftpTransport : IDisposable
{
    /// <summary>The host-key fingerprint (<c>SHA256:…</c>) actually seen on connect.
    /// The caller pins this the first time (TOFU) and verifies it thereafter.</summary>
    string? SeenFingerprint { get; }

    /// <summary>Creates <paramref name="remoteDir"/> and any missing parents.</summary>
    void EnsureDirectory(string remoteDir);

    void Upload(string localPath, string remotePath);

    /// <summary>Renames within the server, <b>overwriting</b> the destination if it
    /// already exists. This is what makes the tmp→final swap atomic for any reader:
    /// they only ever see the complete final file.</summary>
    void Rename(string fromRemote, string toRemote);

    /// <summary>Base names of the files (not sub-directories) in <paramref name="remoteDir"/>;
    /// an empty list if the directory does not exist.</summary>
    IReadOnlyList<string> List(string remoteDir);

    void Delete(string remotePath);

    void Download(string remotePath, string localPath);
}

/// <summary>A sync operation that failed for a reason worth showing the user verbatim
/// (host-key mismatch, missing key file, …) rather than a raw library exception.</summary>
public sealed class SyncException : Exception
{
    public SyncException(string message) : base(message) { }
}
