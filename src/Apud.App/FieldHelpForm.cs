using System.Drawing;

namespace Apud.App;

/// <summary>
/// The F1 field-help panel (Module 10): a small non-modal window showing the
/// offline MARC21 help for one tag. One instance is kept by <see cref="MainForm"/>
/// and re-pointed each time F1 is pressed, so it reads like a docked reference panel
/// that follows the caret without stealing focus from the editor. Text comes from
/// <see cref="FieldHelp"/>; the header names the tag via <see cref="TagNames"/>.
/// </summary>
public sealed class FieldHelpForm : Form
{
    private readonly Label _header;
    private readonly TextBox _body;

    public FieldHelpForm()
    {
        Text = "Field Help";
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        MinimumSize = new Size(340, 200);
        ClientSize = new Size(400, 260);

        _header = new Label
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(10, 8, 10, 0),
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            ForeColor = Color.DarkRed,
        };
        _body = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Segoe UI", 9.75f),
            BackColor = SystemColors.Window,
            TabStop = false,
        };
        var pad = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 4, 10, 10) };
        pad.Controls.Add(_body);

        Controls.Add(pad);
        Controls.Add(_header);
    }

    /// <summary>Points the panel at a tag: header = tag + name, body = its help.</summary>
    public void ShowHelp(string tag)
    {
        string name = tag == "LDR" ? "Leader" : TagNames.For(tag);
        _header.Text = name.Length > 0 ? $"{tag} — {name}" : tag;
        _body.Text = FieldHelp.For(tag);
        _body.SelectionStart = 0;
        _body.SelectionLength = 0;
    }
}
