namespace Apud.App;

/// <summary>
/// Application shell: menu bar, central workspace (record tabs arrive in Module 6),
/// and the message bar that validation output will write to (Module 9).
/// Menus are placeholders until the command table / keymap engine exists (Module 6);
/// from then on they are rendered from the command table, never hand-maintained.
/// </summary>
public sealed class MainForm : Form
{
    private readonly MenuStrip _menu;
    private readonly StatusStrip _messageBar;
    private readonly ToolStripStatusLabel _messageLabel;

    public MainForm()
    {
        Text = "Apud";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 600);

        _menu = new MenuStrip();
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (_, _) => Close())
        {
            ShortcutKeys = Keys.Alt | Keys.F4
        });
        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(new ToolStripMenuItem("&About Apud", null, (_, _) =>
            MessageBox.Show(this,
                $"Apud {Application.ProductVersion}\nMARC21 original cataloguing.",
                "About Apud", MessageBoxButtons.OK, MessageBoxIcon.Information)));
        _menu.Items.Add(file);
        _menu.Items.Add(help);

        _messageLabel = new ToolStripStatusLabel("Ready.") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _messageBar = new StatusStrip();
        _messageBar.Items.Add(_messageLabel);

        Controls.Add(_messageBar);
        Controls.Add(_menu);
        MainMenuStrip = _menu;
    }
}
