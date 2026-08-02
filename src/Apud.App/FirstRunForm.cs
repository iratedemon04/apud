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

        Text = "Welcome to Apud — Setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(500, 380);

        var intro = new Label
        {
            Dock = DockStyle.Top,
            Height = 232,
            Padding = new Padding(16, 14, 16, 0),
            Font = new Font("Segoe UI", 9.75f),
            Text =
                "Apud is a MARC21 original-cataloguing editor. A few things to know:\n\n" +
                "1.  A catalogue is a single database file. Create one now, or open one\n" +
                "     you already have. Apud never opens a catalogue on its own — you\n" +
                "     choose it consciously each session.\n\n" +
                "2.  Your record content comes from YOUR templates. Keep a .mrk template\n" +
                "     per book series / magazine / record type in the templates folder\n" +
                "     beside Apud; press Ctrl+N to start a record from one, or Ctrl+T to\n" +
                "     save the record you're on as a new template.\n\n" +
                "3.  Apud fills in only the mechanical data on push (Ctrl+L): the 001\n" +
                "     control number, the 005 timestamp and the leader lengths. Every\n" +
                "     other byte — org code, language, classification — is yours, typed\n" +
                "     or carried by the template.\n\n" +
                "4.  Keys are yours too: edit keymap.json beside Apud to rebind anything.\n" +
                "     Press F1 on a field any time for its MARC21 help.",
        };

        var newBtn = new Button
        {
            Text = "Create a &New Catalogue…",
            Size = new Size(220, 32),
            Location = new Point(16, 250),
        };
        newBtn.Click += (_, _) => { newCatalog(); RefreshStatus(); };

        var openBtn = new Button
        {
            Text = "&Open an Existing Catalogue…",
            Size = new Size(220, 32),
            Location = new Point(250, 250),
        };
        openBtn.Click += (_, _) => { openCatalog(); RefreshStatus(); };

        _status = new Label
        {
            Location = new Point(16, 296),
            Size = new Size(468, 36),
            Font = new Font("Segoe UI", 9.75f, FontStyle.Italic),
        };

        var done = new Button
        {
            Text = "Done",
            DialogResult = DialogResult.OK,
            Size = new Size(90, 30),
            Location = new Point(394, 338),
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
            ? "Catalogue open — you're ready. Close this, then press Ctrl+N to start your first record."
            : "No catalogue open yet — create or open one above to get started.";
}
