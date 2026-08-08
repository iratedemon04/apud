using System.Runtime.InteropServices;
using Marc.Core;

namespace Apud.App;

/// <summary>
/// The record editor's view: a grid of real, borderless <see cref="TextBox"/>
/// controls (one row per subfield line), replacing the old <see cref="DataGridView"/>
/// and its edit-mode state machine. Real textboxes have deterministic focus and
/// caret — <c>box.Focus()</c> cannot "fail to land", and an empty tag box simply
/// accepts typing (no placeholder to overflow, so no Windows ding). This is the
/// same shape <c>FixedFieldForm</c> already uses bug-free.
///
/// Performance: controls are POOLED and laid out by hand (absolute bounds), so a
/// rebuild REPOSITIONS existing controls instead of destroying and recreating
/// window handles — spamming F6/F7 stays snappy. Painting is frozen across a
/// rebuild so there is no flicker.
///
/// Dumb view: it owns layout, focus, and the Tab/arrow flow, and commits each box
/// back into the <see cref="EditorDocument"/> through the SAME model methods
/// (SetLeader/SetTag/SetIndicators/SetSubfieldCode/SetSubfieldValue/SetControlData).
/// All structure/validation/authority logic stays in the model. Row shaping is
/// <see cref="RecordLayout"/> (pure, unit-tested).
/// </summary>
public sealed class RecordGrid : Panel
{
    // Visual-fidelity constants (docs/UI-REWRITE-PLAN.md) — reproduce the old
    // DataGridView value column 1:1.
    private static readonly Font MonoValue = new("Consolas", 9.75f, FontStyle.Bold);
    private static readonly Font MonoTag = new("Consolas", 9.75f, FontStyle.Bold | FontStyle.Underline);
    private static readonly Font MonoInd = new("Consolas", 9.75f);
    private static readonly Font MonoCode = new("Consolas", 9.75f, FontStyle.Bold | FontStyle.Underline);
    private static readonly Font NameFont = new("Segoe UI", 8.25f, FontStyle.Italic);
    private static readonly Color ApudBlue = Color.FromArgb(225, 238, 250);

    private const int NameW = 140, TagW = 42, IndW = 34, CodeW = 26;
    private const int FixedW = NameW + TagW + IndW + CodeW;
    private const int NameX = 0, TagX = NameW, IndX = NameW + TagW, CodeX = NameW + TagW + IndW, ValueX = FixedW;

    // Spike-1 parity constants: the raw text measure runs ~2px short of the old
    // DataGridView row, and its cell reserved a few px of horizontal padding (so it
    // wrapped slightly earlier). Under-estimating width errs toward a taller row
    // (never clips); the screenshot-parity gate tunes these.
    private const int VPad = 2;
    private const int HInset = 8;
    private const int LineH = 17;

    [DllImport("user32.dll")]
    private static extern int SendMessage(IntPtr hWnd, int msg, bool wParam, int lParam);
    private const int WM_SETREDRAW = 0x000B;

    private readonly Panel _body; // inner content panel; this (AutoScroll) scrolls it

    // Control pools — reused across rebuilds so no window handles are created after
    // the first build. Micro (single-line) and wide (multiline) are pooled apart so
    // a reused box never has to flip Multiline (which would recreate its handle).
    private readonly List<TextBox> _microPool = new();
    private readonly List<TextBox> _widePool = new();
    private readonly List<Label> _labelPool = new();

    private readonly List<(TextBox Box, BoxSpec Spec)> _boxes = new(); // active boxes, in flow order
    private readonly List<(Control C, BoxPart Part, int Row, bool Wide, bool IsName)> _placements = new();
    private readonly Dictionary<int, Label> _nameLabels = new(); // fieldIndex -> its maroon label
    private readonly HashSet<int> _selectedFields = new();       // multi-field selection for bulk delete
    private int _anchorField = -1;                               // Shift-click range anchor
    private int _dragStartField = -1;                            // field the current drag began on
    private bool _dragArmed;                                     // a plain mouse-down that may become a row drag
    private bool _dragging;                                      // a row-select drag is in progress
    private int _rowCount;

