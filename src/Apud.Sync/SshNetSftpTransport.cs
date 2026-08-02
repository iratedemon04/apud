using System.Security.Cryptography;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Apud.Sync;

/// <summary>
/// The only class in Apud that opens a socket (design law #4). SFTP over SSH via
/// SSH.NET, authenticating with a private key. Host identity is trust-on-first-use:
/// the server's SHA256 fingerprint is captured on first connect and, once pinned in
/// <see cref="SyncSettings.HostFingerprint"/>, must match on every connect thereafter
/// — a changed key aborts the connection with a clear message rather than a silent
/// man-in-the-middle. The key passphrase is supplied per session and never stored.
/// </summary>
public sealed class SshNetSftpTransport : ISftpTransport
{
    private readonly SyncSettings _settings;
    private readonly string? _passphrase;
    private SftpClient? _client;
    private bool _hostKeyMismatch;

    public string? SeenFingerprint { get; private set; }

    public SshNetSftpTransport(SyncSettings settings, string? passphrase)
    {
        _settings = settings;
        _passphrase = passphrase;
    }

    /// <summary>Opens the connection and verifies the host key. Throws
    /// <see cref="SyncException"/> with a user-facing message on a key mismatch or a
    /// missing/undecryptable private key.</summary>
    public void Connect()
    {
        PrivateKeyFile key;
        try
        {
            key = string.IsNullOrEmpty(_passphrase)
                ? new PrivateKeyFile(_settings.KeyPath)
                : new PrivateKeyFile(_settings.KeyPath, _passphrase);
        }
        catch (FileNotFoundException)
        {
            throw new SyncException($"Private key not found:\n{_settings.KeyPath}");
        }
        catch (Exception e) when (e is SshException or System.Security.Cryptography.CryptographicException)
        {
            throw new SyncException(
                "The private key could not be read — check the passphrase and that the file is an OpenSSH/PEM key.");
        }

        var info = new ConnectionInfo(_settings.Host, _settings.Port, _settings.User,
            new PrivateKeyAuthenticationMethod(_settings.User, key));

        _client = new SftpClient(info);
        _client.HostKeyReceived += OnHostKey;

        try
        {
            _client.Connect();
        }
        catch (Exception) when (_hostKeyMismatch)
        {
            throw new SyncException(
                "The server's host key has changed since it was first trusted.\n\n" +
                $"Expected: {_settings.HostFingerprint}\n" +
                $"Received: {SeenFingerprint}\n\n" +
                "If you rebuilt or reinstalled the server, use \"Forget host key\" in " +
                "File → Server → Set Server and connect again. Otherwise do NOT proceed.");
        }
    }

    private void OnHostKey(object? sender, HostKeyEventArgs e)
    {
        string fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('=');
        SeenFingerprint = fingerprint;

        if (_settings.HostFingerprint is null)
        {
            e.CanTrust = true; // TOFU: first sighting is trusted and pinned by the caller
            return;
        }

        bool matches = string.Equals(_settings.HostFingerprint, fingerprint, StringComparison.Ordinal);
        e.CanTrust = matches;
        _hostKeyMismatch = !matches;
    }

    public void EnsureDirectory(string remoteDir)
    {
        var client = Require();
        var parts = remoteDir.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string acc = remoteDir.StartsWith('/') ? "" : ".";
        foreach (string part in parts)
        {
            acc = acc == "." ? part : $"{acc}/{part}";
            if (!client.Exists(acc)) client.CreateDirectory(acc);
        }
    }

    public void Upload(string localPath, string remotePath)
    {
        var client = Require();
        using var stream = File.OpenRead(localPath);
        client.UploadFile(stream, remotePath, canOverride: true);
    }

    public void Rename(string fromRemote, string toRemote)
    {
        var client = Require();
        // SFTP rename does not overwrite; clear the destination so tmp→final always lands.
        if (client.Exists(toRemote)) client.DeleteFile(toRemote);
        client.RenameFile(fromRemote, toRemote);
    }

    public IReadOnlyList<string> List(string remoteDir)
    {
        var client = Require();
        if (!client.Exists(remoteDir)) return Array.Empty<string>();
        return client.ListDirectory(remoteDir)
            .Where(f => !f.IsDirectory)
            .Select(f => f.Name)
            .ToList();
    }

    public void Delete(string remotePath)
    {
        var client = Require();
        if (client.Exists(remotePath)) client.DeleteFile(remotePath);
    }

    public void Download(string remotePath, string localPath)
    {
        var client = Require();
        using var stream = File.Create(localPath);
        client.DownloadFile(remotePath, stream);
    }

    private SftpClient Require() =>
        _client ?? throw new InvalidOperationException("Transport is not connected.");

    public void Dispose()
    {
        if (_client is null) return;
        try { if (_client.IsConnected) _client.Disconnect(); } catch { /* closing best-effort */ }
        _client.Dispose();
        _client = null;
    }
}
