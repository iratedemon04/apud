using Apud.Data;
using Apud.Sync;

namespace Apud.App;

/// <summary>
/// Server backup / restore orchestration (Module 11, docs/PLAN.md §9b), extracted
/// from <see cref="MainForm"/> so the sync command handlers read in isolation. Knows
/// nothing about the form: it is handed a minimal set of callbacks (repo/db getters,
/// the text prompt, the message sink, folder helpers, open-catalogue) plus an owner
/// window for its dialogs. The real work lives below in <see cref="SyncService"/>,
/// which is already covered by the FakeSftpTransport tests; this class is the
/// dialog-driven glue around it.
///
/// It also owns the "records pushed since the last backup" counter that drives the
/// on-exit "back up first?" prompt: MainForm calls <see cref="NotePush"/> on each
/// push and <see cref="OfferBackupBeforeClose"/> while closing.
/// </summary>
public sealed class SyncCoordinator
{
    private const string FirstBackupSeenKey = "sync.first_backup_seen";

    private readonly IWin32Window _owner;
    private readonly Func<bool> _requireCatalogue;
    private readonly Func<RecordRepository?> _repo;
    private readonly Func<ApudDatabase?> _db;
    private readonly Func<string?> _catalogPath;
    private readonly Func<string, string, string, bool, string?> _promptText;
    private readonly Action<string> _setMessage;
    private readonly Action _refreshStatus;
    private readonly Func<string> _startFolder;
    private readonly Action<string?> _rememberFolder;
    private readonly Action<string> _openCatalogue;

    private int _pushesSinceSync; // records pushed since the last server backup

    public SyncCoordinator(
        IWin32Window owner,
        Func<bool> requireCatalogue,
        Func<RecordRepository?> repo,
        Func<ApudDatabase?> db,
        Func<string?> catalogPath,
        Func<string, string, string, bool, string?> promptText,
        Action<string> setMessage,
        Action refreshStatus,
        Func<string> startFolder,
        Action<string?> rememberFolder,
        Action<string> openCatalogue)
    {
        _owner = owner;
        _requireCatalogue = requireCatalogue;
        _repo = repo;
        _db = db;
        _catalogPath = catalogPath;
        _promptText = promptText;
        _setMessage = setMessage;
        _refreshStatus = refreshStatus;
        _startFolder = startFolder;
        _rememberFolder = rememberFolder;
        _openCatalogue = openCatalogue;
    }

    /// <summary>Records that a record was pushed, for the on-exit backup prompt.</summary>
    public void NotePush() => _pushesSinceSync++;

    /// <summary>On-exit backup prompt (docs/PLAN.md §9b trigger): if a server is
    /// configured and records were pushed since the last upload, offer to back up
    /// before closing. Returns false to cancel the close (Cancel), true otherwise —
    /// running the upload on Yes.</summary>
    public bool OfferBackupBeforeClose()
    {
        var repo = _repo();
        if (repo is null || _pushesSinceSync == 0) return true;
        var settings = SyncSettings.Load(repo);
        if (!settings.IsConfigured) return true;

        var answer = MessageBox.Show(_owner,
            $"{_pushesSinceSync} record(s) were pushed since the last backup.\n\nBack up to the server before closing?",
            "Apud", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel) return false;
        if (answer == DialogResult.Yes) Upload();
        return true;
    }

    /// <summary>File → Backup Server → Set Server: the per-catalogue backup target.</summary>
    public void Configure()
    {
        if (!_requireCatalogue()) return;
        var repo = _repo()!;

        using var form = new SyncSettingsForm(SyncSettings.Load(repo));
        if (form.ShowDialog(_owner) != DialogResult.OK) return;

        form.Result.Save(repo);
        _setMessage(form.Result.IsConfigured
            ? $"Server set: {form.Result.User}@{form.Result.Host}:{form.Result.Port} → {form.Result.RemoteRoot}."
            : "Server settings saved — host, user and private key are still needed before a backup.");
    }

