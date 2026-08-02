using System.Drawing;
using System.Windows.Forms;

namespace Apud.App;

/// <summary>
/// The first-backup notice: warns that a large catalogue's <b>very first</b> server
/// backup uploads every record and can take a while, with a table of rough estimates
/// up to a million records. Auto-shows once before the first backup of each catalogue
/// (any size — even one record) and is reopenable any time from Help → Backup Time,
/// exactly like the Getting Started intro.
///
/// Estimates assume one record file per second-ish of sequential SFTP (~12 ms/file
/// measured on a fast link) — a one-time cost, because later backups send only the
/// records that changed.
/// </summary>
public sealed class BackupTimeForm : Form
{
    private static readonly (string Size, string Time)[] Estimates =
    {
        ("1 – 100 records", "a few seconds"),
        ("1,000", "~15 seconds"),
        ("10,000", "~2 minutes"),
        ("50,000", "~10 minutes"),
        ("100,000", "~20 minutes"),
        ("500,000", "~1.5 hours"),
        ("1,000,000", "~3 hours"),
    };

    /// <param name="preBackup">True when shown just before a first backup (offers
    /// Back Up Now / Cancel); false when opened from Help (just Close).</param>
    public BackupTimeForm(bool preBackup)
    {
        Text = "Apud — Backup Time";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(440, 372);

        var warning = new Label
        {
            Dock = DockStyle.Top,
            Height = 46,
            Padding = new Padding(18, 16, 18, 0),
            Font = new Font("Segoe UI", 9.75f, FontStyle.Bold),
            Text = "Warning! Large catalogs may take a while to upload on the very first backup.",
        };

        var table = new ListView
        {
            Dock = DockStyle.Top,
            Height = 194,
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Font = new Font("Segoe UI", 9.75f),
            MultiSelect = false,
        };
        table.Columns.Add("Catalogue size", 200, HorizontalAlignment.Left);
        table.Columns.Add("Approx. first backup", 200, HorizontalAlignment.Left);
        foreach (var (size, time) in Estimates)
            table.Items.Add(new ListViewItem(new[] { size, time }));

        var note = new Label
        {
            Dock = DockStyle.Top,
            Height = 74,
            Padding = new Padding(18, 8, 18, 0),
            ForeColor = SystemColors.GrayText,
            Font = new Font("Segoe UI", 9f),
            Text =
                "Times are approximate and depend on your connection. Only the first\n" +
                "backup uploads everything — later backups send just the records you\n" +
                "have changed, so they stay quick.",
        };

        var proceed = new Button
        {
            Text = preBackup ? "Back Up Now" : "Close",
            DialogResult = DialogResult.OK,
            Size = new Size(110, 30),
            Location = new Point(314, 332),
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
        };
        Controls.Add(proceed);

        if (preBackup)
        {
            var cancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Size = new Size(90, 30),
                Location = new Point(218, 332),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            };
            Controls.Add(cancel);
            CancelButton = cancel;
        }
        else
        {
            CancelButton = proceed;
        }

        // Docked controls add top-down; add bottom-most first so the visual order is
        // warning → table → note.
        Controls.Add(note);
        Controls.Add(table);
        Controls.Add(warning);
        AcceptButton = proceed;
    }
}
