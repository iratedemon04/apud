using Apud.Data;
using Marc.Core.FixedFields;

namespace Apud.App;

/// <summary>
/// Application shell, Aleph-fashion (docs/ALEPH-WORKFLOW.md, corrected by the
/// user 2026-07-28): the MAIN area hosts either the Search screen (form +
/// results + session history) or the Record screen (read-only viewer); the LEFT
/// sidebar is the open-records collection — clicking a search result adds the
/// record there, any number of them, from either base. The sidebar is the only
/// way records are held open; nothing lists a whole base unless explicitly asked
/// (List All). Editing arrives in Module 6.
/// </summary>
public sealed class MainForm : Form
{
    private readonly MenuStrip _menu;
    private readonly StatusStrip _messageBar;
    private readonly ToolStripStatusLabel _messageLabel;
    private readonly ToolStripMenuItem _bibItem;
    private readonly ToolStripMenuItem _autItem;

    private readonly ListView _openList;          // sidebar: open records (any base)
    private readonly Button _searchViewButton;
    private readonly Button _recordViewButton;
    private readonly Panel _searchView;
    private readonly Panel _recordView;

    private readonly ComboBox _searchBase;
    private readonly ComboBox _searchScope;
    private readonly TextBox _searchBox;
    private readonly ListView _resultsList;
    private readonly ListView _historyList;
    private readonly SearchHistory _history = new();

    private readonly Label _recordHeader;
    private readonly DataGridView _viewer;

    private static readonly (string Label, SearchScope Scope)[] Scopes =
    {
        ("All fields", SearchScope.All),
        ("Title", SearchScope.Title),
        ("Author", SearchScope.Author),
        ("Subjects", SearchScope.Subjects),
        ("Control No.", SearchScope.ControlNumber),
    };

    private readonly CommandRegistry _commands = new();
    private readonly Keymap _keymap;

    private ApudDatabase? _db;
    private RecordRepository? _repo;
    private bool _syncingBase;
    private EditorDocument? _currentDoc; // the record showing in the editor
    private bool _rendering;             // grid is being rebuilt; ignore its edit events

    private string CurrentBase => _searchBase.SelectedIndex == 1 ? "AUT" : "BIB";

    private CommandContext ActiveContext =>
        _recordView.Visible ? CommandContext.Editor : CommandContext.Search;