    private EditorDocument? _doc;
    private TextBox? _focused;
    private bool _suspendCommit; // true during rebuild / programmatic navigation
    private bool _measuring;     // re-entrancy guard for the resize -> relayout loop

    public event EventHandler? EditCommitted;
    public event Action<string>? Message;

    public RecordGrid()
    {
        Dock = DockStyle.Fill;
        AutoScroll = true;
        BackColor = SystemColors.Window;
        DoubleBuffered = true;
        _body = new Panel { Location = new Point(0, 0), BackColor = SystemColors.Window };
        Controls.Add(_body);
        ClientSizeChanged += (_, _) => LayoutRows();
    }

    public EditorDocument? Document
    {
        get => _doc;
        set { _doc = value; Rebuild(); }
    }

    /// <summary>True when the caret is in one of the grid's boxes.</summary>
    public bool EditorHasFocus => ContainsFocus;

    // ---------- build ----------

    public void Clear() { _doc = null; Rebuild(); }

    public void Rebuild(bool preserveFocus = false)
    {
        BoxSpec? keep = preserveFocus ? _focused?.Tag as BoxSpec : null;
        RebuildCore();
        if (keep is not null) FocusElement(keep.FieldIndex, keep.SubfieldIndex, keep.Part);
    }

    private void RebuildCore()
    {
        _suspendCommit = true;
        bool frozen = IsHandleCreated;
        if (frozen) SendMessage(Handle, WM_SETREDRAW, false, 0); // freeze paint: no flicker
        SuspendLayout();
        try
        {
            _boxes.Clear();
            _placements.Clear();
            _nameLabels.Clear();
            _selectedFields.Clear(); // selection clears on every rebuild/edit
            _anchorField = -1;
            _focused = null;
            _rowCount = 0;

            int micro = 0, wide = 0, label = 0;
            if (_doc is not null)
            {
                var specs = RecordLayout.Build(_doc.Record);
                _rowCount = specs.Count == 0 ? 0 : specs[^1].Row + 1;

                foreach (var spec in specs)
                {
                    if (spec.Name is not null)
                    {
                        var lbl = AcquireLabel(ref label);
                        lbl.Text = spec.Name;
                        lbl.Tag = spec.FieldIndex;
                        lbl.BackColor = SystemColors.Window;
                        if (spec.FieldIndex >= 0) _nameLabels[spec.FieldIndex] = lbl;
                        _placements.Add((lbl, BoxPart.Leader, spec.Row, false, true));
                    }

                    bool isWide = spec.Part is BoxPart.Value or BoxPart.ControlData or BoxPart.Leader;
                    var box = isWide ? AcquireWide(ref wide) : AcquireMicro(ref micro);
                    ConfigureBox(box, spec);
                    _boxes.Add((box, spec));
                    _placements.Add((box, spec.Part, spec.Row, isWide, false));
                }
            }

            // Park pooled controls we didn't use this build (kept for next time).
            for (int i = micro; i < _microPool.Count; i++) _microPool[i].Visible = false;
            for (int i = wide; i < _widePool.Count; i++) _widePool[i].Visible = false;
            for (int i = label; i < _labelPool.Count; i++) _labelPool[i].Visible = false;

            LayoutRows();
        }
        finally
        {
            ResumeLayout(false);
            // Invalidate (not Update): schedule ONE async repaint so rapid rebuilds
            // (holding F7) coalesce into a single paint instead of one per keystroke.
            if (frozen) { SendMessage(Handle, WM_SETREDRAW, true, 0); Invalidate(true); }
            _suspendCommit = false;
        }
    }

    private TextBox AcquireMicro(ref int used)
    {
        if (used < _microPool.Count) { var b = _microPool[used++]; b.Visible = true; return b; }
        var box = NewBox(wide: false);
        _microPool.Add(box);
        _body.Controls.Add(box);
        used++;
        return box;
    }

