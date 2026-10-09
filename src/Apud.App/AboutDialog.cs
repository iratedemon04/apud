namespace Apud.App;

/// <summary>Help → About Apud: the version line plus a Legal link to the MIT licence
/// (docs/MAINFORM-REFACTOR-PLAN.md step 5, moved out of <see cref="MainForm"/> so the
/// licence text doesn't sit in the shell). Built inline, Aleph-plain.</summary>
public static class AboutDialog
{
    public static void Show(IWin32Window owner)
    {
        using var about = new Form
        {
            Text = "About Apud",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(380, 150),
        };
        var label = new Label
        {
            Text = $"Apud {Application.ProductVersion}\nMARC21 original cataloguing.",
            Dock = DockStyle.Top,
            Height = 80,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 9.75f),
        };
        var legal = new LinkLabel { Text = "Legal", AutoSize = true, Location = new Point(18, 108) };
        legal.LinkClicked += (_, _) => ShowLegal(about);
        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Size = new Size(84, 28),
            Location = new Point(about.ClientSize.Width - 100, 104),
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
        };
        about.Controls.Add(label);
        about.Controls.Add(legal);
        about.Controls.Add(ok);
        about.AcceptButton = ok;
        about.ShowDialog(owner);
    }

    private static void ShowLegal(IWin32Window owner) =>
        MessageBox.Show(owner,
            "Copyright (c) Alonso Cossío Vázquez 2026\n\n" +
            "MIT License\n\n" + MitLicense,
            "Legal", MessageBoxButtons.OK, MessageBoxIcon.Information);

    /// <summary>The MIT License text (Apud is released under it).</summary>
    private const string MitLicense =
        "Permission is hereby granted, free of charge, to any person obtaining a copy " +
        "of this software and associated documentation files (the \"Software\"), to deal " +
        "in the Software without restriction, including without limitation the rights " +
        "to use, copy, modify, merge, publish, distribute, sublicense, and/or sell " +
        "copies of the Software, and to permit persons to whom the Software is " +
        "furnished to do so, subject to the following conditions:\n\n" +
        "The above copyright notice and this permission notice shall be included in all " +
        "copies or substantial portions of the Software.\n\n" +
        "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR " +
        "IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, " +
        "FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE " +
        "AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER " +
        "LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, " +
        "OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE " +
        "SOFTWARE.";
}
