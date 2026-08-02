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
        // The commonest mistake by far is picking the .pub file. SSH authenticates
        // with the PRIVATE key; catch a public key up front with a plain message
        // rather than letting it surface as an opaque parse or auth failure.
        if (!File.Exists(_settings.KeyPath))
            throw new SyncException($"Private key not found:\n{_settings.KeyPath}");
        if (LooksLikePublicKey(_settings.KeyPath))
            throw new SyncException(
                "That is a PUBLIC key (.pub) — SSH logs in with the PRIVATE key.\n\n" +
                $"You chose:\n{_settings.KeyPath}\n\n" +
                "Choose the matching file WITHOUT the .pub extension (its contents begin " +
                "with \"-----BEGIN … PRIVATE KEY-----\").");

        PrivateKeyFile key;
        try
        {
            key = string.IsNullOrEmpty(_passphrase)
                ? new PrivateKeyFile(_settings.KeyPath)
                : new PrivateKeyFile(_settings.KeyPath, _passphrase);
        }
        catch (Exception e) when (e is SshException or System.Security.Cryptography.CryptographicException or FormatException)
        {
            throw new SyncException(
                "The private key could not be read. If it has a passphrase, enter it; " +
                "otherwise check the file is an unencrypted OpenSSH or PEM private key " +
                "(PuTTY .ppk files are not supported — export it as OpenSSH first).");
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
        catch (SshAuthenticationException)
        {
            throw new SyncException(
                $"The server refused the key (permission denied) for user \"{_settings.User}\".\n\n" +
                "The connection and host key were fine, so this is an authorization problem:\n" +
                "• is the username correct for this server?\n" +
                "• is this key's .pub line in the server's ~/.ssh/authorized_keys for that user?");
        }
    }

    /// <summary>True if the file is an SSH public key (an OpenSSH one-line
    /// <c>ssh-… / ecdsa-… / sk-…</c> form, or simply a <c>.pub</c> name) rather than a
    /// private key. A cheap first-line peek — the guard, not a full parser.</summary>
    internal static bool LooksLikePublicKey(string path)
    {
        if (path.EndsWith(".pub", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                return line.StartsWith("ssh-", StringComparison.Ordinal)
                    || line.StartsWith("ecdsa-", StringComparison.Ordinal)
                    || line.StartsWith("sk-", StringComparison.Ordinal);
            }
        }
        catch { /* unreadable → let the real key loader report it */ }
        return false;
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
