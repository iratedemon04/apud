using System.Drawing;
using System.Windows.Forms;

namespace Apud.App;

/// <summary>
/// The first-backup notice: warns that a large catalogue's <b>very first</b> server
/// backup uploads every record and can take a while, with a table of rough estimates
/// up to a million records. Auto-shows once before the first backup of each catalogue
/// (any size — even one record) and is reopenable any time from Help → Backup Times,
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
        Text = "Apud — Backup Times";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(440, 302);

        var warning = new Label
        {
            Dock = DockStyle.Top,
            Height = 56,
            Padding = new Padding(18, 16, 18, 0),
            Font = new Font("Segoe UI", 9.75f, FontStyle.Bold),
            Text = "Warning, the first backup upload may take a while depending on the size of your catalogue.",
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

        var proceed = new Button
        {
            Text = preBackup ? "Back Up Now" : "Close",
            DialogResult = DialogResult.OK,
            Size = new Size(110, 30),
            Location = new Point(314, 260),
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
                Location = new Point(218, 260),
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
        // warning → table.
        Controls.Add(table);
        Controls.Add(warning);
        AcceptButton = proceed;
    }
}
