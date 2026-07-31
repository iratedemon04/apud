namespace Apud.App;

/// <summary>
/// Ctrl+N with no record showing: pick a template from templates\ beside the
/// exe. Plain list of file names, Enter/double-click/OK — Aleph's "new record"
/// dialog shape, nothing more.
/// </summary>
public sealed class TemplatePickerForm : Form
{
    private readonly ListBox _list;

    /// <summary>Full path of the chosen template; null until OK.</summary>
    public string? SelectedPath { get; private set; }

    public TemplatePickerForm(IReadOnlyList<string> templatePaths)
    {
        Text = "New Record";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(320, 260);

        var label = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            Text = "Template:",
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
        };

        _list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        foreach (var path in templatePaths.OrderBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase))
            _list.Items.Add(new TemplateEntry(path));
        _list.SelectedIndex = 0;
        _list.DoubleClick += (_, _) => Accept();

        var ok = new Button { Text = "OK", DialogResult = DialogResult.None, Width = 80 };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 80 };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 36,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(4),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(_list);
        Controls.Add(label);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void Accept()
    {
        if (_list.SelectedItem is not TemplateEntry entry) return;
        SelectedPath = entry.Path;
        DialogResult = DialogResult.OK;
    }

    private sealed record TemplateEntry(string Path)
    {
        public override string ToString() => System.IO.Path.GetFileNameWithoutExtension(Path);
    }
}