    public MainForm()
    {
        Text = "Apud";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(950, 620);

        // ----- command table (Module 6 step 1) -----
        // Menus render from this table and keymap.json binds against it.
        // Catalogue commands are menu-only (§6.2: record commands own the keyboard).
        _commands.Add(new Command { Id = "catalogue.new", Name = "&New Catalogue...", Execute = NewCatalog });
        _commands.Add(new Command { Id = "catalogue.open", Name = "&Open Catalogue...", DefaultKey = "Ctrl+O", Execute = OpenCatalogDialog });
        _commands.Add(new Command { Id = "catalogue.import-folder", Name = "&Import Folder...", Execute = ImportFolder });
        _commands.Add(new Command { Id = "catalogue.export-base", Name = "Export &Base...", Execute = ExportBase });
        _commands.Add(new Command { Id = "catalogue.export-selected", Name = "Export &Selected...", Execute = ExportSelected });
        _commands.Add(new Command { Id = "app.exit", Name = "E&xit", DefaultKey = "Alt+F4", Execute = Close });
        _commands.Add(new Command { Id = "base.bib", Name = "&BIB — Bibliographic", Execute = () => SetBase("BIB") });
        _commands.Add(new Command { Id = "base.aut", Name = "&AUT — Authority", Execute = () => SetBase("AUT") });
        _commands.Add(new Command { Id = "search.focus", Name = "&Search", DefaultKey = "F2", Execute = ShowSearchView });
        _commands.Add(new Command { Id = "help.about", Name = "&About Apud", Execute = ShowAbout });
        // Editor commands (Module 6 steps 3-7). §6.2: record commands own the keyboard.
        _commands.Add(new Command { Id = "record.new", Name = "&New Record / Copy", DefaultKey = "Ctrl+N", Execute = NewRecord });
        _commands.Add(new Command { Id = "record.save-draft", Name = "&Save Draft", Context = CommandContext.Editor, DefaultKey = "Ctrl+S", Execute = SaveDraft });
        _commands.Add(new Command { Id = "record.save-template", Name = "Save as &Template...", Context = CommandContext.Editor, DefaultKey = "Ctrl+T", Execute = SaveTemplate });
        _commands.Add(new Command { Id = "record.undo", Name = "&Undo", Context = CommandContext.Editor, DefaultKey = "Ctrl+Z", Execute = UndoEdit });
        _commands.Add(new Command { Id = "record.redo", Name = "&Redo", Context = CommandContext.Editor, DefaultKey = "Ctrl+Y", Execute = RedoEdit });
        _commands.Add(new Command { Id = "field.new", Name = "New &Field", Context = CommandContext.Editor, DefaultKey = "F6", Execute = NewField });
        _commands.Add(new Command { Id = "subfield.new", Name = "New Su&bfield", Context = CommandContext.Editor, DefaultKey = "F7", Execute = NewSubfield });
        _commands.Add(new Command { Id = "field.delete", Name = "&Delete Field", Context = CommandContext.Editor, DefaultKey = "Ctrl+F5", Execute = DeleteCurrentField });
        _commands.Add(new Command { Id = "subfield.delete", Name = "Delete Subf&ield", Context = CommandContext.Editor, DefaultKey = "Ctrl+F7", Execute = DeleteCurrentSubfield });
        // Stubs wired, not built — keys rebindable from day one (step 7).
        _commands.Add(new Command { Id = "field.fixed-edit", Name = "Edit Fixed Field by &Position...", Context = CommandContext.Editor, DefaultKey = "Ctrl+F3", Execute = EditFixedField });
        _commands.Add(new Command { Id = "field.validate", Name = "&Validate Field / Browse Headings", Context = CommandContext.Editor, DefaultKey = "Ctrl+F4", Execute = () => SetMessage("Field validation and heading browse arrive in Module 8.") });
        _commands.Add(new Command { Id = "record.validate", Name = "Validate &Record", Context = CommandContext.Editor, DefaultKey = "Ctrl+W", Execute = () => SetMessage("Validation arrives in Module 9.") });
        _commands.Add(new Command { Id = "record.push", Name = "Validate && &Push", Context = CommandContext.Editor, DefaultKey = "Ctrl+L", Execute = () => SetMessage("Validate + push arrives in Module 9.") });

        _keymap = Keymap.LoadFile(_commands, Path.Combine(AppContext.BaseDirectory, Keymap.FileName));

        // ----- menu (rendered from the command table) -----
        _menu = new MenuStrip();

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(MenuItem("catalogue.new"));
        file.DropDownItems.Add(MenuItem("catalogue.open"));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(MenuItem("catalogue.import-folder"));
        file.DropDownItems.Add(MenuItem("catalogue.export-base"));
        file.DropDownItems.Add(MenuItem("catalogue.export-selected"));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(MenuItem("app.exit"));

        var @base = new ToolStripMenuItem("&Base");
        _bibItem = MenuItem("base.bib");
        _bibItem.Checked = true;
        _autItem = MenuItem("base.aut");
        @base.DropDownItems.Add(_bibItem);
        @base.DropDownItems.Add(_autItem);

        var record = new ToolStripMenuItem("&Record");
        record.DropDownItems.Add(MenuItem("record.new"));
        record.DropDownItems.Add(MenuItem("record.save-draft"));
        record.DropDownItems.Add(MenuItem("record.save-template"));
        record.DropDownItems.Add(new ToolStripSeparator());
        record.DropDownItems.Add(MenuItem("record.undo"));
        record.DropDownItems.Add(MenuItem("record.redo"));
        record.DropDownItems.Add(new ToolStripSeparator());
        record.DropDownItems.Add(MenuItem("field.new"));
        record.DropDownItems.Add(MenuItem("subfield.new"));
        record.DropDownItems.Add(MenuItem("field.delete"));
        record.DropDownItems.Add(MenuItem("subfield.delete"));
        record.DropDownItems.Add(new ToolStripSeparator());
        record.DropDownItems.Add(MenuItem("field.fixed-edit"));
        record.DropDownItems.Add(MenuItem("field.validate"));
        record.DropDownItems.Add(MenuItem("record.validate"));
        record.DropDownItems.Add(MenuItem("record.push"));

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(MenuItem("help.about"));

        _menu.Items.Add(file);
        _menu.Items.Add(@base);
        _menu.Items.Add(record);
        _menu.Items.Add(help);

        // ----- sidebar: open records -----
        _openList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = true,
            HideSelection = false,
        };
        _openList.Columns.Add("Base", 45);
        _openList.Columns.Add("001", 60);
        _openList.Columns.Add("Title", 170);
        _openList.SelectedIndexChanged += (_, _) => ShowSelectedOpenRecord();
        _openList.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) RemoveSelectedOpenRecords();
        };
        var openMenu = new ContextMenuStrip();
        openMenu.Items.Add("Remove", null, (_, _) => RemoveSelectedOpenRecords());
        openMenu.Items.Add("Remove All", null, (_, _) => RemoveAllOpenRecords());
        _openList.ContextMenuStrip = openMenu;

        var openLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            Text = "Records",
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(4, 0, 0, 0),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
        };

        // ----- main area: view switch -----
        _searchViewButton = new Button { Text = "Search", Width = 80, FlatStyle = FlatStyle.Flat };
        _recordViewButton = new Button { Text = "Record", Width = 80, FlatStyle = FlatStyle.Flat };
        _searchViewButton.Click += (_, _) => ShowSearchView();
        _recordViewButton.Click += (_, _) => ShowRecordView();
        var switchStrip = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 30,
            Padding = new Padding(2),
        };
        switchStrip.Controls.Add(_searchViewButton);
        switchStrip.Controls.Add(_recordViewButton);

        // ----- search view -----
        _searchBase = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };
        _searchBase.Items.Add("BIB");
        _searchBase.Items.Add("AUT");
        _searchBase.SelectedIndex = 0;
        _searchBase.SelectedIndexChanged += (_, _) => SetBase(CurrentBase);

        _searchScope = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
        foreach (var (label, _) in Scopes) _searchScope.Items.Add(label);
        _searchScope.SelectedIndex = 0;

        _searchBox = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right };
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { RunSearch(); e.SuppressKeyPress = true; }
        };
        var searchButton = new Button { Text = "Search", Width = 60 };
        searchButton.Click += (_, _) => RunSearch();
        var listAllButton = new Button { Text = "List All", Width = 60 };
        listAllButton.Click += (_, _) => ListAll();

        var searchForm = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 32,
            ColumnCount = 5,
            Padding = new Padding(2),
        };
        searchForm.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        searchForm.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        searchForm.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        searchForm.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        searchForm.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        searchForm.Controls.Add(_searchBase, 0, 0);
        searchForm.Controls.Add(_searchScope, 1, 0);
        searchForm.Controls.Add(_searchBox, 2, 0);
        searchForm.Controls.Add(searchButton, 3, 0);
        searchForm.Controls.Add(listAllButton, 4, 0);

        _resultsList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
        };
        _resultsList.Columns.Add("001", 70);
        _resultsList.Columns.Add("Title", 300);
        _resultsList.Columns.Add("Author", 180);
        _resultsList.Columns.Add("Year", 50);
        _resultsList.Columns.Add("Status", 60);
        _resultsList.DoubleClick += (_, _) => OpenSelectedResult();
        _resultsList.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { OpenSelectedResult(); e.SuppressKeyPress = true; }
        };

        _historyList = new ListView
        {
            Dock = DockStyle.Bottom,
            Height = 140,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
        };
        _historyList.Columns.Add("Search history", 300);
        _historyList.Columns.Add("Base", 50);
        _historyList.Columns.Add("Scope", 80);
        _historyList.Columns.Add("Hits", 50, HorizontalAlignment.Right);
        _historyList.DoubleClick += (_, _) => RerunFromHistory();

        _searchView = new Panel { Dock = DockStyle.Fill };
        _searchView.Controls.Add(_resultsList);
        _searchView.Controls.Add(searchForm);
        _searchView.Controls.Add(_historyList);

        // ----- record view -----
        _recordHeader = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DarkRed,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            Padding = new Padding(6, 0, 0, 0),
            AutoEllipsis = true,
        };

        // Module 5.9: the Aleph editor look (docs/inspiration/README.md) — a
        // white page of dense text, not a form. No grid lines, no boxes, rows
        // tight like a text editor; red underlined tags and subfield codes,
        // bold black data text.
        // Module 6: the same page of text is now the editor — in-place edits on
        // the cells themselves, never separate input boxes (user decision).
        _viewer = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            MultiSelect = false,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.None,
            ColumnHeadersVisible = false,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
        };
        _viewer.CellEndEdit += ViewerCellEndEdit;
        _viewer.EditingControlShowing += ViewerEditingControlShowing;
        _viewer.DefaultCellStyle.Padding = new Padding(0);
        _viewer.DefaultCellStyle.SelectionBackColor = Color.FromArgb(225, 238, 250);
        _viewer.DefaultCellStyle.SelectionForeColor = Color.Black;
        _viewer.RowTemplate.Height = 17;

        var mono = new Font("Consolas", 9.75f);
        _viewer.Columns.Add(NewColumn("name", 140, italic: true, color: Color.Maroon,
            font: new Font("Segoe UI", 8.25f), readOnly: true));
        _viewer.Columns.Add(NewColumn("tag", 42, font: mono, bold: true, underline: true));
        _viewer.Columns.Add(NewColumn("ind", 34, font: mono));
        _viewer.Columns.Add(NewColumn("code", 26, font: mono, bold: true, underline: true));
        var value = NewColumn("value", 200, font: mono, bold: true);
        value.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        value.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _viewer.Columns.Add(value);

        _recordView = new Panel { Dock = DockStyle.Fill, Visible = false };
        _recordView.Controls.Add(_viewer);
        _recordView.Controls.Add(_recordHeader);

        // ----- composition -----
        var rightPanel = new Panel { Dock = DockStyle.Fill };
        rightPanel.Controls.Add(_searchView);
        rightPanel.Controls.Add(_recordView);
        rightPanel.Controls.Add(switchStrip);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 285,
            FixedPanel = FixedPanel.Panel1,
        };
        split.Panel1.Controls.Add(_openList);
        split.Panel1.Controls.Add(openLabel);
        split.Panel2.Controls.Add(rightPanel);

        _messageLabel = new ToolStripStatusLabel("Ready.") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _messageBar = new StatusStrip();
        _messageBar.Items.Add(_messageLabel);

        Controls.Add(split);
        Controls.Add(_messageBar);
        Controls.Add(_menu);
        MainMenuStrip = _menu;

        ShowSearchView();

        // Deliberately dumb (user decision, 2026-07-28): the app never reconnects
        // to a previous catalogue, remembers nothing between sessions, and creates
        // nothing on its own — cataloguers must consciously choose the database
        // every session, exactly like Aleph's Connect to...
        var configReports = new List<string>();
        if (TagNames.LoadFile(Path.Combine(AppContext.BaseDirectory, TagNames.FileName)) is string tagNamesReport)
            configReports.Add(tagNamesReport);
        configReports.AddRange(_keymap.Diagnostics);
        Load += (_, _) => SetMessage(configReports.Count > 0
            ? string.Join("  |  ", configReports)
            : "No catalogue open — File → New Catalogue or Open Catalogue.");
        FormClosed += (_, _) => _db?.Dispose();
    }

    /// <summary>Menu item for a command: text, handler and shortcut display all
    /// come from the one table entry, so they can never disagree.</summary>
    private ToolStripMenuItem MenuItem(string commandId)
    {
        var cmd = _commands.Find(commandId)
            ?? throw new InvalidOperationException($"Menu references unregistered command {commandId}.");
        var item = new ToolStripMenuItem(cmd.Name, null, (_, _) => cmd.Execute());
        if (_keymap.BindingFor(commandId) is string chord)
            item.ShortcutKeyDisplayString = chord;
        return item;
    }

    private void ShowAbout() =>
        MessageBox.Show(this,
            $"Apud {Application.ProductVersion}\nMARC21 original cataloguing.",
            "About Apud", MessageBoxButtons.OK, MessageBoxIcon.Information);

    /// <summary>Keymap dispatch through the framework's shortcut hook. Keys land
    /// here before the focused control; chords the keymap doesn't claim follow
    /// the normal WinForms path.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (ShouldDispatch(keyData) && _keymap.Lookup(keyData, ActiveContext) is string id)
        {
            _commands.Find(id)!.Execute();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>While the cursor is in a text control, only modified chords and
    /// F-keys dispatch — a plain letter, digit, Del or Enter is typing, not a
    /// command (documented in keymap.json's header).</summary>
    private bool ShouldDispatch(Keys keyData)
    {
        if ((keyData & (Keys.Control | Keys.Alt)) != 0) return true;
        var code = keyData & Keys.KeyCode;
        if (code is >= Keys.F1 and <= Keys.F24) return true;
        return FocusedControl() is not (TextBoxBase or ComboBox)
               && !_viewer.IsCurrentCellInEditMode; // a grid cell being typed in is a text box too
    }

    private Control? FocusedControl()
    {
        Control? c = ActiveControl;
        while (c is ContainerControl container && container.ActiveControl != null)
            c = container.ActiveControl;
        return c;
    }

    private static DataGridViewTextBoxColumn NewColumn(
        string name, int width, bool italic = false, bool bold = false, bool underline = false,
        Color? color = null, Font? font = null, bool readOnly = false)
    {
        var col = new DataGridViewTextBoxColumn { Name = name, Width = width, ReadOnly = readOnly };
        var style = col.DefaultCellStyle;
        if (color is Color c)
        {
            style.ForeColor = c;
            style.SelectionForeColor = c; // colored columns keep their color when selected
        }
        Font baseFont = font ?? new Font("Segoe UI", 9f);
        var flags = (italic ? FontStyle.Italic : FontStyle.Regular)
                  | (bold ? FontStyle.Bold : FontStyle.Regular)
                  | (underline ? FontStyle.Underline : FontStyle.Regular);
        style.Font = new Font(baseFont, flags);
        return col;
    }

    // ---------- view switching ----------

    private void ShowSearchView()
    {
        _searchView.Visible = true;
        _recordView.Visible = false;
        _searchViewButton.Font = new Font(_searchViewButton.Font, FontStyle.Bold);
        _recordViewButton.Font = new Font(_recordViewButton.Font, FontStyle.Regular);
        _searchBox.Focus();
    }

    private void ShowRecordView()
    {
        _searchView.Visible = false;
        _recordView.Visible = true;
        _recordViewButton.Font = new Font(_recordViewButton.Font, FontStyle.Bold);
        _searchViewButton.Font = new Font(_searchViewButton.Font, FontStyle.Regular);
    }

    // ---------- catalogue lifecycle ----------

    /// <summary>Documents\Apud if the user has made it, else Documents. Never created by us.</summary>
    private static string SuggestedFolder()
    {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string apud = Path.Combine(documents, "Apud");
        return Directory.Exists(apud) ? apud : documents;
    }

    private void NewCatalog()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "New Catalogue",
            InitialDirectory = SuggestedFolder(),
            FileName = "catalog.db",
            Filter = "Apud catalogue (*.db)|*.db",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        // The dialog already asked about overwriting; a stale file must not be
        // silently opened as an existing catalogue.
        if (File.Exists(dialog.FileName)) File.Delete(dialog.FileName);
        OpenCatalog(dialog.FileName, createNew: true);
    }

    private void OpenCatalogDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Open Catalogue",
            InitialDirectory = SuggestedFolder(),
            Filter = "Apud catalogue (*.db)|*.db|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            OpenCatalog(dialog.FileName);
    }

    private void OpenCatalog(string path, bool createNew = false)
    {
        // Opening never creates: SQLite would happily conjure an empty database
        // at a mistyped path, and this software creates nothing on its own.
        if (!createNew && !File.Exists(path))
        {
            MessageBox.Show(this, $"There is no catalogue at:\n{path}", "Cannot open catalogue",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        ApudDatabase db;
        try
        {
            db = ApudDatabase.Open(path);
        }
        catch (Exception e) when (e is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            MessageBox.Show(this, e.Message, "Cannot open catalogue",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _db?.Dispose();
        _db = db;
        _repo = new RecordRepository(db);

        // A different catalogue means different record ids: everything on
        // screen belonged to the old one.
        _openList.Items.Clear();
        _resultsList.Items.Clear();
        _historyList.Items.Clear();
        ClearViewer();
        ShowSearchView();

        Text = $"Apud — {path}";
        SetMessage($"Catalogue open — BIB: {_repo.List("BIB").Count}, AUT: {_repo.List("AUT").Count} record(s).");
    }

    // ---------- base ----------

    private void SetBase(string @base)
    {
        if (_syncingBase) return;
        _syncingBase = true;
        _bibItem.Checked = @base == "BIB";
        _autItem.Checked = @base == "AUT";
        _searchBase.SelectedIndex = @base == "AUT" ? 1 : 0;
        _syncingBase = false;
    }

    // ---------- search ----------

    private void RunSearch()
    {
        if (_repo is null) { SetMessage("Open a catalogue first."); return; }
        string query = _searchBox.Text.Trim();
        if (query.Length == 0) return;

        var scope = Scopes[_searchScope.SelectedIndex].Scope;
        var ids = _repo.Search(CurrentBase, query, scope);

        _history.Add(new SearchHistoryEntry(query, scope, CurrentBase, ids.Count));
        RefreshHistoryList();

        var byId = _repo.List(CurrentBase).ToDictionary(s => s.Id);
        FillResults(ids.Select(id => byId.GetValueOrDefault(id)).Where(s => s != null)!);
        SetMessage($"{ids.Count} hit(s) for \"{query}\" in {CurrentBase}.");
    }

    /// <summary>The explicit whole-base listing (control-number order).</summary>
    private void ListAll()
    {
        if (_repo is null) { SetMessage("Open a catalogue first."); return; }
        var list = _repo.List(CurrentBase);
        FillResults(list);
        SetMessage($"{CurrentBase}: {list.Count} record(s).");
    }

    private void FillResults(IEnumerable<RecordSummary> summaries)
    {
        _resultsList.BeginUpdate();
        _resultsList.Items.Clear();
        foreach (var s in summaries)
        {
            var item = new ListViewItem(s.ControlNumber ?? "");
            item.SubItems.Add(s.Title);
            item.SubItems.Add(s.Author);
            item.SubItems.Add(s.Year);
            item.SubItems.Add(s.Status == RecordStatus.Pushed ? "pushed" : "draft");
            item.Tag = s;
            _resultsList.Items.Add(item);
        }
        _resultsList.EndUpdate();
    }

    private void RefreshHistoryList()
    {
        _historyList.BeginUpdate();
        _historyList.Items.Clear();
        foreach (var e in _history.Entries)
        {
            var item = new ListViewItem(e.Query);
            item.SubItems.Add(e.Base);
            item.SubItems.Add(Scopes.First(s => s.Scope == e.Scope).Label);
            item.SubItems.Add(e.Hits.ToString());
            item.Tag = e;
            _historyList.Items.Add(item);
        }
        _historyList.EndUpdate();
    }

    private void RerunFromHistory()
    {
        if (_historyList.SelectedItems.Count == 0) return;
        var e = (SearchHistoryEntry)_historyList.SelectedItems[0].Tag!;
        SetBase(e.Base);
        _searchBox.Text = e.Query;
        _searchScope.SelectedIndex = Array.FindIndex(Scopes, s => s.Scope == e.Scope);
        RunSearch();
    }

    // ---------- open records (sidebar) ----------

    private void OpenSelectedResult()
    {
        if (_repo is null || _resultsList.SelectedItems.Count == 0) return;
        var s = (RecordSummary)_resultsList.SelectedItems[0].Tag!;

        // Already open? Select it instead of duplicating.
        foreach (ListViewItem existing in _openList.Items)
        {
            if (existing.Tag is EditorDocument d && d.Stored.Id == s.Id)
            {
                existing.Selected = true;
                ShowRecordView();
                return;
            }
        }

        var stored = _repo.Load(s.Id);
        if (stored is null) return;
        AddToSidebar(new EditorDocument(stored));
    }

    /// <summary>Adds an open record to the sidebar and selects it (which shows
    /// it). The document lives on the list item; edits persist in memory while
    /// switching records, until saved or removed.</summary>
    private void AddToSidebar(EditorDocument doc)
    {
        var item = new ListViewItem(doc.Stored.Base);
        item.SubItems.Add(doc.Record.ControlNumber ?? "");
        item.SubItems.Add(TitleOf(doc.Record));
        item.Tag = doc;
        _openList.Items.Add(item);
        _openList.SelectedItems.Clear();
        item.Selected = true; // triggers ShowSelectedOpenRecord → record view
    }

    private void ShowSelectedOpenRecord()
    {
        if (_openList.SelectedItems.Count == 0) return;
        _currentDoc = _openList.SelectedItems[0].Tag as EditorDocument;
        RenderRecord();
        ShowRecordView();
    }

    private void RemoveSelectedOpenRecords() =>
        RemoveOpenRecords(_openList.SelectedItems.Cast<ListViewItem>().ToList());

    private void RemoveAllOpenRecords() =>
        RemoveOpenRecords(_openList.Items.Cast<ListViewItem>().ToList());

    private void RemoveOpenRecords(List<ListViewItem> items)
    {
        int dirty = items.Count(i => i.Tag is EditorDocument { Dirty: true });
        if (dirty > 0 && MessageBox.Show(this,
                $"{dirty} record(s) have unsaved changes. Remove anyway?",
                "Remove Records", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        foreach (var item in items)
        {
            if (ReferenceEquals(item.Tag, _currentDoc)) _currentDoc = null;
            _openList.Items.Remove(item);
        }
        if (_currentDoc is null) ClearViewer();
    }

    private void ClearViewer()
    {
        _currentDoc = null;
        _recordHeader.Text = "";
        _viewer.Rows.Clear();
    }

    private static string TitleOf(Marc.Core.MarcRecord record)
    {
        foreach (var tag in new[] { "245", "100", "110", "111", "130", "150", "151" })
        {
            var f = record.FieldsWithTag(tag).FirstOrDefault();
            if (f?.Subfields.Count > 0) return f.Subfields[0].Value;
        }
        return "";
    }

    // ---------- editor (Module 6 steps 3-6) ----------

    /// <summary>Redraws the grid and header from the current document. Cheap
    /// (a record is a few dozen rows), so structural edits just call it again.</summary>
    private void RenderRecord()
    {
        if (_currentDoc is null) { _recordHeader.Text = ""; _viewer.Rows.Clear(); return; }
        var doc = _currentDoc;

        _rendering = true;
        UpdateHeader();
        _viewer.Rows.Clear();
        foreach (var row in RecordDisplay.Build(doc.Record))
        {
            int i = _viewer.Rows.Add(row.FieldName, row.Tag, row.Indicators, row.Code, row.Value);
            var gridRow = _viewer.Rows[i];
            gridRow.Tag = row;
            ApplyCellEditability(gridRow, row, doc);
        }
        _viewer.ClearSelection();
        _rendering = false;
    }

    private void UpdateHeader()
    {
        if (_currentDoc is null) return;
        _recordHeader.Text =
            RecordDisplay.HeaderText(_currentDoc.Stored.Base, _currentDoc.Record.ControlNumber, _currentDoc.Record)
            + (_currentDoc.Dirty ? "  *" : "");
    }

    /// <summary>Which cells accept typing, per row shape: the name column never;
    /// tag only on a field's first row; indicators only on data fields; code
    /// only where a subfield can live; the leader's value is its whole row.</summary>
    private static void ApplyCellEditability(DataGridViewRow gridRow, DisplayRow row, EditorDocument doc)
    {
        bool leader = row.FieldIndex < 0;
        bool control = !leader && doc.Record.Fields[row.FieldIndex].IsControl;
        bool continuation = !leader && row.Tag.Length == 0; // second+ subfield rows

        gridRow.Cells["tag"].ReadOnly = leader || continuation;
        gridRow.Cells["ind"].ReadOnly = leader || control || continuation;
        gridRow.Cells["code"].ReadOnly = leader || control;
    }

    /// <summary>An edited cell is committed into the document. Structural
    /// outcomes (retag, subfield created on an empty field) re-render — deferred
    /// with BeginInvoke because rebuilding rows inside CellEndEdit re-enters the
    /// grid. Plain value edits only normalize the cell text in place.</summary>
    private void ViewerCellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        if (_rendering || _currentDoc is null) return;
        var doc = _currentDoc;
        var gridRow = _viewer.Rows[e.RowIndex];
        if (gridRow.Tag is not DisplayRow row) return;
        var cell = gridRow.Cells[e.ColumnIndex];
        string text = cell.Value?.ToString() ?? "";
        string? error = null;
        bool structural = false;

        switch (_viewer.Columns[e.ColumnIndex].Name)
        {
            case "value" when row.FieldIndex < 0:
                error = doc.SetLeader(text);
                cell.Value = Caret(doc.Record.Leader); // on error this restores the old text
                break;
            case "value" when doc.Record.Fields[row.FieldIndex].IsControl:
                doc.SetControlData(row.FieldIndex, text);
                cell.Value = Caret(doc.Record.Fields[row.FieldIndex].ControlData ?? "");
                break;
            case "value":
                structural = row.SubfieldIndex < 0 && text.Length > 0; // typing creates the subfield
                doc.SetSubfieldValue(row.FieldIndex, row.SubfieldIndex, text);
                break;
            case "tag":
                error = doc.SetTag(row.FieldIndex, text);
                if (error is null) structural = true;
                else cell.Value = row.Tag; // refused — put the old tag back
                break;
            case "ind":
                doc.SetIndicators(row.FieldIndex, text);
                var f = doc.Record.Fields[row.FieldIndex];
                cell.Value = new string(new[] { f.Ind1 == ' ' ? '_' : f.Ind1, f.Ind2 == ' ' ? '_' : f.Ind2 });
                break;
            case "code":
                structural = row.SubfieldIndex < 0 && text.Length > 0;
                doc.SetSubfieldCode(row.FieldIndex, row.SubfieldIndex, text);
                if (!structural && row.SubfieldIndex >= 0)
                    cell.Value = doc.Record.Fields[row.FieldIndex].Subfields[row.SubfieldIndex].Code.ToString();
                break;
        }

        if (error != null) SetMessage(error);
        UpdateHeader();
        if (structural) BeginInvoke(RenderRecord);
    }

    /// <summary>Caps the editing box by column to the fixed MARC widths: a tag
    /// is 3 characters, indicators 2, a subfield code 1. Any characters are
    /// allowed within that width (dumb editor) — only the length is fixed. The
    /// grid reuses one editing control across cells, so this is set every time.</summary>
    private void ViewerEditingControlShowing(object? sender, DataGridViewEditingControlShowingEventArgs e)
    {
        if (e.Control is not TextBox tb) return;
        tb.MaxLength = _viewer.CurrentCell?.OwningColumn.Name switch
        {
            "tag" => 3,
            "ind" => 2,
            "code" => 1,
            _ => 0, // 0 = unlimited (value and control-data cells)
        };
    }

    private static string Caret(string s) => s.Replace(' ', '^');

    private void UndoEdit()
    {
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _viewer.EndEdit(); // commit any in-progress cell edit so Ctrl+Z reverts it too
        if (!_currentDoc.Undo()) { SetMessage("Nothing to undo."); return; }
        RenderRecord();
        UpdateSidebarItem(_currentDoc);
        SetMessage(_currentDoc.CanUndo ? "Undo." : "Undo — nothing more to undo.");
    }

    private void RedoEdit()
    {
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _viewer.EndEdit();
        if (!_currentDoc.Redo()) { SetMessage("Nothing to redo."); return; }
        RenderRecord();
        UpdateSidebarItem(_currentDoc);
        SetMessage("Redo.");
    }

    /// <summary>The field/subfield the cursor is on, in model indices.</summary>
    private (int FieldIndex, int SubfieldIndex)? CurrentRef() =>
        _viewer.CurrentCell?.OwningRow.Tag is DisplayRow row
            ? (row.FieldIndex, row.SubfieldIndex)
            : null;

    /// <summary>Puts the cursor on a field/subfield after a structural change.</summary>
    private void SelectCell(int fieldIndex, int subfieldIndex, string column)
    {
        foreach (DataGridViewRow gridRow in _viewer.Rows)
        {
            if (gridRow.Tag is DisplayRow r && r.FieldIndex == fieldIndex && r.SubfieldIndex == subfieldIndex)
            {
                _viewer.CurrentCell = gridRow.Cells[column];
                return;
            }
        }
    }

    private void NewField()
    {
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _viewer.EndEdit();
        int after = CurrentRef()?.FieldIndex ?? _currentDoc.Record.Fields.Count - 1;
        int at = _currentDoc.InsertBlankFieldAfter(after);
        RenderRecord();
        SelectCell(at, -1, "tag");
        SetMessage("New field — type its tag.");
    }

    private void NewSubfield()
    {
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _viewer.EndEdit();
        if (CurrentRef() is not { } at || at.FieldIndex < 0)
        {
            SetMessage("Stand in a field first.");
            return;
        }
        var (index, error) = _currentDoc.InsertSubfieldAfter(at.FieldIndex, at.SubfieldIndex);
        if (error != null) { SetMessage(error); return; }
        RenderRecord();
        SelectCell(at.FieldIndex, index, "code");
    }

    private void DeleteCurrentField()
    {
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _viewer.EndEdit();
        if (CurrentRef() is not { } at || at.FieldIndex < 0)
        {
            SetMessage("Stand in a field first (the leader cannot be deleted).");
            return;
        }
        _currentDoc.DeleteField(at.FieldIndex);
        RenderRecord();
        SelectCell(Math.Min(at.FieldIndex, _currentDoc.Record.Fields.Count - 1), -1, "tag");
    }

    private void DeleteCurrentSubfield()
    {
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _viewer.EndEdit();
        if (CurrentRef() is not { } at || at.FieldIndex < 0 || at.SubfieldIndex < 0)
        {
            SetMessage("Stand in a subfield first.");
            return;
        }
        _currentDoc.DeleteSubfield(at.FieldIndex, at.SubfieldIndex);
        RenderRecord();
        int left = _currentDoc.Record.Fields[at.FieldIndex].Subfields.Count;
        SelectCell(at.FieldIndex, left == 0 ? -1 : Math.Min(at.SubfieldIndex, left - 1), "code");
    }

    // ---------- fixed-field position editor (Module 7) ----------

    /// <summary>Ctrl+F3: edit the leader or an 008 byte-by-byte through the
    /// position dialog. The target is whatever the caret stands on; the write
    /// goes back through EditorDocument (SetLeader/SetControlData) so it lands on
    /// the undo stack like any other edit. §6.2: this is the fixed-field editor
    /// the user asked for — "on LDR/008, open its data by position."</summary>
    private void EditFixedField()
    {
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _viewer.EndEdit();
        if (CurrentRef() is not { } at) { SetMessage("Stand on the leader or an 008 field first."); return; }
        var doc = _currentDoc;

        FixedFieldLayout? layout;
        string current, title;
        Action<string> writeBack;

        if (at.FieldIndex < 0) // leader row
        {
            layout = FixedFieldLayouts.Leader(doc.Record.Leader);
            current = doc.Record.Leader;
            title = "Leader — edit by position";
            writeBack = text => { if (doc.SetLeader(text) is string err) SetMessage(err); };
        }
        else
        {
            var field = doc.Record.Fields[at.FieldIndex];
            if (field.Tag != "008")
            {
                SetMessage(field.IsControl
                    ? $"Ctrl+F3 maps the leader and 008. {field.Tag} is a plain control field — type its value directly."
                    : "Ctrl+F3 edits fixed fields — stand on the leader or the 008.");
                return;
            }
            layout = FixedFieldLayouts.For008(doc.Record.Leader);
            current = field.ControlData ?? "";
            title = $"008 — {FixedFieldLayouts.Material008(doc.Record.Leader)} — edit by position";
            int fieldIndex = at.FieldIndex;
            writeBack = text => doc.SetControlData(fieldIndex, text);
        }

        if (layout is null) { SetMessage("No fixed-field layout is available for this record type."); return; }

        using var form = new FixedFieldForm(title, layout, current);
        if (form.ShowDialog(this) != DialogResult.OK) return;

        writeBack(form.Result!);
        RenderRecord();
        UpdateSidebarItem(doc);
        SelectCell(at.FieldIndex, -1, "value");
        SetMessage($"{(at.FieldIndex < 0 ? "Leader" : "008")} updated by position.");
    }

    // ---------- new record / save (Module 6 steps 4-6) ----------

    private static string TemplatesFolder => Path.Combine(AppContext.BaseDirectory, "templates");

    /// <summary>Ctrl+N, context-sensitive per §6.2: standing in a record copies
    /// it as a new draft (001 cleared, sequence fills at push); otherwise a new
    /// record from a template.</summary>
    private void NewRecord()
    {
        if (_repo is null) { SetMessage("Open a catalogue first."); return; }

        if (_recordView.Visible && _currentDoc is not null)
        {
            _viewer.EndEdit();
            var copy = EditorDocument.CopyWithout001(_currentDoc.Record);
            AddToSidebar(new EditorDocument(new StoredRecord(_currentDoc.Stored.Base, copy), dirty: true));
            SetMessage("Copied as a new draft — 001 will be assigned at push.");
            return;
        }

        var files = Directory.Exists(TemplatesFolder)
            ? Directory.GetFiles(TemplatesFolder, "*.mrk")
            : Array.Empty<string>();
        if (files.Length == 0)
        {
            SetMessage(@"No templates found — put .mrk skeletons in templates\ beside Apud.exe.");
            return;
        }

        using var picker = new TemplatePickerForm(files);
        if (picker.ShowDialog(this) != DialogResult.OK || picker.SelectedPath is null) return;

        var read = Marc.Core.Mrk.MrkReader.Read(File.ReadAllText(picker.SelectedPath));
        if (read.Records.Count == 0)
        {
            SetMessage($"{Path.GetFileName(picker.SelectedPath)} contains no record.");
            return;
        }

        // The template is a skeleton: take its first record, drop any 001
        // (fresh records earn theirs at push), route by LDR/06 like import.
        var record = EditorDocument.CopyWithout001(read.Records[0]);
        string @base = record.Kind == Marc.Core.RecordKind.Authority ? "AUT" : "BIB";
        AddToSidebar(new EditorDocument(new StoredRecord(@base, record), dirty: true));
        SetMessage($"New {@base} record from {Path.GetFileNameWithoutExtension(picker.SelectedPath)}.");
    }

    private void SaveDraft()
    {
        if (_repo is null) { SetMessage("Open a catalogue first."); return; }
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _viewer.EndEdit();

        try
        {
            _repo.SaveDraft(_currentDoc.Stored);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            SetMessage($"Not saved: {ex.Message}");
            return;
        }

        _currentDoc.MarkSaved();
        UpdateSidebarItem(_currentDoc);
        UpdateHeader();
        SetMessage("Saved as draft.");
    }

    private void UpdateSidebarItem(EditorDocument doc)
    {
        foreach (ListViewItem item in _openList.Items)
        {
            if (!ReferenceEquals(item.Tag, doc)) continue;
            item.SubItems[1].Text = doc.Record.ControlNumber ?? "";
            item.SubItems[2].Text = TitleOf(doc.Record);
            return;
        }
    }

    private void SaveTemplate()
    {
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _viewer.EndEdit();

        using var dialog = new SaveFileDialog
        {
            Title = "Save as Template",
            InitialDirectory = Directory.Exists(TemplatesFolder) ? TemplatesFolder : AppContext.BaseDirectory,
            Filter = "MARC text (*.mrk)|*.mrk",
            FileName = "template.mrk",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        File.WriteAllBytes(dialog.FileName,
            Marc.Core.Mrk.MrkWriter.ToBytes(new[] { _currentDoc.Record }));
        SetMessage($"Template saved: {Path.GetFileName(dialog.FileName)}.");
    }

    // ---------- import ----------

    private void ImportFolder()
    {
        if (_repo is null)
        {
            SetMessage("Open a catalogue first.");
            return;
        }

        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose a folder; every .mrk file in it (and its subfolders) will be imported.",
            UseDescriptionForTitle = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var engine = new ImportEngine(_repo);
        var plan = engine.AnalyzeFolder(dialog.SelectedPath);
        var report = plan.Report;

        if (report.TotalRecords == 0)
        {
            MessageBox.Show(this, "No records found (no .mrk files, or all empty).",
                "Import Folder", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var wizard = new ImportWizardForm(dialog.SelectedPath, report);
        if (wizard.ShowDialog(this) != DialogResult.OK) return; // nothing committed

        try
        {
            var result = engine.Commit(plan, wizard.SelectedMode);
            SetMessage($"Imported {result.RecordsImported} record(s) — BIB {result.BibCount}, AUT {result.AutCount}.");
        }
        catch (Microsoft.Data.Sqlite.SqliteException e)
        {
            // e.g. a record inserted between Analyze and Commit now collides;
            // the transaction rolled back — the catalogue is untouched.
            MessageBox.Show(this, $"Import failed and nothing was committed.\n\n{e.Message}",
                "Import Folder", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------- export ----------

    private void ExportBase()
    {
        if (_repo is null) { SetMessage("Open a catalogue first."); return; }
        int count = _repo.List(CurrentBase).Count;
        if (count == 0) { SetMessage($"{CurrentBase} is empty — nothing to export."); return; }
        ExportTo($"{CurrentBase}.mrk", path =>
        {
            new ExportEngine(_repo).ExportBaseToFile(CurrentBase, path);
            return count;
        });
    }

    private void ExportSelected()
    {
        if (_repo is null) { SetMessage("Open a catalogue first."); return; }
        var ids = _openList.SelectedItems.Cast<ListViewItem>()
            .Select(i => i.Tag).OfType<EditorDocument>()
            .Where(d => d.Stored.Id != 0).Select(d => d.Stored.Id).ToList();
        if (ids.Count == 0)
        {
            SetMessage("Select one or more saved records in the sidebar first.");
            return;
        }
        ExportTo("selection.mrk", path =>
        {
            new ExportEngine(_repo!).ExportToFile(ids, path);
            return ids.Count;
        });
    }

    private void ExportTo(string suggestedName, Func<string, int> write)
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Export",
            InitialDirectory = SuggestedFolder(),
            FileName = suggestedName,
            Filter = "MARC text (*.mrk)|*.mrk",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        int count = write(dialog.FileName);
        SetMessage($"Exported {count} record(s) to {dialog.FileName}.");
    }

    private void SetMessage(string text) => _messageLabel.Text = text;
}