    private TextBox AcquireWide(ref int used)
    {
        if (used < _widePool.Count) { var b = _widePool[used++]; b.Visible = true; return b; }
        var box = NewBox(wide: true);
        _widePool.Add(box);
        _body.Controls.Add(box);
        used++;
        return box;
    }

    private Label AcquireLabel(ref int used)
    {
        if (used < _labelPool.Count) { var l = _labelPool[used++]; l.Visible = true; return l; }
        var lbl = new Label
        {
            Font = NameFont,
            ForeColor = Color.Maroon,
            AutoSize = false,
            Margin = new Padding(0),
            Padding = new Padding(0),
            TextAlign = ContentAlignment.TopLeft,
        };
        lbl.MouseDown += (_, e) => OnSelDown(lbl, e);
        lbl.MouseMove += (_, e) => OnSelMove(lbl, e);
        lbl.MouseUp += (_, _) => OnSelUp();
        _labelPool.Add(lbl);
        _body.Controls.Add(lbl);
        used++;
        return lbl;
    }

    private TextBox NewBox(bool wide)
    {
        var box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Window,
            Margin = new Padding(0),
            Multiline = wide, // fixed for the life of the control (pooled by kind)
        };
        if (wide) { box.WordWrap = true; box.ScrollBars = ScrollBars.None; box.AcceptsReturn = false; }
        box.Enter += (_, _) => OnBoxEnter(box);
        box.Leave += (_, _) => OnBoxLeave(box);
        box.TextChanged += (_, _) => OnBoxTextChanged(box);
        box.MouseDown += (_, e) => OnSelDown(box, e);
        box.MouseMove += (_, e) => OnSelMove(box, e);
        box.MouseUp += (_, _) => OnSelUp();
        return box;
    }

    /// <summary>Re-points a pooled box at a spec: its text, identity, font/colour,
    /// and length rule. Multiline never changes (pools are split by kind).</summary>
    private static void ConfigureBox(TextBox box, BoxSpec spec)
    {
        box.Tag = spec;
        box.BackColor = SystemColors.Window;
        box.MaxLength = spec.MaxLength == RecordLayout.Unlimited ? 0 : spec.MaxLength;
        box.Font = spec.Part switch
        {
            BoxPart.Tag => MonoTag,
            BoxPart.Ind => MonoInd,
            BoxPart.Code => MonoCode,
            _ => MonoValue,
        };
        box.ForeColor = spec.Part == BoxPart.Tag ? Color.Gray : Color.Black;
        if (box.Text != spec.Text) box.Text = spec.Text;
    }

    private static int ColX(BoxPart part) => part switch
    {
        BoxPart.Tag => TagX,
        BoxPart.Ind => IndX,
        BoxPart.Code => CodeX,
        _ => ValueX,
    };

    private static int ColW(BoxPart part, int valueW) => part switch
    {
        BoxPart.Tag => TagW,
        BoxPart.Ind => IndW,
        BoxPart.Code => CodeW,
        _ => valueW,
    };

    // ---------- manual layout (fast; also re-wraps on resize) ----------

    /// <summary>Positions every active control by hand: fixed columns, one row per
    /// subfield line, wrapped value/leader/control rows measured explicitly (a
    /// multiline TextBox reports no preferred height — Spike 1). O(n), no
    /// TableLayoutPanel. Runs on rebuild and on resize.</summary>
    private void LayoutRows()
    {
        if (_measuring) return;
        _measuring = true;
        try
        {
            if (_doc is null || _rowCount == 0) { _body.Size = new Size(ClientSize.Width, 0); return; }

            int valueW = Math.Max(60, ClientSize.Width - FixedW);

            if (valueW != _cacheWidth) { _heightCache.Clear(); _cacheWidth = valueW; }
            var rowH = new int[_rowCount];
            Array.Fill(rowH, LineH + VPad);
            foreach (var p in _placements)
                if (p.Wide && p.C is TextBox tb)
                    rowH[p.Row] = MeasureWide(tb, valueW);

            var rowY = new int[_rowCount];
            int acc = 0;
            for (int r = 0; r < _rowCount; r++) { rowY[r] = acc; acc += rowH[r]; }

            foreach (var p in _placements)
            {
                if (p.IsName) { p.C.SetBounds(NameX, rowY[p.Row], NameW, LineH); continue; }
                int h = p.Wide ? rowH[p.Row] : LineH;
                p.C.SetBounds(ColX(p.Part), rowY[p.Row], ColW(p.Part, valueW), h);
            }

            _body.Size = new Size(ClientSize.Width, acc);
        }
        finally { _measuring = false; }
    }

    // Memoize wrapped-row heights: on a rebuild most rows' text/width are unchanged,
    // so measuring only the edited row keeps F7 spam cheap. Keyed by "font|text";
    // cleared when the value column width changes (a resize).
    private readonly Dictionary<string, int> _heightCache = new();
    private int _cacheWidth = -1;

    private int MeasureWide(TextBox b, int valueW)
    {
        if (b.Text.Length == 0) return LineH + VPad;
        string key = b.Font.Bold + "" + b.Text;
        if (_heightCache.TryGetValue(key, out int cached)) return cached;
        var sz = TextRenderer.MeasureText(b.Text, b.Font, new Size(valueW - HInset, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding);
        int h = Math.Max(LineH, sz.Height) + VPad;
        _heightCache[key] = h;
        return h;
    }

    // ---------- focus / navigation ----------

    private void OnBoxEnter(TextBox box)
    {
        _focused = box;
        RecolorAll();
        // Select the whole 1-3 char content so the first keystroke REPLACES it (a
        // micro box is always full — "__"/"10"/a code — so otherwise the caret sits
        // at the end of a full box and typing dings). Deferred so it survives a mouse
        // click's own caret placement, not just Tab/arrow arrival.
        if (box.Tag is BoxSpec { Part: BoxPart.Tag or BoxPart.Ind or BoxPart.Code })
            BeginInvoke(() => { if (box.Focused && !box.IsDisposed) box.SelectAll(); });
    }

    private void OnBoxLeave(TextBox box)
    {
        if (!_suspendCommit) CommitInternal(box);
        if (ReferenceEquals(_focused, box)) _focused = null;
        RecolorAll();
    }

    private void OnBoxTextChanged(TextBox box)
    {
        if (_suspendCommit || !box.Focused || box.Tag is not BoxSpec spec) return;
        if (box.MaxLength <= 0 || box.TextLength < box.MaxLength) return;

        // A filled indicators (2) or subfield-code (1) box hands off to the next box
        // so the cataloguer types straight through. Skip a code box on an empty field
        // (SubfieldIndex -1): committing it CREATES the subfield (structural), which
        // goes through the normal path, not this in-place hop.
        bool hop = spec.Part == BoxPart.Ind
                   || (spec.Part == BoxPart.Code && spec.SubfieldIndex >= 0);
        if (hop) BeginInvoke(() => MoveNext(forward: true));
    }

    /// <summary>Keyboard navigation between boxes. Tab / Shift+Tab walk the logical
    /// flow (tag -> ind -> code -> value -> next subfield's code ...); arrows move
    /// like a text editor — Left/Right hop to the neighbouring box at the ends of a
    /// value (always, for the 1-3 char micro boxes), Up/Down move between rows.
    /// Handled here so it beats the textbox's own handling and the host's dispatch,
    /// and so a structural rebuild mid-flow still lands focus on the intended box.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_doc is not null && ContainsFocus && _focused is { } b)
        {
            bool micro = b.Tag is BoxSpec { Part: BoxPart.Tag or BoxPart.Ind or BoxPart.Code };
            switch (keyData)
            {
                case Keys.Tab: MoveNext(forward: true); return true;
                case Keys.Tab | Keys.Shift: MoveNext(forward: false); return true;
                case Keys.Right when micro || AtEnd(b): MoveNext(forward: true); return true;
                case Keys.Left when micro || AtStart(b): MoveNext(forward: false); return true;
                case Keys.Down when micro || OnLastLine(b): MoveRow(+1); return true;
                case Keys.Up when micro || OnFirstLine(b): MoveRow(-1); return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private static bool AtEnd(TextBox b) => b.SelectionLength == 0 && b.SelectionStart >= b.TextLength;
    private static bool AtStart(TextBox b) => b.SelectionLength == 0 && b.SelectionStart == 0;
    private static bool OnFirstLine(TextBox b) => b.GetLineFromCharIndex(b.SelectionStart) == 0;
    private static bool OnLastLine(TextBox b) =>
        b.GetLineFromCharIndex(b.SelectionStart) == b.GetLineFromCharIndex(b.TextLength);

    private void MoveNext(bool forward)
    {
        if (_boxes.Count == 0) return;
        _suspendCommit = true;
        try
        {
            var curSpec = _focused?.Tag as BoxSpec;
            bool structural = _focused is not null && CommitInternal(_focused);
            if (structural) RebuildCore();

            int pos = curSpec is null ? -1 : Locate(curSpec.FieldIndex, curSpec.SubfieldIndex, curSpec.Part);
            int target = pos < 0 ? (forward ? 0 : _boxes.Count - 1) : pos + (forward ? 1 : -1);
            if (target >= 0 && target < _boxes.Count) FocusBoxAt(target);
        }
        finally { _suspendCommit = false; }
    }

    /// <summary>Up/Down: move to the box in the adjacent row, preferring the same
    /// column (part), else that row's first box.</summary>
    private void MoveRow(int delta)
    {
        if (_boxes.Count == 0 || _focused?.Tag is not BoxSpec cur) return;
        _suspendCommit = true;
        try
        {
            if (CommitInternal(_focused)) RebuildCore();
            int targetRow = cur.Row + delta;
            int i = _boxes.FindIndex(x => x.Spec.Row == targetRow && x.Spec.Part == cur.Part);
            if (i < 0) i = _boxes.FindIndex(x => x.Spec.Row == targetRow);
            if (i >= 0) FocusBoxAt(i);
        }
        finally { _suspendCommit = false; }
    }

    private void FocusBoxAt(int i)
    {
        var box = _boxes[i].Box;
        ScrollControlIntoView(box);
        // Authoritative: because controls are POOLED, the target may be the very
        // physical control that already holds OS focus (reused for a shifted field
        // after a rebuild). Then box.Focus() is a no-op and Enter never re-fires, so
        // set the tracking/affordances here directly — otherwise CurrentRef() goes
        // null and the next command ("stand in a field first") fails. This is what
        // lets you spam Ctrl+F5 down a record, or delete a gutter selection.
        _focused = box;
        if (!box.Focused) box.Focus();
        RecolorAll();
        if (box.Tag is BoxSpec { Part: BoxPart.Tag or BoxPart.Ind or BoxPart.Code })
            BeginInvoke(() => { if (box.Focused && !box.IsDisposed) box.SelectAll(); });
    }

    /// <summary>Index into the ordered box list for a model element, with fallback:
    /// exact (field, subfield, part), then (field, part) — so a phantom subfield
    /// (-1) that just became real (0) still resolves — then the field's first box.</summary>
    private int Locate(int fieldIndex, int subfieldIndex, BoxPart part)
    {
        int byAll = _boxes.FindIndex(b => b.Spec.FieldIndex == fieldIndex
            && b.Spec.SubfieldIndex == subfieldIndex && b.Spec.Part == part);
        if (byAll >= 0) return byAll;
        int byPart = _boxes.FindIndex(b => b.Spec.FieldIndex == fieldIndex && b.Spec.Part == part);
        if (byPart >= 0) return byPart;
        return _boxes.FindIndex(b => b.Spec.FieldIndex == fieldIndex);
    }

    /// <summary>Puts the caret on a model element (replaces SelectCell). Leader is
    /// FieldIndex -1. Falls back within the field when the exact part/subfield is
    /// gone after a reshape.</summary>
    public void FocusElement(int fieldIndex, int subfieldIndex, BoxPart part)
    {
        int i = Locate(fieldIndex, subfieldIndex, part);
        if (i >= 0) FocusBoxAt(i);
    }

    /// <summary>Puts the caret on a field's first row at the given part (replaces
    /// SelectFieldRow).</summary>
    public void FocusField(int fieldIndex, BoxPart part) => FocusElement(fieldIndex, -1, part);

    /// <summary>Insert: ensure the caret sits in an editable box; on a value box it
    /// drops the caret at the start so a filled field is prepended.</summary>
    public void FocusForEdit()
    {
        if (_focused is null) { if (_boxes.Count > 0) FocusBoxAt(0); return; }
        if (_focused.Tag is BoxSpec { Part: BoxPart.Value or BoxPart.ControlData or BoxPart.Leader })
        {
            _focused.SelectionStart = 0;
            _focused.SelectionLength = 0;
        }
    }

    // ---------- multi-field selection (drag / Shift / Ctrl from anywhere) ----------

    /// <summary>Field indices the user has selected for a one-step multi-field
    /// delete. Built by dragging the mouse down/up across rows (from any box), or
    /// Shift/Ctrl-clicking a field. Empty when nothing is selected (the host then
    /// deletes just the field under the caret).</summary>
    public IReadOnlyCollection<int> SelectedFieldIndices => _selectedFields;

    private static int FieldOf(Control c) =>
        c is TextBox { Tag: BoxSpec s } ? s.FieldIndex : c is Label { Tag: int fi } ? fi : -1;

    private void OnSelDown(Control c, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        int field = FieldOf(c);
        var mods = ModifierKeys;

        if ((mods & Keys.Control) != 0) // toggle one field in/out
        {
            if (field >= 0) { if (!_selectedFields.Add(field)) _selectedFields.Remove(field); _anchorField = field; }
            _dragArmed = false; RecolorAll(); return;
        }
        if ((mods & Keys.Shift) != 0 && _anchorField >= 0) // extend a range
        {
            SelectRange(_anchorField, field); _dragArmed = false; RecolorAll(); return;
        }

        // Plain press: this is an edit click, so clear any selection — but ARM a
        // drag. If the mouse then moves into another row it becomes a row-select
        // (a drag that stays in this box is just normal text selection).
        _selectedFields.Clear();
        _dragStartField = field;
        if (field >= 0) _anchorField = field;
        _dragArmed = field >= 0;
        _dragging = false;
        RecolorAll();
    }

    private void OnSelMove(Control c, MouseEventArgs e)
    {
        if (!_dragArmed || (MouseButtons & MouseButtons.Left) == 0) return;
        int cur = FieldAtY(_body.PointToClient(c.PointToScreen(e.Location)).Y);
        if (cur < 0) return;
        if (_dragging || cur != _dragStartField)
        {
            _dragging = true;
            if (c is TextBox tb) tb.SelectionLength = 0; // suppress the origin box's own text-drag
            SelectRange(_dragStartField, cur);
            RecolorAll();
        }
    }

    private void OnSelUp() { _dragArmed = false; _dragging = false; }

    private void SelectRange(int a, int b)
    {
        _selectedFields.Clear();
        if (a < 0) a = 0;
        if (b < 0) b = 0;
        for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++)
            if (i >= 0) _selectedFields.Add(i);
    }

    /// <summary>Which field's row contains the given Y (in _body coords); clamps to
    /// the first/last field when a drag runs past the ends.</summary>
    private int FieldAtY(int y)
    {
        if (_boxes.Count == 0) return -1;
        foreach (var (box, spec) in _boxes)
            if (y >= box.Top && y < box.Bottom) return spec.FieldIndex;
        return y < _boxes[0].Box.Top ? _boxes[0].Spec.FieldIndex : _boxes[^1].Spec.FieldIndex;
    }

    /// <summary>A box is tinted apud-blue when it is focused OR its field is
    /// selected; else white. The name label follows its field's selection.</summary>
    private void RecolorAll()
    {
        if (_suspendCommit) return;
        foreach (var (box, spec) in _boxes)
            box.BackColor = ReferenceEquals(box, _focused) || _selectedFields.Contains(spec.FieldIndex)
                ? ApudBlue : SystemColors.Window;
        foreach (var (fi, lbl) in _nameLabels)
            lbl.BackColor = _selectedFields.Contains(fi) ? ApudBlue : SystemColors.Window;
    }

    /// <summary>The field/subfield the caret is on, in model indices.</summary>
    public (int FieldIndex, int SubfieldIndex)? CurrentRef() =>
        _focused?.Tag is BoxSpec s ? (s.FieldIndex, s.SubfieldIndex) : null;

    /// <summary>The full element (with box part) the caret is on — the host needs
    /// the part to re-land the cursor on the same column after ordering fields.</summary>
    public (int FieldIndex, int SubfieldIndex, BoxPart Part)? CurrentElement() =>
        _focused?.Tag is BoxSpec s ? (s.FieldIndex, s.SubfieldIndex, s.Part) : null;

    // ---------- commit ----------

    /// <summary>Commits the focused box into the document (replaces EndEdit). Host
    /// command handlers call this before a model op, then Rebuild + FocusElement.</summary>
    public void CommitFocused()
    {
        if (_focused is not null) CommitInternal(_focused);
    }

    /// <summary>Writes one box back through the model. Returns true when the edit
    /// changed the record's SHAPE (a tag change, or a value/code typed onto an empty
    /// field creating its first subfield) — the caller then rebuilds.</summary>
    private bool CommitInternal(TextBox box)
    {
        if (_doc is null || box.Tag is not BoxSpec spec) return false;
        string text = box.Text;
        string? error = null;
        bool structural = false;

        switch (spec.Part)
        {
            case BoxPart.Leader:
                error = _doc.SetLeader(text);
                break;
            case BoxPart.ControlData:
                _doc.SetControlData(spec.FieldIndex, text);
                break;
            case BoxPart.Tag:
                error = _doc.SetTag(spec.FieldIndex, text);
                structural = error is null;
                break;
            case BoxPart.Ind:
                _doc.SetIndicators(spec.FieldIndex, text);
                break;
            case BoxPart.Code:
                structural = spec.SubfieldIndex < 0 && text.Length > 0;
                _doc.SetSubfieldCode(spec.FieldIndex, spec.SubfieldIndex, text);
                break;
            case BoxPart.Value:
                structural = spec.SubfieldIndex < 0 && text.Length > 0;
                _doc.SetSubfieldValue(spec.FieldIndex, spec.SubfieldIndex, text);
                break;
        }

        if (error is not null) { Message?.Invoke(error); RefreshBox(box, spec); }
        else if (!structural) RefreshBox(box, spec); // normalize ^/_ display in place

        EditCommitted?.Invoke(this, EventArgs.Empty);
        return structural;
    }

    /// <summary>Re-reads a box's canonical display from the model after a
    /// non-structural commit (leader/control carets, indicator underscores, a
    /// refused tag reverting).</summary>
    private void RefreshBox(TextBox box, BoxSpec spec)
    {
        if (_doc is null) return;
        bool prev = _suspendCommit;
        _suspendCommit = true;
        try
        {
            box.Text = spec.Part switch
            {
                BoxPart.Leader => Caret(_doc.Record.Leader),
                BoxPart.ControlData => Caret(_doc.Record.Fields[spec.FieldIndex].ControlData ?? ""),
                BoxPart.Ind => IndicatorsOf(_doc.Record.Fields[spec.FieldIndex]),
                BoxPart.Tag => _doc.Record.Fields[spec.FieldIndex].Tag,
                BoxPart.Code when spec.SubfieldIndex >= 0 =>
                    _doc.Record.Fields[spec.FieldIndex].Subfields[spec.SubfieldIndex].Code.ToString(),
                _ => box.Text,
            };
        }
        catch (ArgumentOutOfRangeException) { /* field/subfield gone; a rebuild will fix it */ }
        finally { _suspendCommit = prev; }
    }

    private static string IndicatorsOf(MarcField f) =>
        f.IsControl ? "" : new(new[] { f.Ind1 == ' ' ? '_' : f.Ind1, f.Ind2 == ' ' ? '_' : f.Ind2 });

    private static string Caret(string s) => s.Replace(' ', '^');
}
