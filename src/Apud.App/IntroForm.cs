using System.Drawing;

namespace Apud.App;

/// <summary>
/// A quick, terse three-step introduction — create a catalogue, import records,
/// search — that replaces the old Setup wizard (user request 2026-08-01). It
/// writes nothing and offers no actions: it just tells the user the three menu
/// steps to get going. Auto-shows once on a fresh install
/// (<see cref="AppState.FirstRunDone"/>) and is reopenable any time from
/// Help → Getting Started.
/// </summary>
public sealed class IntroForm : Form
{
    public IntroForm()
    {
        Text = "Apud — Getting Started";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(440, 220);

        var body = new Label
        {
            Dock = DockStyle.Top,
            Height = 168,
            Padding = new Padding(18, 16, 18, 0),
            Font = new Font("Segoe UI", 9.75f),
            Text =
                "1.  Create a catalogue — File → New Catalogue.\n\n" +
                "2.  Import records — File → Import Records, then pick a single\n" +
                "      file or a whole folder of .mrk files.\n\n" +
                "3.  Search it — type in the search box, choose a scope, press\n" +
                "      Enter. Double-click a result to open it.\n\n" +
                "Reopen this any time from Help → Getting Started.",
        };

        var close = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            Size = new Size(90, 30),
            Location = new Point(334, 178),
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
        };

        Controls.Add(close);
        Controls.Add(body);
        AcceptButton = close;
        CancelButton = close;
    }
}
