using Apud.Sync;

namespace Apud.App;

/// <summary>
/// File → Server → Set Server: the per-catalogue backup target (docs/PLAN.md §9b).
/// A plain form over <see cref="SyncSettings"/> — host, user, key file, remote root,
/// how many snapshots to keep, whether to also publish .mrk exports. The private-key
/// passphrase is deliberately NOT here: it is asked for each session at connect time
/// and never stored. The pinned host key is shown read-only with a Forget button to
/// reset trust-on-first-use after a legitimate server rebuild.
/// </summary>
public sealed class SyncSettingsForm : Form
{
    private readonly TextBox _host = NewBox();
    private readonly TextBox _port = NewBox();
    private readonly TextBox _user = NewBox();
    private readonly TextBox _keyPath = NewBox();
    private readonly TextBox _root = NewBox();
    private readonly TextBox _retention = NewBox();
    private readonly CheckBox _export = new() { Text = "Also publish a .mrk export of each base to latest/", AutoSize = true };
    private readonly Label _fingerprint = new() { AutoSize = false, Font = new Font("Consolas", 8.5f), AutoEllipsis = true };

    private string? _hostFingerprint;

    public SyncSettings Result { get; private set; } = new();

    public SyncSettingsForm(SyncSettings current)
    {
        Text = "Set Server";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(560, 360);
        Font = new Font("Segoe UI", 9.75f);

        _host.Text = current.Host;
        _port.Text = current.Port.ToString();
        _user.Text = current.User;
        _keyPath.Text = current.KeyPath;
        _root.Text = current.RemoteRoot;
        _retention.Text = current.Retention.ToString();
        _export.Checked = current.UploadExport;
        _hostFingerprint = current.HostFingerprint;

        int y = 16;
        Row("Host", _host, ref y);
        Row("Port", _port, ref y);
        Row("User", _user, ref y);
        RowWithButton("Private key", _keyPath, "Browse…", BrowseKey, ref y);
        Row("Remote root", _root, ref y);
        Row("Keep last N snapshots", _retention, ref y);

        _export.Location = new Point(150, y + 2);
        Controls.Add(_export);
        y += 32;

        var fpLabel = new Label { Text = "Pinned host key", AutoSize = true, Location = new Point(14, y + 3) };
        _fingerprint.Location = new Point(150, y);
        _fingerprint.Size = new Size(300, 34);
        var forget = new Button { Text = "Forget", Size = new Size(78, 26), Location = new Point(456, y - 1) };
        forget.Click += (_, _) => { _hostFingerprint = null; RefreshFingerprint(); };
        Controls.Add(fpLabel);
        Controls.Add(_fingerprint);
        Controls.Add(forget);
        RefreshFingerprint();
        y += 44;

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Size = new Size(90, 28), Location = new Point(360, y) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(90, 28), Location = new Point(456, y) };
        ok.Click += OnOk;
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private static TextBox NewBox() => new() { Size = new Size(390, 24) };

    private void Row(string label, TextBox box, ref int y)
    {
        Controls.Add(new Label { Text = label, AutoSize = true, Location = new Point(14, y + 3) });
        box.Location = new Point(150, y);
        Controls.Add(box);
        y += 32;
    }

    private void RowWithButton(string label, TextBox box, string buttonText, EventHandler onClick, ref int y)
    {
        Controls.Add(new Label { Text = label, AutoSize = true, Location = new Point(14, y + 3) });
        box.Location = new Point(150, y);
        box.Size = new Size(304, 24);
        var button = new Button { Text = buttonText, Size = new Size(78, 26), Location = new Point(456, y - 1) };
        button.Click += onClick;
        Controls.Add(box);
        Controls.Add(button);
        y += 32;
    }

    private void RefreshFingerprint() =>
        _fingerprint.Text = string.IsNullOrEmpty(_hostFingerprint)
            ? "(not yet trusted — pinned on first backup)"
            : _hostFingerprint;

    private void BrowseKey(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose the SSH private key",
            Filter = "Private key (*.pem;*.key;id_*)|*.pem;*.key;id_*|All files (*.*)|*.*",
            FileName = _keyPath.Text,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _keyPath.Text = dialog.FileName;
    }

    private void OnOk(object? sender, EventArgs e)
    {
        int port = int.TryParse(_port.Text.Trim(), out int p) && p > 0 ? p : 22;
        int keep = int.TryParse(_retention.Text.Trim(), out int n) ? n : 10;
        Result = new SyncSettings
        {
            Host = _host.Text.Trim(),
            Port = port,
            User = _user.Text.Trim(),
            KeyPath = _keyPath.Text.Trim(),
            RemoteRoot = string.IsNullOrWhiteSpace(_root.Text) ? "apud" : _root.Text.Trim(),
            Retention = keep,
            UploadExport = _export.Checked,
            HostFingerprint = _hostFingerprint,
        };
    }
}
