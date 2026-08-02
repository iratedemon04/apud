using Apud.Data;

namespace Apud.Sync;

/// <summary>
/// The server relationship for one catalogue (docs/PLAN.md §9b): where to back up
/// and how much history to keep. Stored per-catalogue in the <c>setting</c> table
/// under <c>sync.*</c> keys — a backup target belongs to the database it protects,
/// not to the machine. The private-key <b>passphrase is never stored</b>; it is
/// prompted per session and passed to the transport at connect time.
/// </summary>
public sealed class SyncSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";

    /// <summary>Path to the SSH private key used to authenticate.</summary>
    public string KeyPath { get; set; } = "";

    /// <summary>Root folder on the server; <c>snapshots/</c> and <c>latest/</c> live under it.</summary>
    public string RemoteRoot { get; set; } = "apud";

    /// <summary>How many dated snapshots to keep on the server; older ones are pruned.
    /// 0 (or less) keeps everything — a safety valve so a blank field never wipes history.</summary>
    public int Retention { get; set; } = 10;

    /// <summary>Also publish a plain <c>.mrk</c> export of each non-empty base to
    /// <c>latest/</c>, so server-side scripts can consume the catalogue without SQLite.</summary>
    public bool UploadExport { get; set; } = true;

    /// <summary>The server's host-key fingerprint (SHA256:…), pinned on first successful
    /// connect (TOFU) and verified on every connect thereafter. Null until first pinned.</summary>
    public string? HostFingerprint { get; set; }

    /// <summary>The minimum needed to attempt a connection.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Host) &&
        !string.IsNullOrWhiteSpace(User) &&
        !string.IsNullOrWhiteSpace(KeyPath);

    private const string Prefix = "sync.";

    public static SyncSettings Load(RecordRepository repo)
    {
        string? G(string k) => repo.GetSetting(Prefix + k);
        var s = new SyncSettings
        {
            Host = (G("host") ?? "").Trim(),
            User = (G("user") ?? "").Trim(),
            KeyPath = (G("keypath") ?? "").Trim(),
        };
        string? root = G("root");
        if (!string.IsNullOrWhiteSpace(root)) s.RemoteRoot = root.Trim();
        if (int.TryParse(G("port"), out int p) && p > 0) s.Port = p;
        if (int.TryParse(G("retention"), out int n)) s.Retention = n;
        s.UploadExport = G("export") != "0"; // default on; only an explicit "0" turns it off
        string? fp = G("fingerprint");
        s.HostFingerprint = string.IsNullOrWhiteSpace(fp) ? null : fp.Trim();
        return s;
    }

    public void Save(RecordRepository repo)
    {
        void S(string k, string v) => repo.SetSetting(Prefix + k, v);
        S("host", Host.Trim());
        S("user", User.Trim());
        S("keypath", KeyPath.Trim());
        S("root", RemoteRoot.Trim());
        S("port", Port.ToString());
        S("retention", Retention.ToString());
        S("export", UploadExport ? "1" : "0");
        S("fingerprint", HostFingerprint ?? "");
    }
}
