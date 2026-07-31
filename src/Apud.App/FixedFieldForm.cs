using Marc.Core.FixedFields;

namespace Apud.App;

/// <summary>
/// The position-by-position editor for a fixed field (Ctrl+F3, §6.2 / Module 7).
/// This is the ONE place in Apud where boxes-you-fill-out are the right shape
/// (Decisions, 2026-07-28): the cataloguer edits LDR/008 bytes by meaning instead
/// of counting spaces. Modal, OK/Cancel, two columns of labeled single-position
/// boxes exactly like the user's Aleph 008 dialog (docs/ALEPH-WORKFLOW.md). All
/// the logic lives in <see cref="FixedFieldData"/> in Marc.Core; this form is a
/// thin skin over it, so correctness is tested without WinForms.
/// </summary>
public sealed class FixedFieldForm : Form
{
    private readonly FixedFieldData _data;
    private readonly List<(FixedFieldPosition Pos, TextBox Box)> _boxes = new();

    /// <summary>The assembled fixed-field string; null until OK.</summary>
    public string? Result { get; private set; }

    public FixedFieldForm(string title, FixedFieldLayout layout, string current)
    {
        _data = new FixedFieldData(layout, current);
        PrefillAutoDate();

        Text = title;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(560, 320);
        ClientSize = new Size(760, 460);

        var mono = new Font("Consolas", 9.75f);
        int half = (layout.Positions.Count + 1) / 2;

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            AutoScroll = true,
            Padding = new Padding(8),
        };
        // Two mirrored triplets: label | box | meaning.
        foreach (var pct in new[] { 34f, 0f, 66f, 34f, 0f, 66f })
            grid.ColumnStyles.Add(pct == 0f
                ? new ColumnStyle(SizeType.AutoSize)
                : new ColumnStyle(SizeType.Percent, pct));

        for (int i = 0; i < layout.Positions.Count; i++)
        {
            var pos = layout.Positions[i];
            int row = i < half ? i : i - half;
            int colBase = i < half ? 0 : 3;

            var label = new Label
            {
                Text = pos.Label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(3, 5, 6, 3),
            };

            var box = new TextBox
            {
                Font = mono,
                MaxLength = pos.Len,
                Width = 14 + pos.Len * 9,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 2, 6, 2),
                Text = Display(_data.Slice(pos)),
            };
            if (pos.Protected)
            {
                box.ReadOnly = true;
                box.TabStop = false;
                box.BackColor = SystemColors.Control;
                box.ForeColor = SystemColors.GrayText;
            }

            var meaning = new Label
            {
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 5, 3, 3),
                Text = MeaningFor(pos, box.Text),
            };
            box.TextChanged += (_, _) => meaning.Text = MeaningFor(pos, box.Text);

            grid.Controls.Add(label, colBase, row);
            grid.Controls.Add(box, colBase + 1, row);
            grid.Controls.Add(meaning, colBase + 2, row);
            _boxes.Add((pos, box));
        }

        var ok = new Button { Text = "OK", Width = 84, DialogResult = DialogResult.None };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Text = "Cancel", Width = 84, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(6),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(grid);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;

        // Land the cursor on the first editable position.
        ActiveControl = _boxes.FirstOrDefault(b => !b.Pos.Protected).Box;
    }

    /// <summary>008/00-05 pre-fills with today (YYMMDD) when the field is blank,
    /// as it does on a fresh record in Aleph.</summary>
    private void PrefillAutoDate()
    {
        foreach (var pos in _data.Layout.Positions)
        {
            if (pos.Auto == "yymmdd" && string.IsNullOrWhiteSpace(_data.Slice(pos)))
                _data.Set(pos, DateTime.Now.ToString("yyMMdd"));
        }
    }

    private void Accept()
    {
        foreach (var (pos, box) in _boxes)
            _data.Set(pos, box.Text);
        Result = _data.ToString();
        DialogResult = DialogResult.OK;
    }

    /// <summary>Blanks show as empty boxes, not runs of spaces; a value with real
    /// content (even a trailing space) is shown as-is.</summary>
    private static string Display(string slice) => string.IsNullOrWhiteSpace(slice) ? "" : slice;

    /// <summary>The grey helper text beside a box: the decoded meaning of a coded
    /// position, a hint for a lookup position, or a flag for an unknown code.</summary>
    private static string MeaningFor(FixedFieldPosition pos, string text)
    {
        if (pos.Values is { } values)
        {
            string key = text.Length == 0 ? " " : text;
            if (values.TryGetValue(key, out var meaning)) return meaning;
            return text.Trim().Length > 0 ? "— undefined code —" : "";
        }
        if (pos.Lookup is "marc-countries") return "(country code)";
        if (pos.Lookup is "marc-languages") return "(language code)";
        return "";
    }
}