    /// <summary>File → Backup Server → Back Up to Server: VACUUM-INTO snapshot + latest/ refresh,
    /// uploaded atomically, then old snapshots pruned to the keep-N.</summary>
    public void Upload()
    {
        if (!_requireCatalogue()) return;
        var repo = _repo()!;
        var db = _db();
        if (db is null) return;

        var settings = SyncSettings.Load(repo);
        if (!settings.IsConfigured)
        {
            _setMessage("Configure the server first — File → Backup Server → Set Server.");
            return;
        }

        if (!ConfirmFirstBackup(repo)) return; // first backup: warn about time, once, before anything
        if (AskPassphrase(settings) is not string passphrase) return; // cancelled

        _setMessage($"Backing up to {settings.Host}…");
        _refreshStatus();
        Cursor.Current = Cursors.WaitCursor;
        try
        {
            var service = SyncServiceFor(settings, passphrase);
            var source = new DbSnapshotSource(db, repo, settings.UploadExport);
            var result = service.Upload(source, settings, DateTime.UtcNow);

            // Trust-on-first-use: pin the fingerprint the first time we see it.
            if (settings.HostFingerprint is null && result.SeenFingerprint is not null)
            {
                settings.HostFingerprint = result.SeenFingerprint;
                settings.Save(repo);
            }

            _pushesSinceSync = 0;
            repo.SetSetting(FirstBackupSeenKey, "1"); // first backup done → no more time warning
            string records = result.RecordFiles > 0 ? $" + {result.RecordFiles} record file(s)" : "";
            string pruned = result.Pruned > 0 ? $" Pruned {result.Pruned} old snapshot(s)." : "";
            _setMessage($"Backed up {Path.GetFileName(result.RemoteSnapshot)}{records} → {settings.RemoteRoot}.{pruned}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(_owner, ex.Message, "Backup failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _setMessage("Backup failed.");
        }
        finally { Cursor.Current = Cursors.Default; }
    }

    /// <summary>File → Backup Server → Restore from Server: pick a server snapshot, download it
    /// to a local file, and (optionally) open that copy side-by-side. The working
    /// catalogue is never overwritten — restoring is opening a downloaded copy, a
    /// conscious act, not an automatic replace.</summary>
    public void Restore()
    {
        if (!_requireCatalogue()) return;
        var repo = _repo()!;

        var settings = SyncSettings.Load(repo);
        if (!settings.IsConfigured)
        {
            _setMessage("Configure the server first — File → Backup Server → Set Server.");
            return;
        }

        if (AskPassphrase(settings) is not string passphrase) return;

        IReadOnlyList<SnapshotInfo> snapshots;
        Cursor.Current = Cursors.WaitCursor;
        try
        {
            snapshots = SyncServiceFor(settings, passphrase).ListSnapshots(settings);
        }
        catch (Exception ex)
        {
            Cursor.Current = Cursors.Default;
            MessageBox.Show(_owner, ex.Message, "Restore", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally { Cursor.Current = Cursors.Default; }

        if (snapshots.Count == 0) { _setMessage("No snapshots on the server yet."); return; }

        if (PickSnapshot(snapshots) is not string chosen) return;

        using var save = new SaveFileDialog
        {
            Title = "Save downloaded snapshot as",
            InitialDirectory = _startFolder(),
            FileName = chosen,
            Filter = "Apud catalogue (*.db)|*.db",
            OverwritePrompt = true,
        };
        if (save.ShowDialog(_owner) != DialogResult.OK) return;
        _rememberFolder(Path.GetDirectoryName(save.FileName));

        Cursor.Current = Cursors.WaitCursor;
        try
        {
            SyncServiceFor(settings, passphrase).Download(settings, chosen, save.FileName);
        }
        catch (Exception ex)
        {
            Cursor.Current = Cursors.Default;
            MessageBox.Show(_owner, ex.Message, "Restore", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally { Cursor.Current = Cursors.Default; }

        // Same logic as the snapshot download, applied to the physical .mrk records: an
        // optional pull of the whole bib/ + aut/ tree into a folder the user picks.
        int? records = OfferRecordFolderDownload(settings, passphrase);
        string recNote = records is int n
            ? (n > 0 ? $" {n} record file(s) downloaded." : " No record files on the server.")
            : "";

        if (MessageBox.Show(_owner,
                $"Downloaded to {Path.GetFileName(save.FileName)}.\n\nOpen it now as a separate catalogue?",
                "Restore", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            _openCatalogue(save.FileName);
        else
            _setMessage($"Snapshot saved to {Path.GetFileName(save.FileName)}.{recNote}");
    }

    /// <summary>Builds a SyncService whose transport connects with the session
    /// passphrase. A fresh transport per operation (the factory runs each call).</summary>
    private SyncService SyncServiceFor(SyncSettings settings, string? passphrase) =>
        new(() =>
        {
            var transport = new SshNetSftpTransport(settings, passphrase);
            transport.Connect();
            return transport;
        }, CatalogueName());

    /// <summary>The open catalogue's file name (no extension) — the identity a backup keeps.</summary>
    private string CatalogueName()
    {
        var path = _catalogPath();
        return path is null ? "catalog" : Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>Asks for the key passphrase (masked, blank allowed). Null = cancelled.</summary>
    private string? AskPassphrase(SyncSettings settings) =>
        _promptText("Private Key Passphrase",
            $"Passphrase for {Path.GetFileName(settings.KeyPath)} — leave blank if the key has none.",
            "", true);

    /// <summary>Before a catalogue's very first backup, show the time warning once (any
    /// size). Returns false if the user cancelled at the warning. The "seen" flag is a
    /// per-catalogue setting, set only after the first backup actually succeeds — so a
    /// cancelled or failed first attempt still warns next time.</summary>
    private bool ConfirmFirstBackup(RecordRepository repo)
    {
        if (repo.GetSetting(FirstBackupSeenKey) == "1") return true;
        using var form = new BackupTimeForm(preBackup: true);
        return form.ShowDialog(_owner) == DialogResult.OK;
    }

    /// <summary>Offers to also download the published per-record .mrk folders (bib/ + aut/)
    /// into a folder the user chooses. Returns the number of files downloaded, or null if
    /// the user declined the offer, cancelled the folder pick, or it failed.</summary>
    private int? OfferRecordFolderDownload(SyncSettings settings, string? passphrase)
    {
        if (MessageBox.Show(_owner,
                "Also download the record files (.mrk)?\n\nThey arrive as bib/ and aut/ sub-folders in a folder you choose; files of the same name are replaced.",
                "Restore records", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return null;

        using var pick = new FolderBrowserDialog
        {
            Description = "Choose a folder for the record files (bib/ and aut/ go inside it)",
            UseDescriptionForTitle = true,
            SelectedPath = _startFolder(),
        };
        if (pick.ShowDialog(_owner) != DialogResult.OK) return null;
        _rememberFolder(pick.SelectedPath);

        Cursor.Current = Cursors.WaitCursor;
        try
        {
            return SyncServiceFor(settings, passphrase)
                .DownloadRecordFolders(settings, pick.SelectedPath).Files;
        }
        catch (Exception ex)
        {
            Cursor.Current = Cursors.Default;
            MessageBox.Show(_owner, ex.Message, "Restore records", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
        finally { Cursor.Current = Cursors.Default; }
    }

    /// <summary>A plain list picker for the server snapshots (newest first).</summary>
    private string? PickSnapshot(IReadOnlyList<SnapshotInfo> snapshots)
    {
        using var dialog = new Form
        {
            Text = "Restore from Server",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(420, 320),
        };
        var list = new ListBox { Location = new Point(14, 14), Size = new Size(392, 250), Font = new Font("Consolas", 9f) };
        foreach (var s in snapshots)
            list.Items.Add($"{s.Name}   ({s.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss})");
        list.SelectedIndex = 0;
        var ok = new Button { Text = "Download", DialogResult = DialogResult.OK, Size = new Size(100, 28), Location = new Point(202, 276) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(90, 28), Location = new Point(316, 276) };
        list.DoubleClick += (_, _) => { if (list.SelectedIndex >= 0) { dialog.DialogResult = DialogResult.OK; dialog.Close(); } };
        dialog.Controls.Add(list);
        dialog.Controls.Add(ok);
        dialog.Controls.Add(cancel);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;

        return dialog.ShowDialog(_owner) == DialogResult.OK && list.SelectedIndex >= 0
            ? snapshots[list.SelectedIndex].Name
            : null;
    }
}
