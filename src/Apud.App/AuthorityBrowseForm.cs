using Marc.Core;
using Apud.Data;

namespace Apud.App;

/// <summary>
/// The Ctrl+F4 authority browse popup (Module 8, docs/PLAN.md §6.3): a single
/// interleaved index of the AUT base's authorized headings and their see /
/// see-also references, opened positioned alphabetically at the bib field the
/// cursor is on (normalized comparison). Arrow keys scroll; typing in the box and
/// Enter re-positions; Enter (or double-click) on a line links the bib field to
/// that heading's authority record; Esc cancels and writes nothing.
///
/// The form is a thin skin: the positioned windows come from
/// <see cref="RecordRepository.BrowseHeadings"/> via the reposition callback, and
/// the actual heading write-back is <see cref="Headings.ApplyAuthorizedHeading"/>
/// in Marc.Core — so the authority logic is tested without WinForms.
/// </summary>
public sealed class AuthorityBrowseForm : Form
{
    private readonly Func<string, BrowseResult> _reposition;
    private readonly Func<long, string?> _authorizedDisplay;
    private readonly TextBox _positionBox;
    private readonly ListView _list;

    /// <summary>The chosen heading's authority record id, or null when cancelled.</summary>
    public long? SelectedAuthRecordId { get; private set; }

    /// <summary>The chosen line's display text (for the confirmation message).</summary>
    public string? SelectedDisplay { get; private set; }

    public AuthorityBrowseForm(string fieldText, BrowseResult initial,
        Func<string, BrowseResult> reposition, Func<long, string?> authorizedDisplay)
    {
        _reposition = reposition;
        _authorizedDisplay = authorizedDisplay;

        Text = "Browse Authority Headings — Ctrl+F4";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(560, 360);
        ClientSize = new Size(720, 480);

        var prompt = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            Text = "Position at:",
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
        };

        _positionBox = new TextBox { Dock = DockStyle.Top, Text = fieldText };
        _positionBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { Reposition(_positionBox.Text); e.SuppressKeyPress = true; }
        };

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _list.Columns.Add("", 70);           // kind marker (see / see also)
        _list.Columns.Add("Heading", 600);
        _list.DoubleClick += (_, _) => AcceptSelection();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { AcceptSelection(); e.SuppressKeyPress = true; }
        };

        var link = new Button { Text = "Link", Width = 84, DialogResult = DialogResult.None };
        link.Click += (_, _) => AcceptSelection();
        var cancel = new Button { Text = "Cancel", Width = 84, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(6),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(link);

        Controls.Add(_list);
        Controls.Add(_positionBox);
        Controls.Add(prompt);
        Controls.Add(buttons);
        AcceptButton = link;
        CancelButton = cancel;

        Fill(initial);
    }

    private void Reposition(string text) => Fill(_reposition(text));

    /// <summary>Redraws the list from a positioned window and lands the selection
    /// on the entry at/just after the search point.</summary>
    private void Fill(BrowseResult browse)
    {
        // Map each authority record to its authorized display, so a see-reference
        // can render "variant → see: authorized form" (§6.3.2) when both are in view.
        var authorized = browse.Entries
            .Where(e => e.Kind == HeadingKind.Authorized)
            .GroupBy(e => e.AuthRecordId)
            .ToDictionary(g => g.Key, g => g.First().Display);

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var e in browse.Entries)
        {
            string marker = e.Kind switch
            {
                HeadingKind.See => "see",
                HeadingKind.SeeAlso => "see also",
                _ => "",
            };
            // A see-reference shows its authorized target: prefer the copy already in
            // this window, else look it up (the authorized form usually sorts far from
            // the variant and so is out of view). Only if the record has no indexed
            // authorized heading at all does the bare variant show.
            string? target = null;
            if (e.Kind == HeadingKind.See &&
                !authorized.TryGetValue(e.AuthRecordId, out target))
                target = _authorizedDisplay(e.AuthRecordId);
            string text = target is { Length: > 0 }
                ? $"{e.Display}   →  see: {target}"
                : e.Display;

            var item = new ListViewItem(marker) { Tag = e };
            item.SubItems.Add(text);
            if (e.Kind != HeadingKind.Authorized) item.ForeColor = Color.DimGray;
            _list.Items.Add(item);
        }
        _list.EndUpdate();

        if (_list.Items.Count > 0)
        {
            int at = Math.Min(browse.Position, _list.Items.Count - 1);
            _list.Items[at].Selected = true;
            _list.Items[at].EnsureVisible();
            _list.Select();
        }
    }

    /// <summary>Enter/double-click/Link: take the selected line's authority record.
    /// A see or see-also line resolves to the very same authority record's 1XX (the
    /// reference and its authorized form live in one authority record), so linking
    /// always writes the authorized heading — jumping through the reference is
    /// automatic.</summary>
    private void AcceptSelection()
    {
        if (_list.SelectedItems.Count == 0) return;
        var entry = (BrowseHeading)_list.SelectedItems[0].Tag!;
        SelectedAuthRecordId = entry.AuthRecordId;
        SelectedDisplay = entry.Display;
        DialogResult = DialogResult.OK;
    }
}
