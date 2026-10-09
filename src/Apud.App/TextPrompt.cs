namespace Apud.App;

/// <summary>The one-line text prompt (docs/MAINFORM-REFACTOR-PLAN.md step 5, moved out of
/// <see cref="MainForm"/>). The collaborators reach it through MainForm's
/// <c>PromptForText</c> callback, which passes the main window as owner.</summary>
public static class TextPrompt
{
    /// <summary>A minimal one-line text prompt (WinForms ships no InputBox). Returns the
    /// entered text on OK, or null on Cancel. Built inline, Aleph-plain.</summary>
    public static string? Show(IWin32Window owner, string title, string prompt, string initial, bool mask = false)
    {
        using var dialog = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(420, 130),
        };
        var label = new Label
        {
            Text = prompt,
            Location = new Point(14, 12),
            Size = new Size(392, 44),
            Font = new Font("Segoe UI", 9.75f),
        };
        var box = new TextBox
        {
            Text = initial,
            Location = new Point(14, 62),
            Size = new Size(392, 24),
            Font = new Font("Segoe UI", 9.75f),
            UseSystemPasswordChar = mask,
        };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Size = new Size(84, 28), Location = new Point(228, 94) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(84, 28), Location = new Point(320, 94) };
        dialog.Controls.Add(label);
        dialog.Controls.Add(box);
        dialog.Controls.Add(ok);
        dialog.Controls.Add(cancel);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        box.SelectAll();

        return dialog.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
    }
}
