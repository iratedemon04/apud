using System.Drawing;

namespace Apud.App;

/// <summary>
/// The Setup wizard (Module 10) — a short, explicit orientation so a new user on a
/// clean machine reaches their first record unaided. It writes nothing and decides
/// nothing on its own: it explains the workflow and hands off to the same
/// New/Open Catalogue actions the menus use. Honouring NO-SMART-BEHAVIOR, choosing
/// a catalogue is still a conscious act the user takes here — the wizard just points
/// the way. It auto-shows once on a fresh install (<see cref="AppState.FirstRunDone"/>)
/// and is reachable any time from Help → Setup.
/// </summary>
public sealed class FirstRunForm : Form
{
    private readonly Func<bool> _catalogOpen;
    private readonly Label _status;

    public FirstRunForm(Action newCatalog, Action openCatalog, Func<bool> catalogOpen)
    {
        _catalogOpen = catalogOpen;

        Text = "Apud — Setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(440, 190);

        var intro = new Label
        {
            Dock = DockStyle.Top,
            Height = 84,
            Padding = new Padding(16, 16, 16, 0),
            Font = new Font("Segoe UI", 9.75f),
            Text =
                "Create a new catalogue or open an existing one to begin. Records are " +
                "built from your templates (Ctrl+N). Press F1 on a field for help.",
        };

        var newBtn = new Button
        {
            Text = "&New Catalogue…",
            Size = new Size(196, 32),
            Location = new Point(16, 96),
        };
        newBtn.Click += (_, _) => { newCatalog(); RefreshStatus(); };

        var openBtn = new Button
        {
            Text = "&Open Catalogue…",
            Size = new Size(196, 32),
            Location = new Point(224, 96),
        };
        openBtn.Click += (_, _) => { openCatalog(); RefreshStatus(); };

        _status = new Label
        {
            Location = new Point(16, 138),
            Size = new Size(404, 20),
            Font = new Font("Segoe UI", 9.75f),
        };

        var done = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            Size = new Size(90, 30),
            Location = new Point(334, 150),
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
        };

        Controls.Add(newBtn);
        Controls.Add(openBtn);
        Controls.Add(_status);
        Controls.Add(done);
        Controls.Add(intro);
        AcceptButton = done;

        RefreshStatus();
    }

    private void RefreshStatus() =>
        _status.Text = _catalogOpen()
            ? "Catalogue open. Close this window, then press Ctrl+N to start a record."
            : "No catalogue open.";
}
