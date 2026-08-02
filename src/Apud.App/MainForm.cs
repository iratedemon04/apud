using System.Diagnostics.CodeAnalysis;
using Apud.Data;
using Apud.Sync;
using Marc.Core;
using Marc.Core.FixedFields;
using Marc.Core.Validation;

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
    private readonly ComboBox _sortBox;
    private readonly TextBox _searchBox;
    private readonly ListView _resultsList;
    private readonly ListView _historyList;
    private readonly SearchHistory _history = new();
    private IReadOnlyList<RecordSummary> _currentResults = Array.Empty<RecordSummary>();

    private readonly Label _recordHeader;
    private readonly DataGridView _viewer;
    private readonly ListView _findings;          // Ctrl+W/Ctrl+L output, click to jump

    // The scope dropdown is base-aware: BIB and AUT index completely different
    // fields, so each base shows its own list (Aleph presents different indexes per
    // base). _scopes holds whichever list is currently displayed.
    private static readonly (string Label, SearchScope Scope)[] BibScopes =
    {
        ("All fields", SearchScope.All),
        ("Title", SearchScope.Title),
        ("Author", SearchScope.Author),
        ("Subjects", SearchScope.Subjects),
        ("Series", SearchScope.Series),
        ("Publisher", SearchScope.Publisher),
        ("Notes", SearchScope.Notes),
        ("Call No.", SearchScope.CallNumber),
        ("ISBN/ISSN", SearchScope.Isbn),
        ("Control No.", SearchScope.ControlNumber),
    };

    private static readonly (string Label, SearchScope Scope)[] AutScopes =
    {
        ("All fields", SearchScope.All),
        ("Personal name", SearchScope.HeadingPersonal),
        ("Corporate name", SearchScope.HeadingCorporate),
        ("Meeting name", SearchScope.HeadingMeeting),
        ("Uniform title", SearchScope.HeadingUniform),
        ("Topical term", SearchScope.HeadingTopical),
        ("Geographic name", SearchScope.HeadingGeographic),
        ("Genre/form", SearchScope.HeadingGenre),
        ("See-from / Variants", SearchScope.SeeFrom),
        ("See-also / Related", SearchScope.SeeAlso),
        ("Sources", SearchScope.Sources),
        ("Control No.", SearchScope.ControlNumber),
    };

    private (string Label, SearchScope Scope)[] _scopes = BibScopes;
    private string _scopesBase = "";

    /// <summary>How the result set is ordered. Relevance keeps the FTS rank order
    /// (a keyword search's best-match-first); the rest are deterministic sorts the
    /// cataloguer chooses, ILS-style. Default is Control No. — predictable, always
    /// defined, and the same order as List All.</summary>
    private enum ResultSort { ControlNumber, Title, Author, Relevance }

    private static readonly (string Label, ResultSort Sort)[] Sorts =
    {
        ("Sort: Control No.", ResultSort.ControlNumber),
        ("Sort: Title", ResultSort.Title),
        ("Sort: Author", ResultSort.Author),
        ("Sort: Relevance", ResultSort.Relevance),
    };

    private readonly CommandRegistry _commands = new();
    private readonly Keymap _keymap;
    private readonly AppState _appState = AppState.Load();
    private FieldHelpForm? _fieldHelp;   // the F1 help panel, created on first use, reused thereafter

    private ApudDatabase? _db;
    private RecordRepository? _repo;
    private string? _catalogPath;          // the open .db path; MARC_OUT sits beside it
    private bool _syncingBase;
    private EditorDocument? _currentDoc; // the record showing in the editor
    private bool _rendering;             // grid is being rebuilt; ignore its edit events
    private bool _resumeEditAfterRender; // a structural edit re-rendered mid-typing; drop back into the cell
    private MarcField? _fieldClipboard;      // Ctrl+T copy field / Alt+T paste
    private MarcSubfield? _subfieldClipboard; // Ctrl+S copy subfield / Alt+S paste
    private int _pushesSinceSync;            // records pushed since the last server backup

    private string CurrentBase => _searchBase.SelectedIndex == 1 ? "AUT" : "BIB";

    private CommandContext ActiveContext =>
        _recordView.Visible ? CommandContext.Editor : CommandContext.Search;

    public MainForm()
    {
        Text = "Apud";
        var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("Apud.App.apud.ico");
        if (iconStream != null) Icon = new Icon(iconStream);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(950, 620);

        // ----- command table (Module 6 step 1) -----
        // Menus render from this table and keymap.json binds against it.
        // Catalogue commands are menu-only (§6.2: record commands own the keyboard).
        _commands.Add(new Command { Id = "catalogue.new", Name = "&New Catalogue...", Execute = NewCatalog });
        _commands.Add(new Command { Id = "catalogue.open", Name = "&Open Catalogue...", DefaultKey = "Ctrl+O", Execute = OpenCatalogDialog });
        _commands.Add(new Command { Id = "catalogue.import", Name = "&Import Records...", Execute = ImportRecords });
        _commands.Add(new Command { Id = "catalogue.marc-out", Name = "Set &BIB Output Folder...", Execute = () => SetMarcOutFolder("BIB") });
        _commands.Add(new Command { Id = "catalogue.marc-out-aut", Name = "Set &Authority Output Folder...", Execute = () => SetMarcOutFolder("AUT") });
        _commands.Add(new Command { Id = "catalogue.org-code", Name = "Set &Organization Code...", Execute = SetOrgCode });
        _commands.Add(new Command { Id = "sync.configure", Name = "&Set Server...", Execute = ConfigureSync });
        _commands.Add(new Command { Id = "sync.upload", Name = "&Back Up to Server", DefaultKey = "Ctrl+U", Execute = UploadToServer });
        _commands.Add(new Command { Id = "sync.restore", Name = "&Restore from Server...", Execute = RestoreFromServer });
        _commands.Add(new Command { Id = "catalogue.export-base", Name = "Export &Base...", Execute = ExportBase });
        _commands.Add(new Command { Id = "catalogue.export-selected", Name = "Export &Selected...", Execute = ExportSelected });
        _commands.Add(new Command { Id = "app.exit", Name = "E&xit", DefaultKey = "Alt+F4", Execute = Close });
        _commands.Add(new Command { Id = "base.bib", Name = "&BIB — Bibliographic", Execute = () => SetBase("BIB") });
        _commands.Add(new Command { Id = "base.aut", Name = "&AUT — Authority", Execute = () => SetBase("AUT") });
        _commands.Add(new Command { Id = "search.focus", Name = "&Search", DefaultKey = "F2", Execute = ShowSearchView });
        _commands.Add(new Command { Id = "help.field", Name = "&Field Help", DefaultKey = "F1", Execute = ShowFieldHelp });
        _commands.Add(new Command { Id = "help.intro", Name = "&Getting Started", Execute = ShowIntro });
        _commands.Add(new Command { Id = "help.about", Name = "&About Apud", Execute = ShowAbout });
        // Editor commands (Module 6 steps 3-7). §6.2: record commands own the keyboard.
        _commands.Add(new Command { Id = "record.new", Name = "&New Record / Copy", DefaultKey = "Ctrl+N", Execute = NewRecord });
        _commands.Add(new Command { Id = "record.save-draft", Name = "&Save Draft", Context = CommandContext.Editor, DefaultKey = "Ctrl+D", Execute = SaveDraft });
        _commands.Add(new Command { Id = "record.save-template", Name = "Save as &Template...", Context = CommandContext.Editor, DefaultKey = "Ctrl+Shift+T", Execute = SaveTemplate });
        _commands.Add(new Command { Id = "record.undo", Name = "&Undo", Context = CommandContext.Editor, DefaultKey = "Ctrl+Z", Execute = UndoEdit });
        _commands.Add(new Command { Id = "record.redo", Name = "&Redo", Context = CommandContext.Editor, DefaultKey = "Ctrl+Y", Execute = RedoEdit });
        _commands.Add(new Command { Id = "field.edit", Name = "&Edit Field (cursor)", Context = CommandContext.Editor, DefaultKey = "Insert", Execute = BeginEditCurrentCell });
        _commands.Add(new Command { Id = "field.new", Name = "New &Field", Context = CommandContext.Editor, DefaultKey = "F6", Execute = NewField });
        _commands.Add(new Command { Id = "subfield.new", Name = "New Su&bfield", Context = CommandContext.Editor, DefaultKey = "F7", Execute = NewSubfield });
        _commands.Add(new Command { Id = "field.delete", Name = "&Delete Field", Context = CommandContext.Editor, DefaultKey = "Ctrl+F5", Execute = DeleteCurrentField });
        _commands.Add(new Command { Id = "field.delete-selected", Name = "Delete Se&lected Fields", Context = CommandContext.Editor, DefaultKey = "Ctrl+Shift+F5", Execute = DeleteSelectedFields });
        _commands.Add(new Command { Id = "subfield.delete", Name = "Delete Subf&ield", Context = CommandContext.Editor, DefaultKey = "Ctrl+F7", Execute = DeleteCurrentSubfield });
        _commands.Add(new Command { Id = "field.copy", Name = "&Copy Field", Context = CommandContext.Editor, DefaultKey = "Ctrl+T", Execute = CopyField });
        _commands.Add(new Command { Id = "field.paste", Name = "&Paste Field", Context = CommandContext.Editor, DefaultKey = "Alt+T", Execute = PasteField });
        _commands.Add(new Command { Id = "subfield.copy", Name = "Copy Subfield", Context = CommandContext.Editor, DefaultKey = "Ctrl+S", Execute = CopySubfield });
        _commands.Add(new Command { Id = "subfield.paste", Name = "Paste Subfield", Context = CommandContext.Editor, DefaultKey = "Alt+S", Execute = PasteSubfield });
        // Stubs wired, not built — keys rebindable from day one (step 7).
        _commands.Add(new Command { Id = "field.fixed-edit", Name = "Edit Fixed Field by &Position...", Context = CommandContext.Editor, DefaultKey = "Ctrl+F3", Execute = EditFixedField });
        _commands.Add(new Command { Id = "field.validate", Name = "Browse && Link &Heading", Context = CommandContext.Editor, DefaultKey = "Ctrl+F4", Execute = BrowseAndLinkHeading });
        _commands.Add(new Command { Id = "record.validate", Name = "Validate &Record", Context = CommandContext.Editor, DefaultKey = "Ctrl+W", Execute = ValidateRecord });
        _commands.Add(new Command { Id = "record.push", Name = "Validate && &Push", Context = CommandContext.Editor, DefaultKey = "Ctrl+L", Execute = PushRecord });
        _commands.Add(new Command { Id = "record.delete", Name = "&Delete Record...", Context = CommandContext.Editor, DefaultKey = "Ctrl+Delete", Execute = DeleteRecord });

        _keymap = Keymap.LoadFile(_commands, Path.Combine(AppContext.BaseDirectory, Keymap.FileName));

        // ----- menu (rendered from the command table) -----
        _menu = new MenuStrip();

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(MenuItem("catalogue.new"));
        file.DropDownItems.Add(MenuItem("catalogue.open"));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(MenuItem("catalogue.import"));
        file.DropDownItems.Add(MenuItem("catalogue.marc-out"));
        file.DropDownItems.Add(MenuItem("catalogue.marc-out-aut"));
        file.DropDownItems.Add(MenuItem("catalogue.org-code"));
        file.DropDownItems.Add(MenuItem("catalogue.export-base"));
        file.DropDownItems.Add(MenuItem("catalogue.export-selected"));
        file.DropDownItems.Add(new ToolStripSeparator());
        var server = new ToolStripMenuItem("&Backup Server");
        server.DropDownItems.Add(MenuItem("sync.configure"));
        server.DropDownItems.Add(MenuItem("sync.upload"));
        server.DropDownItems.Add(MenuItem("sync.restore"));
        file.DropDownItems.Add(server);
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
        record.DropDownItems.Add(MenuItem("field.edit"));
        record.DropDownItems.Add(MenuItem("field.new"));
        record.DropDownItems.Add(MenuItem("subfield.new"));
        record.DropDownItems.Add(MenuItem("field.delete"));
        record.DropDownItems.Add(MenuItem("field.delete-selected"));
        record.DropDownItems.Add(MenuItem("subfield.delete"));
        record.DropDownItems.Add(MenuItem("field.copy"));
        record.DropDownItems.Add(MenuItem("field.paste"));
        record.DropDownItems.Add(MenuItem("subfield.copy"));
        record.DropDownItems.Add(MenuItem("subfield.paste"));
        record.DropDownItems.Add(new ToolStripSeparator());
        record.DropDownItems.Add(MenuItem("field.fixed-edit"));
        record.DropDownItems.Add(MenuItem("field.validate"));
        record.DropDownItems.Add(MenuItem("record.validate"));
        record.DropDownItems.Add(MenuItem("record.push"));
        record.DropDownItems.Add(new ToolStripSeparator());
        record.DropDownItems.Add(MenuItem("record.delete"));

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(MenuItem("help.field"));
        help.DropDownItems.Add(MenuItem("help.intro"));
        help.DropDownItems.Add(new ToolStripSeparator());
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

        _searchScope = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        PopulateScopes(CurrentBase);

        _sortBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
        foreach (var (label, _) in Sorts) _sortBox.Items.Add(label);
        _sortBox.SelectedIndex = 0;   // deterministic default: Control No.
        // Changing the sort re-orders the current results in place, no re-query.
        _sortBox.SelectedIndexChanged += (_, _) => RenderResults();

        _searchBox = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right };
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { RunSearch(); e.SuppressKeyPress = true; }
            // Down arrow steps straight into the results — no reaching for the
            // mouse to pick a hit (user request 2026-08-01).
            else if (e.KeyCode == Keys.Down && _resultsList!.Items.Count > 0) { FocusResults(); e.SuppressKeyPress = true; }
        };
        var searchButton = new Button { Text = "Search", Width = 60 };
        searchButton.Click += (_, _) => RunSearch();
        var listAllButton = new Button { Text = "List All", Width = 60 };
        listAllButton.Click += (_, _) => ListAll();

        var searchForm = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 32,
            ColumnCount = 6,
            Padding = new Padding(2),
        };
        searchForm.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        searchForm.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        searchForm.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        searchForm.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        searchForm.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        searchForm.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        searchForm.Controls.Add(_searchBase, 0, 0);
        searchForm.Controls.Add(_searchScope, 1, 0);
        searchForm.Controls.Add(_sortBox, 2, 0);
        searchForm.Controls.Add(_searchBox, 3, 0);
        searchForm.Controls.Add(searchButton, 4, 0);
        searchForm.Controls.Add(listAllButton, 5, 0);

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
            // Up from the first hit hands focus back to the search box, so the
            // whole search→pick loop stays on the keyboard.
            else if (e.KeyCode == Keys.Up && _resultsList.FocusedItem?.Index == 0)
            {
                _searchBox.Focus();
                _searchBox.SelectionStart = _searchBox.TextLength;
                e.SuppressKeyPress = true;
            }
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
            // Multi-select so a range of fields can be picked (Shift/Ctrl-click or
            // Shift+arrows) and deleted at once — e.g. pruning a pasted-in record
            // down to a new authority (user request 2026-08-01). Single-cell
            // editing is unaffected.
            MultiSelect = true,
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
        _viewer.Columns.Add(NewColumn("tag", 42, font: mono, bold: true, underline: true, color: Color.Gray));
        _viewer.Columns.Add(NewColumn("ind", 34, font: mono));
        _viewer.Columns.Add(NewColumn("code", 26, font: mono, bold: true, underline: true));
        var value = NewColumn("value", 200, font: mono, bold: true);
        value.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        value.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _viewer.Columns.Add(value);

        // Validation output (Module 9): a list docked below the record, hidden
        // until Ctrl+W/Ctrl+L produces findings. Clicking or pressing Enter on a
        // row jumps the cursor to the offending field (docs/PLAN.md §5).
        _findings = new ListView
        {
            Dock = DockStyle.Bottom,
            Height = 130,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            Visible = false,
        };
        _findings.Columns.Add("", 70);          // severity
        _findings.Columns.Add("Field", 90);
        _findings.Columns.Add("Message", 640);
        _findings.DoubleClick += (_, _) => JumpToSelectedFinding();
        _findings.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { JumpToSelectedFinding(); e.Handled = true; } };

        _recordView = new Panel { Dock = DockStyle.Fill, Visible = false };
        // Add order sets docking: viewer fills, findings pins to the bottom,
        // header pins to the top.
        _recordView.Controls.Add(_viewer);
        _recordView.Controls.Add(_findings);
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
        if (FieldHelp.LoadFile(Path.Combine(AppContext.BaseDirectory, FieldHelp.FileName)) is string tagHelpReport)
            configReports.Add(tagHelpReport);
        foreach (var b in new[] { "BIB", "AUT" })
            if (ValidationProfileConfig.LoadFile(
                    Path.Combine(AppContext.BaseDirectory, ValidationProfileConfig.FileName(b)), b) is string profileReport)
                configReports.Add(profileReport);
        configReports.AddRange(_keymap.Diagnostics);
        Load += (_, _) =>
        {
            SetMessage(configReports.Count > 0
                ? string.Join("  |  ", configReports)
                : "No catalogue open — File → New Catalogue or Open Catalogue.");
            MaybeShowIntro();
        };
        FormClosing += OnFormClosing;
        FormClosed += (_, _) => _db?.Dispose();
    }

    /// <summary>On-exit backup prompt (docs/PLAN.md §9b trigger): if a server is
    /// configured and records were pushed since the last upload, offer to back up
    /// before closing. Cancel keeps the window open. Only appears when both apply —
    /// a catalogue with no server never sees it.</summary>
    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_repo is null || _pushesSinceSync == 0) return;
        var settings = SyncSettings.Load(_repo);
        if (!settings.IsConfigured) return;

        var answer = MessageBox.Show(this,
            $"{_pushesSinceSync} record(s) were pushed since the last backup.\n\nBack up to the server before closing?",
            "Apud", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel) { e.Cancel = true; return; }
        if (answer == DialogResult.Yes) UploadToServer();
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

    /// <summary>F1: show the offline MARC21 help for the field at the caret in the
    /// reusable, non-modal help panel. Works from the editor (help for the current
    /// field or the leader); elsewhere it says so rather than doing nothing.</summary>
    private void ShowFieldHelp()
    {
        string? tag = null;
        if (_recordView.Visible && _currentDoc is not null && CurrentRef() is { } at)
            tag = at.FieldIndex < 0 ? "LDR" : _currentDoc.Record.Fields[at.FieldIndex].Tag;

        if (tag is null)
        {
            SetMessage("Field Help (F1): open a record and stand on a field to see its MARC21 help.");
            return;
        }

        if (_fieldHelp is null || _fieldHelp.IsDisposed)
        {
            _fieldHelp = new FieldHelpForm();
            _fieldHelp.Location = new Point(
                Math.Max(0, Bounds.Right - _fieldHelp.Width - 40),
                Math.Max(0, Bounds.Top + 120));
        }
        _fieldHelp.ShowHelp(tag);
        if (!_fieldHelp.Visible) _fieldHelp.Show(this);
        else _fieldHelp.BringToFront();
        // Keep the caret in the editor — help is a glance, not a focus change.
        _viewer.Focus();
    }

    /// <summary>Help → Getting Started: the terse three-step intro. Reachable any
    /// time; also shown once on a fresh install by <see cref="MaybeShowIntro"/>.</summary>
    private void ShowIntro()
    {
        using var intro = new IntroForm();
        intro.ShowDialog(this);
    }

    /// <summary>Auto-shows the intro exactly once on a clean install, then records
    /// that so it never appears on its own again (Help → Getting Started still opens
    /// it). This is one-time onboarding, not remembered session state.</summary>
    private void MaybeShowIntro()
    {
        if (_appState.FirstRunDone) return;
        _appState.FirstRunDone = true;
        _appState.Save();
        ShowIntro();
    }

    private void ShowAbout()
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
        about.ShowDialog(this);
    }

    private void ShowLegal(IWin32Window owner) =>
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

    /// <summary>Where a File dialog should open: the last folder used if we still
    /// have it, else the suggested one. The one remembered piece of UI state (user
    /// 2026-08-01, explicit exception) — see <see cref="AppState"/>.</summary>
    private string StartFolder() =>
        !string.IsNullOrEmpty(_appState.LastFolder) && Directory.Exists(_appState.LastFolder)
            ? _appState.LastFolder!
            : SuggestedFolder();

    /// <summary>Remembers the folder a dialog just used so the next one opens there.</summary>
    private void RememberFolder(string? folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        _appState.LastFolder = folder;
        _appState.Save();
    }

    private void NewCatalog()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "New Catalogue",
            InitialDirectory = StartFolder(),
            FileName = "catalog.db",
            Filter = "Apud catalogue (*.db)|*.db",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        RememberFolder(Path.GetDirectoryName(dialog.FileName));

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
            InitialDirectory = StartFolder(),
            Filter = "Apud catalogue (*.db)|*.db|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        RememberFolder(Path.GetDirectoryName(dialog.FileName));
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
        _catalogPath = path;

        // A different catalogue means different record ids: everything on
        // screen belonged to the old one.
        _openList.Items.Clear();
        _resultsList.Items.Clear();
        _currentResults = Array.Empty<RecordSummary>();
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
        PopulateScopes(@base);
    }

    /// <summary>Fills the scope dropdown with the current base's indexes (BIB and
    /// AUT are entirely different). No-op when the base's list is already shown, so
    /// switching to the same base doesn't wipe the user's chosen scope.</summary>
    private void PopulateScopes(string @base)
    {
        if (@base == _scopesBase && _searchScope.Items.Count > 0) return;
        _scopesBase = @base;
        _scopes = @base == "AUT" ? AutScopes : BibScopes;
        _searchScope.Items.Clear();
        foreach (var (label, _) in _scopes) _searchScope.Items.Add(label);
        _searchScope.SelectedIndex = 0;
    }

    // ---------- search ----------

    private void RunSearch()
    {
        if (!RequireCatalogue()) return;
        string query = _searchBox.Text.Trim();
        if (query.Length == 0) return;

        var scope = _scopes[_searchScope.SelectedIndex].Scope;
        var ids = _repo.Search(CurrentBase, query, scope);

        _history.Add(new SearchHistoryEntry(query, scope, CurrentBase, ids.Count));
        RefreshHistoryList();

        // ids arrive in FTS relevance order; keep that as the base order so the
        // Relevance sort can reproduce it. Other sorts reorder in RenderResults.
        var byId = _repo.List(CurrentBase).ToDictionary(s => s.Id);
        _currentResults = ids.Select(id => byId.GetValueOrDefault(id)).Where(s => s != null).Cast<RecordSummary>().ToList();
        RenderResults();
        SetMessage($"{ids.Count} hit(s) for \"{query}\" in {CurrentBase}.");
    }

    /// <summary>The explicit whole-base listing. Feeds the same sort dropdown; its
    /// natural order is control-number, which is also the default sort.</summary>
    private void ListAll()
    {
        if (!RequireCatalogue()) return;
        _currentResults = _repo.List(CurrentBase);
        RenderResults();
        SetMessage($"{CurrentBase}: {_currentResults.Count} record(s).");
    }

    /// <summary>Orders <see cref="_currentResults"/> by the chosen sort and fills the
    /// list. Relevance keeps the incoming (FTS rank) order; the deterministic sorts
    /// are stable, so ties keep that same underlying order.</summary>
    private void RenderResults()
    {
        var sort = Sorts[_sortBox.SelectedIndex].Sort;
        IEnumerable<RecordSummary> ordered = sort switch
        {
            ResultSort.Title => _currentResults.OrderBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase),
            ResultSort.Author => _currentResults.OrderBy(s => s.Author, StringComparer.CurrentCultureIgnoreCase),
            ResultSort.ControlNumber => _currentResults.OrderBy(s => ControlNumberKey(s.ControlNumber)),
            _ => _currentResults,   // Relevance: as-is
        };
        FillResults(ordered);
    }

    /// <summary>Sort key for a 001: numeric when it is a plain integer (so 2 &lt; 10),
    /// otherwise a large sentinel so odd/blank values fall to the end in a stable
    /// way. Ties among those keep the underlying order.</summary>
    private static long ControlNumberKey(string? controlNumber) =>
        long.TryParse(controlNumber, out long n) ? n : long.MaxValue;

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

    /// <summary>Moves the keyboard into the results list, selecting the first hit
    /// if none is selected — the entry point for arrow-key navigation.</summary>
    private void FocusResults()
    {
        if (_resultsList.Items.Count == 0) return;
        var item = _resultsList.SelectedItems.Count > 0 ? _resultsList.SelectedItems[0] : _resultsList.Items[0];
        item.Selected = true;
        item.Focused = true;
        _resultsList.EnsureVisible(item.Index);
        _resultsList.Focus();
    }

    /// <summary>The display label for a scope, from whichever base's list defines it
    /// (search history can hold entries from either base).</summary>
    private static string ScopeLabel(SearchScope scope)
    {
        foreach (var list in new[] { BibScopes, AutScopes })
        {
            int i = Array.FindIndex(list, s => s.Scope == scope);
            if (i >= 0) return list[i].Label;
        }
        return scope.ToString();
    }

    private void RefreshHistoryList()
    {
        _historyList.BeginUpdate();
        _historyList.Items.Clear();
        foreach (var e in _history.Entries)
        {
            var item = new ListViewItem(e.Query);
            item.SubItems.Add(e.Base);
            item.SubItems.Add(ScopeLabel(e.Scope));
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
        SetBase(e.Base);   // repopulates the scope list for that base
        _searchBox.Text = e.Query;
        int idx = Array.FindIndex(_scopes, s => s.Scope == e.Scope);
        _searchScope.SelectedIndex = idx >= 0 ? idx : 0;
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
        RenderRecord(preservePosition: false);
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

    /// <summary>Redraws the grid and header from the current document. When
    /// <paramref name="preservePosition"/> is set the cursor stays on the same
    /// field/subfield/column across the rebuild — so a structural edit made while
    /// typing (retag, first subfield) does not fling the cursor to the top and
    /// force a click (user, 2026-08-01). Switching to a different record passes
    /// false, since the old position means nothing in the new record.</summary>
    private void RenderRecord(bool preservePosition = true)
    {
        if (_currentDoc is null) { _recordHeader.Text = ""; _viewer.Rows.Clear(); return; }
        var doc = _currentDoc;

        var keep = preservePosition ? CaptureCell() : null;
        bool resume = _resumeEditAfterRender && _viewer.ContainsFocus;
        _resumeEditAfterRender = false;

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

        if (keep is { } k && RestoreCell(k))
        {
            if (resume && _viewer.CurrentCell is { ReadOnly: false }) _viewer.BeginEdit(false);
        }
    }

    /// <summary>The cursor's logical position (field/subfield/column), stable
    /// across a rebuild because it is stored by model index, not grid row.</summary>
    private (int FieldIndex, int SubfieldIndex, string Column)? CaptureCell()
    {
        var cell = _viewer.CurrentCell;
        if (cell?.OwningRow.Tag is DisplayRow r)
            return (r.FieldIndex, r.SubfieldIndex, _viewer.Columns[cell.ColumnIndex].Name);
        return null;
    }

    /// <summary>Puts the cursor back on a captured position after a rebuild;
    /// false when that field/subfield no longer exists (e.g. it was deleted).</summary>
    private bool RestoreCell((int FieldIndex, int SubfieldIndex, string Column) pos)
    {
        foreach (DataGridViewRow gridRow in _viewer.Rows)
        {
            if (gridRow.Tag is DisplayRow r && r.FieldIndex == pos.FieldIndex && r.SubfieldIndex == pos.SubfieldIndex)
            {
                _viewer.CurrentCell = gridRow.Cells[pos.Column];
                return true;
            }
        }
        return false;
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
        if (structural)
        {
            // The grid has already advanced the cursor (Tab/Enter) to the next
            // cell; re-render preserving that spot and drop back into it so the
            // tag→indicators→code→value flow stays on the keyboard (task 5).
            _resumeEditAfterRender = true;
            BeginInvoke(() => RenderRecord());
        }
    }

    /// <summary>Caps the editing box by column to the fixed MARC widths: a tag
    /// is 3 characters, indicators 2, a subfield code 1. Any characters are
    /// allowed within that width (dumb editor) — only the length is fixed. The
    /// grid reuses one editing control across cells, so this is set every time.</summary>
    private void ViewerEditingControlShowing(object? sender, DataGridViewEditingControlShowingEventArgs e)
    {
        if (e.Control is not TextBox tb) return;
        string? col = _viewer.CurrentCell?.OwningColumn.Name;
        tb.MaxLength = col switch
        {
            "tag" => 3,
            "ind" => 2,
            "code" => 1,
            _ => 0, // 0 = unlimited (value and control-data cells)
        };

        // The three fixed micro-cells (tag 3, indicators 2, code 1) open with
        // their content SELECTED, so the first keystroke replaces it — otherwise a
        // brand-new field's placeholder ("   " / "__") already fills the cell to
        // its max length and the cataloguer must delete those "bars" before typing
        // (user report 2026-08-01). The larger value/control cells keep the
        // text-editor feel: a caret at the end, nothing selected.
        bool micro = col is "tag" or "ind" or "code";
        BeginInvoke(() =>
        {
            if (tb.IsDisposed || !tb.IsHandleCreated) return;
            if (micro) { tb.SelectAll(); return; }
            tb.SelectionStart = tb.Text.Length;
            tb.SelectionLength = 0;
        });
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

    /// <summary>Puts the cursor on a field/subfield after a structural change and
    /// focuses the grid, so the cursor visibly lands there — e.g. on the field
    /// below after a delete — instead of leaving focus adrift (task 2a).</summary>
    private void SelectCell(int fieldIndex, int subfieldIndex, string column)
    {
        foreach (DataGridViewRow gridRow in _viewer.Rows)
        {
            if (gridRow.Tag is DisplayRow r && r.FieldIndex == fieldIndex && r.SubfieldIndex == subfieldIndex)
            {
                _viewer.CurrentCell = gridRow.Cells[column];
                _viewer.Focus();
                return;
            }
        }
    }

    /// <summary>Lands the cursor on a field's first row (its tag cell) whatever the
    /// field's shape — the reliable "put me on this field" after a multi-field
    /// delete, where the surviving field may be a data field with subfields.</summary>
    private void SelectFieldRow(int fieldIndex)
    {
        foreach (DataGridViewRow gridRow in _viewer.Rows)
        {
            if (gridRow.Tag is DisplayRow r && r.FieldIndex == fieldIndex)
            {
                _viewer.CurrentCell = gridRow.Cells["tag"];
                _viewer.Focus();
                return;
            }
        }
    }

    /// <summary>Insert: drop into the current cell with a caret at the end — the
    /// "pulsing bar", not a highlighted value — so the record reads as a text
    /// editor (task 1). Read-only cells (a field name, a control field's
    /// indicators) simply say so.</summary>
    private void BeginEditCurrentCell()
    {
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        if (_viewer.CurrentCell is not { } cell) { SetMessage("Stand on a field first."); return; }
        if (cell.ReadOnly) { SetMessage("Nothing to type here — move to the tag, indicators, code or value."); return; }
        _viewer.Focus();
        _viewer.BeginEdit(false); // false = caret, not select-all (EditingControlShowing puts it at the end)
    }

    private void NewField()
    {
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _viewer.EndEdit();
        int after = CurrentRef()?.FieldIndex ?? _currentDoc.Record.Fields.Count - 1;
        int at = _currentDoc.InsertBlankFieldAfter(after);
        RenderRecord();
        SelectCell(at, -1, "tag");
        _viewer.BeginEdit(false); // caret in the tag cell — start typing at once (task 5)
        SetMessage("New field — type its tag, then Tab through indicators, code and value.");
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
        _viewer.BeginEdit(false); // caret in the new subfield code — start typing at once
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

    /// <summary>Ctrl+Shift+F5: delete every field that has a selected cell in one
    /// undoable step — the bulk prune for a pasted-in record (user request
    /// 2026-08-01). With one field selected it behaves like Ctrl+F5; several ask
    /// first. The leader is never touched.</summary>
    private void DeleteSelectedFields()
    {
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _viewer.EndEdit();

        var indices = _viewer.SelectedCells.Cast<DataGridViewCell>()
            .Select(c => c.OwningRow.Tag as DisplayRow)
            .Where(r => r is { FieldIndex: >= 0 })
            .Select(r => r!.FieldIndex)
            .Distinct().ToList();

        if (indices.Count == 0)
        {
            SetMessage("Select one or more fields first (the leader cannot be deleted).");
            return;
        }
        if (indices.Count > 1 && MessageBox.Show(this,
                $"Delete {indices.Count} selected fields?",
                "Delete Fields", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        int land = indices.Min(); // the field the survivors shift up into
        _currentDoc.DeleteFields(indices);
        RenderRecord();
        UpdateSidebarItem(_currentDoc);
        UpdateHeader();
        if (_currentDoc.Record.Fields.Count > 0)
            SelectFieldRow(Math.Min(land, _currentDoc.Record.Fields.Count - 1));
        SetMessage($"Deleted {indices.Count} field(s).");
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

    // ---------- copy / paste field & subfield (user request 2026-08-01) ----------

    /// <summary>Ctrl+T: copy the whole field under the cursor onto the field
    /// clipboard (a deep copy, so later edits to the record don't change it). The
    /// clipboard survives switching records, so a field can be pasted into another
    /// record.</summary>
    private void CopyField()
    {
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _viewer.EndEdit();
        if (CurrentRef() is not { } at || at.FieldIndex < 0)
        {
            SetMessage("Stand in a field first (the leader cannot be copied).");
            return;
        }
        _fieldClipboard = _currentDoc.CopyField(at.FieldIndex);
        SetMessage($"Copied field {_currentDoc.Record.Fields[at.FieldIndex].Tag} — Alt+T pastes it.");
    }

    /// <summary>Alt+T: paste the copied field as a new field just below the cursor
    /// (a fresh clone each time). Never reorders — like every other editor edit,
    /// the cataloguer places it.</summary>
    private void PasteField()
    {
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        if (_fieldClipboard is null) { SetMessage("No field copied yet — Ctrl+T copies the current field."); return; }
        _viewer.EndEdit();
        int after = CurrentRef()?.FieldIndex ?? _currentDoc.Record.Fields.Count - 1;
        int at = _currentDoc.PasteFieldAfter(after, _fieldClipboard);
        RenderRecord();
        UpdateSidebarItem(_currentDoc);
        UpdateHeader();
        SelectCell(at, -1, "tag");
        SetMessage($"Pasted field {_fieldClipboard.Tag}.");
    }

    /// <summary>Ctrl+S: copy the subfield under the cursor onto the subfield
    /// clipboard.</summary>
    private void CopySubfield()
    {
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _viewer.EndEdit();
        if (CurrentRef() is not { } at || at.FieldIndex < 0 || at.SubfieldIndex < 0)
        {
            SetMessage("Stand on a subfield first.");
            return;
        }
        _subfieldClipboard = _currentDoc.CopySubfield(at.FieldIndex, at.SubfieldIndex);
        SetMessage($"Copied subfield ‡{_subfieldClipboard.Code} — Alt+S pastes it.");
    }

    /// <summary>Alt+S: paste the copied subfield just after the cursor's subfield
    /// (or at the top of the field when standing on an empty one).</summary>
    private void PasteSubfield()
    {
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        if (_subfieldClipboard is null) { SetMessage("No subfield copied yet — Ctrl+S copies the current subfield."); return; }
        _viewer.EndEdit();
        if (CurrentRef() is not { } at || at.FieldIndex < 0)
        {
            SetMessage("Stand in a field first.");
            return;
        }
        var (index, error) = _currentDoc.PasteSubfieldAfter(at.FieldIndex, at.SubfieldIndex, _subfieldClipboard);
        if (error != null) { SetMessage(error); return; }
        RenderRecord();
        UpdateSidebarItem(_currentDoc);
        SelectCell(at.FieldIndex, index, "value");
        SetMessage($"Pasted subfield ‡{_subfieldClipboard.Code}.");
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

    // ---------- authority browse + link (Ctrl+F4, Module 8) ----------

    /// <summary>Ctrl+F4: from a controlled bib heading field, open the AUT browse
    /// list positioned at the field text; Enter on a heading rewrites the field to
    /// the authorized form and stores the link (§6.2 red-pen: "Enter links BOTH
    /// records"). The write goes through EditorDocument, so Ctrl+Z reverts it.</summary>
    private void BrowseAndLinkHeading()
    {
        if (!RequireCatalogue()) return;
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _viewer.EndEdit();

        var doc = _currentDoc;
        if (doc.Stored.Base != "BIB")
        {
            SetMessage("Ctrl+F4 links bibliographic headings to the authority base — open a BIB record.");
            return;
        }
        if (CurrentRef() is not { } at || at.FieldIndex < 0)
        {
            SetMessage("Stand on a heading field first (a name, subject, or added entry).");
            return;
        }

        var field = doc.Record.Fields[at.FieldIndex];
        if (!Headings.IsControlledBibTag(field.Tag))
        {
            SetMessage($"{field.Tag} is not a controlled heading field — Ctrl+F4 works on 1XX/240/6XX/7XX/8XX.");
            return;
        }

        string fieldText = Headings.HeadingText(field);
        BrowseResult Position(string text) => _repo.BrowseHeadings(HeadingNormalization.Normalize(text));

        var initial = Position(fieldText);
        if (initial.Entries.Count == 0)
        {
            SetMessage("No authority headings to browse — the AUT base has no pushed records yet.");
            return;
        }

        using var form = new AuthorityBrowseForm(fieldText, initial, Position);
        if (form.ShowDialog(this) != DialogResult.OK || form.SelectedAuthRecordId is not long authId) return;

        var auth = _repo.Load(authId);
        if (auth is null) { SetMessage("That authority record could not be loaded."); return; }

        if (!doc.LinkAuthority(at.FieldIndex, authId, auth.Record))
        {
            SetMessage("That authority record has no 1XX heading to copy — nothing linked.");
            return;
        }

        RenderRecord();
        UpdateSidebarItem(doc);
        SelectCell(at.FieldIndex, 0, "value");
        SetMessage($"Linked {field.Tag} to authorized heading: {form.SelectedDisplay}");
    }

    // ---------- validate + push (Ctrl+W / Ctrl+L, Module 9) ----------

    /// <summary>Ctrl+W: run the whole pipeline as a dry run — nothing is written.
    /// Errors and warnings both show in the findings list; a clean record just
    /// says so.</summary>
    private void ValidateRecord()
    {
        if (!RequireCatalogue()) return;
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _viewer.EndEdit();

        // A brief, visible beat so the cataloguer sees that validation ran — a
        // clean record's result is otherwise a single unchanged line and reads as
        // "nothing happened".
        SetMessage("Validating…");
        _messageBar.Refresh();
        Cursor.Current = Cursors.WaitCursor;
        System.Threading.Thread.Sleep(200);
        Cursor.Current = Cursors.Default;

        var profile = ValidationProfileConfig.For(_currentDoc.Stored.Base);
        var findings = new PushService(_repo).Check(_currentDoc.Stored, profile);
        ShowFindings(findings);

        if (findings.Count == 0)
            SetMessage("✓ Validation complete — the record is valid, no problems found.");
        else
            SetMessage($"Validation complete — {FindingSummary(findings)} (see the list below). Nothing was pushed.");
    }

    /// <summary>Ctrl+L: validate and push. On any error nothing is written and the
    /// findings list stays up so the cataloguer can click straight to each one; a
    /// clean record is promoted to pushed (001/005/leader derived) and, for an
    /// authority record, ripples into its linked bibs.</summary>
    private void PushRecord()
    {
        if (!RequireCatalogue()) return;
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _viewer.EndEdit();

        var doc = _currentDoc;

        // Already pushed and untouched since: there is nothing to write. Skip the
        // whole cycle so a repeat Ctrl+L doesn't restamp 005, rewrite the row, or
        // rewrite the .mrk for no reason.
        if (doc.Stored.Status == RecordStatus.Pushed && !doc.Dirty)
        {
            ClearFindings();
            SetMessage($"{doc.Record.ControlNumber} is already pushed and unchanged.");
            return;
        }

        var profile = ValidationProfileConfig.For(doc.Stored.Base);

        // A brief, visible beat so the push reads as a real action (same as Ctrl+W).
        SetMessage("Validating and pushing…");
        _messageBar.Refresh();
        Cursor.Current = Cursors.WaitCursor;
        System.Threading.Thread.Sleep(200);
        Cursor.Current = Cursors.Default;

        PushResult result;
        try
        {
            result = new PushService(_repo).Push(doc.Stored, profile);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            SetMessage($"Not pushed: {ex.Message}");
            return;
        }

        if (!result.Ok)
        {
            ShowFindings(result.Findings);
            SetMessage($"{FindingSummary(result.Findings)} — push blocked; nothing was written.");
            return;
        }

        // Pushed: the record was reordered and its mechanical fields filled, so
        // re-render and refresh. Warnings (if any) were shown against the pre-push
        // layout during the run; the summary reports their count.
        doc.MarkSaved();
        _pushesSinceSync++; // for the on-exit "back up first?" prompt
        RenderRecord();
        UpdateSidebarItem(doc);
        UpdateHeader();
        ClearFindings();

        // Mirror the pushed record to <output folder>\<001>.mrk (user request).
        // The push itself is already committed; a file-write failure is reported
        // but does not undo the push.
        string mirrorNote;
        try
        {
            string? written = RecordMirror.Write(MarcOutFolder(doc.Stored.Base), doc.Record);
            mirrorNote = written is not null
                ? $" Wrote {Path.GetFileName(written)} to {Path.GetDirectoryName(written)}."
                : $" (no MARC output folder — File → Set {doc.Stored.Base} Output Folder to save .mrk files.)";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            mirrorNote = $" (but its .mrk file could not be written: {ex.Message})";
        }

        int warnings = result.Warnings.Count();
        string msg = $"Pushed as {doc.Record.ControlNumber} in {doc.Stored.Base}.";
        if (warnings > 0) msg += $" {warnings} warning(s) — Ctrl+W to review.";
        if (result.RippledFields > 0) msg += $" Rippled into {result.RippledFields} linked bib field(s).";
        SetMessage(msg + mirrorNote);
    }

    /// <summary>Fills and shows the findings list (errors first), or hides it when
    /// there is nothing to report. Each row remembers its field ref for jumping.</summary>
    private void ShowFindings(IReadOnlyList<ValidationFinding> findings)
    {
        _findings.BeginUpdate();
        _findings.Items.Clear();
        foreach (var f in findings.OrderBy(f => f.IsError ? 0 : 1))
        {
            var item = new ListViewItem(f.IsError ? "Error" : "Warning")
            {
                ForeColor = f.IsError ? Color.Firebrick : Color.DarkGoldenrod,
                Tag = f.Ref,
            };
            item.SubItems.Add(FindingFieldLabel(f.Ref));
            item.SubItems.Add(f.Message);
            _findings.Items.Add(item);
        }
        _findings.EndUpdate();
        _findings.Visible = findings.Count > 0;
    }

    private void ClearFindings()
    {
        _findings.Items.Clear();
        _findings.Visible = false;
    }

    private static string FindingSummary(IReadOnlyList<ValidationFinding> findings)
    {
        int errors = findings.Count(f => f.IsError);
        int warnings = findings.Count - errors;
        var parts = new List<string>();
        if (errors > 0) parts.Add($"{errors} error(s)");
        if (warnings > 0) parts.Add($"{warnings} warning(s)");
        return parts.Count == 0 ? "No problems" : string.Join(", ", parts);
    }

    /// <summary>The "Field" column text for a finding — its tag (with subfield code
    /// when the ref is that precise), "Leader", or blank for a record-level rule.</summary>
    private string FindingFieldLabel(FieldRef? @ref)
    {
        if (@ref is not FieldRef r || _currentDoc is null) return "";
        if (r.FieldIndex < 0) return "Leader";
        if (r.FieldIndex >= _currentDoc.Record.Fields.Count) return "";
        var field = _currentDoc.Record.Fields[r.FieldIndex];
        if (r.SubfieldIndex >= 0 && r.SubfieldIndex < field.Subfields.Count)
            return $"{field.Tag} ‡{field.Subfields[r.SubfieldIndex].Code}";
        return field.Tag;
    }

    /// <summary>Click/Enter on a finding: move the cursor to the offending place
    /// (docs/PLAN.md §5). Record-level findings (no ref) simply do nothing.</summary>
    private void JumpToSelectedFinding()
    {
        if (_currentDoc is null || _findings.SelectedItems.Count == 0) return;
        if (_findings.SelectedItems[0].Tag is not FieldRef r) return;

        ShowRecordView();
        foreach (DataGridViewRow gridRow in _viewer.Rows)
        {
            if (gridRow.Tag is not DisplayRow d || d.FieldIndex != r.FieldIndex) continue;
            if (r.SubfieldIndex >= 0 && d.SubfieldIndex != r.SubfieldIndex) continue;
            _viewer.CurrentCell = gridRow.Cells["value"];
            _viewer.Focus();
            return;
        }
    }

    // ---------- MARC output folder ----------

    private const string MarcOutSetting = "marc_out_folder";
    private const string MarcOutSettingAut = "marc_out_folder_aut";

    /// <summary>The settings key and default subfolder for a base. BIB and AUT
    /// mirror to separate folders (user request 2026-08-01) — same mechanism as
    /// bib, its own chosen folder — because their control numbers are numbered
    /// independently and would otherwise collide (BIB 758.mrk vs AUT 758.mrk).</summary>
    private static (string Key, string DefaultName) MarcOutSpec(string @base) => @base == "AUT"
        ? (MarcOutSettingAut, RecordMirror.DefaultFolderNameAut)
        : (MarcOutSetting, RecordMirror.DefaultFolderName);

    /// <summary>The folder a base's pushed records are mirrored to: the
    /// cataloguer's chosen folder (persisted per catalogue) if set, else null —
    /// meaning no .mrk is written and the record lives only in the .db (user
    /// request 2026-08-01: an unset folder mirrors nothing, rather than defaulting
    /// to a MARC_OUT subfolder beside the .db).</summary>
    private string? MarcOutFolder(string @base)
    {
        var (key, _) = MarcOutSpec(@base);
        string? configured = _repo?.GetSetting(key);
        return string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
    }

    /// <summary>File → Set BIB / Authority Output Folder: pick the folder Apud
    /// writes each pushed record's .mrk into for that base. Stored in the
    /// catalogue's settings (remembered per catalogue). Cancelling leaves the
    /// current choice; picking the same place is idempotent.</summary>
    /// <summary>File → Set Organization Code: the MARC org code Apud stamps into 003
    /// on push (per-catalogue, stored in the setting table). A per-catalogue constant,
    /// not per-record content — the one org-level value Apud fills for you. Blank turns
    /// 003 auto-fill back off. This is a single focused command, not a Settings dialog
    /// (that was cut Module 10); it mirrors the Set … Output Folder commands.</summary>
    private void SetOrgCode()
    {
        if (!RequireCatalogue()) return;

        string current = _repo.GetSetting("org_code") ?? "";
        if (PromptForText(
                "Set Organization Code",
                "MARC organization code for field 003. Leave blank for none.",
                current) is not string entered)
            return; // cancelled, nothing changes

        string code = entered.Trim();
        _repo.SetSetting("org_code", code);
        SetMessage(code.Length > 0
            ? $"Organization code set to {code}."
            : "Organization code cleared.");
    }

    /// <summary>A minimal one-line text prompt (WinForms ships no InputBox). Returns the
    /// entered text on OK, or null on Cancel. Built inline, Aleph-plain.</summary>
    private string? PromptForText(string title, string prompt, string initial, bool mask = false)
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

        return dialog.ShowDialog(this) == DialogResult.OK ? box.Text : null;
    }

    private void SetMarcOutFolder(string @base)
    {
        if (!RequireCatalogue()) return;

        var (key, defaultName) = MarcOutSpec(@base);
        string? current = _repo.GetSetting(key);
        using var dialog = new FolderBrowserDialog
        {
            Description = $"Choose the folder Apud writes each pushed {@base} record to (as <001>.mrk).",
            UseDescriptionForTitle = true,
            SelectedPath = !string.IsNullOrWhiteSpace(current) && Directory.Exists(current)
                ? current
                : RecordMirror.DefaultFolderFor(_catalogPath, defaultName) is string d && Directory.Exists(d) ? d : "",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _repo.SetSetting(key, dialog.SelectedPath);
        SetMessage($"Pushed {@base} records will be written to {dialog.SelectedPath}.");
    }

    // ---------- server backup / publish (Module 11, docs/PLAN.md §9b) ----------

    /// <summary>File → Backup Server → Set Server: the per-catalogue backup target.</summary>
    private void ConfigureSync()
    {
        if (!RequireCatalogue()) return;

        using var form = new SyncSettingsForm(SyncSettings.Load(_repo));
        if (form.ShowDialog(this) != DialogResult.OK) return;

        form.Result.Save(_repo);
        SetMessage(form.Result.IsConfigured
            ? $"Server set: {form.Result.User}@{form.Result.Host}:{form.Result.Port} → {form.Result.RemoteRoot}."
            : "Server settings saved — host, user and private key are still needed before a backup.");
    }

    /// <summary>Builds a SyncService whose transport connects with the session
    /// passphrase. A fresh transport per operation (the factory runs each call).</summary>
    private static SyncService SyncServiceFor(SyncSettings settings, string? passphrase) =>
        new(() =>
        {
            var transport = new SshNetSftpTransport(settings, passphrase);
            transport.Connect();
            return transport;
        });

    /// <summary>Asks for the key passphrase (masked, blank allowed). Null = cancelled.</summary>
    private string? AskPassphrase(SyncSettings settings) =>
        PromptForText("Private Key Passphrase",
            $"Passphrase for {Path.GetFileName(settings.KeyPath)} — leave blank if the key has none.",
            "", mask: true);

    /// <summary>File → Backup Server → Back Up to Server: VACUUM-INTO snapshot + latest/ refresh,
    /// uploaded atomically, then old snapshots pruned to the keep-N.</summary>
    private void UploadToServer()
    {
        if (!RequireCatalogue() || _db is null) return;

        var settings = SyncSettings.Load(_repo);
        if (!settings.IsConfigured)
        {
            SetMessage("Configure the server first — File → Backup Server → Set Server.");
            return;
        }

        if (AskPassphrase(settings) is not string passphrase) return; // cancelled

        SetMessage($"Backing up to {settings.Host}…");
        _messageBar.Refresh();
        Cursor.Current = Cursors.WaitCursor;
        try
        {
            var service = SyncServiceFor(settings, passphrase);
            var source = new DbSnapshotSource(_db, _repo, settings.UploadExport);
            var result = service.Upload(source, settings, DateTime.UtcNow);

            // Trust-on-first-use: pin the fingerprint the first time we see it.
            if (settings.HostFingerprint is null && result.SeenFingerprint is not null)
            {
                settings.HostFingerprint = result.SeenFingerprint;
                settings.Save(_repo);
            }

            _pushesSinceSync = 0;
            string exports = result.Exports.Count > 0 ? $" + {string.Join(" + ", result.Exports)}" : "";
            string pruned = result.Pruned > 0 ? $" Pruned {result.Pruned} old snapshot(s)." : "";
            SetMessage($"Backed up {Path.GetFileName(result.RemoteSnapshot)}{exports} → {settings.RemoteRoot}.{pruned}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Backup failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetMessage("Backup failed.");
        }
        finally { Cursor.Current = Cursors.Default; }
    }

    /// <summary>File → Backup Server → Restore from Server: pick a server snapshot, download it
    /// to a local file, and (optionally) open that copy side-by-side. The working
    /// catalogue is never overwritten — restoring is opening a downloaded copy, a
    /// conscious act, not an automatic replace.</summary>
    private void RestoreFromServer()
    {
        if (!RequireCatalogue()) return;

        var settings = SyncSettings.Load(_repo);
        if (!settings.IsConfigured)
        {
            SetMessage("Configure the server first — File → Backup Server → Set Server.");
            return;
        }

        if (AskPassphrase(settings) is not string passphrase) return;

        IReadOnlyList<SnapshotInfo> snapshots;
        Cursor.Current = Cursors.WaitCursor;
        try
        {
            snapshots = SyncServiceFor(settings, passphrase).ListSnapshots(settings);
        }
        catch (Exception ex)
        {
            Cursor.Current = Cursors.Default;
            MessageBox.Show(this, ex.Message, "Restore", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally { Cursor.Current = Cursors.Default; }

        if (snapshots.Count == 0) { SetMessage("No snapshots on the server yet."); return; }

        if (PickSnapshot(snapshots) is not string chosen) return;

        using var save = new SaveFileDialog
        {
            Title = "Save downloaded snapshot as",
            InitialDirectory = StartFolder(),
            FileName = chosen,
            Filter = "Apud catalogue (*.db)|*.db",
            OverwritePrompt = true,
        };
        if (save.ShowDialog(this) != DialogResult.OK) return;
        RememberFolder(Path.GetDirectoryName(save.FileName));

        Cursor.Current = Cursors.WaitCursor;
        try
        {
            SyncServiceFor(settings, passphrase).Download(settings, chosen, save.FileName);
        }
        catch (Exception ex)
        {
            Cursor.Current = Cursors.Default;
            MessageBox.Show(this, ex.Message, "Restore", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        finally { Cursor.Current = Cursors.Default; }

        if (MessageBox.Show(this,
                $"Downloaded {chosen}.\n\nOpen it now as a separate catalogue? Your current catalogue is left untouched.",
                "Restore", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            OpenCatalog(save.FileName);
        else
            SetMessage($"Snapshot saved to {save.FileName}.");
    }

    /// <summary>A plain list picker for the server snapshots (newest first).</summary>
    private string? PickSnapshot(IReadOnlyList<SnapshotInfo> snapshots)
    {
        using var dialog = new Form
        {
            Text = "Restore from Server",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(420, 320),
        };
        var list = new ListBox { Location = new Point(14, 14), Size = new Size(392, 250), Font = new Font("Consolas", 9f) };
        foreach (var s in snapshots)
            list.Items.Add($"{s.Name}   ({s.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss})");
        list.SelectedIndex = 0;
        var ok = new Button { Text = "Download", DialogResult = DialogResult.OK, Size = new Size(100, 28), Location = new Point(202, 276) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(90, 28), Location = new Point(316, 276) };
        list.DoubleClick += (_, _) => { if (list.SelectedIndex >= 0) { dialog.DialogResult = DialogResult.OK; dialog.Close(); } };
        dialog.Controls.Add(list);
        dialog.Controls.Add(ok);
        dialog.Controls.Add(cancel);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;

        return dialog.ShowDialog(this) == DialogResult.OK && list.SelectedIndex >= 0
            ? snapshots[list.SelectedIndex].Name
            : null;
    }

    /// <summary>Deletes the displayed record from the catalogue AND its
    /// &lt;001&gt;.mrk file (user request). Irreversible, so it confirms
    /// first; a linked authority record is refused (repo.Delete guard) so
    /// authority control never dangles. Authority links key off the internal
    /// record id, not the 001, so removing a record never breaks other records.</summary>
    private void DeleteRecord()
    {
        if (!RequireCatalogue()) return;
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        var doc = _currentDoc;

        if (doc.Stored.Id == 0)
        {
            SetMessage("This record was never saved — use Remove in the sidebar to close it.");
            return;
        }

        string cn = doc.Record.ControlNumber ?? "(no 001)";
        if (MessageBox.Show(this,
                $"Delete record {cn} from {doc.Stored.Base}?\n\n" +
                $"This removes it from the catalogue and deletes its {cn}.mrk " +
                "file from the MARC output folder, if present. This cannot be undone.",
                "Delete Record", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        try
        {
            _repo.Delete(doc.Stored.Id);
        }
        catch (InvalidOperationException ex) // the refuse-delete guard for a linked authority
        {
            MessageBox.Show(this, ex.Message, "Delete Record",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try { RecordMirror.Delete(MarcOutFolder(doc.Stored.Base), doc.Record.ControlNumber); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetMessage($"Record {cn} deleted from the catalogue, but its .mrk file could not be removed: {ex.Message}");
            CloseOpenRecord(doc);
            return;
        }

        CloseOpenRecord(doc);
        SetMessage($"Deleted record {cn} from the catalogue and its .mrk file.");
    }

    /// <summary>Closes a record's sidebar entry and viewer after it is gone from
    /// the catalogue (no dirty prompt — it no longer exists to save).</summary>
    private void CloseOpenRecord(EditorDocument doc)
    {
        foreach (ListViewItem item in _openList.Items.Cast<ListViewItem>().ToList())
            if (ReferenceEquals(item.Tag, doc))
            {
                _openList.Items.Remove(item);
                break;
            }
        if (ReferenceEquals(_currentDoc, doc)) ClearViewer();
        ClearFindings();
    }

    // ---------- new record / save (Module 6 steps 4-6) ----------

    private static string TemplatesFolder => Path.Combine(AppContext.BaseDirectory, "templates");

    /// <summary>Ctrl+N, context-sensitive per §6.2: standing in a record copies
    /// it as a new draft (001 cleared, sequence fills at push); otherwise a new
    /// record from a template.</summary>
    private void NewRecord()
    {
        if (!RequireCatalogue()) return;

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
        if (!RequireCatalogue()) return;
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

    /// <summary>File → Import Records: one menu entry that first asks whether to
    /// import a single file or a whole folder, then routes to the matching picker
    /// (user request 2026-08-01 — the two former commands collapsed into one, the
    /// scope chosen inside Apud rather than by which menu item was clicked).</summary>
    private void ImportRecords()
    {
        if (!RequireCatalogue()) return;
        switch (ChooseImportSource())
        {
            case ImportSource.File: ImportFiles(); break;
            case ImportSource.Folder: ImportFolder(); break;
            // Cancel: do nothing.
        }
    }

    private enum ImportSource { Cancel, File, Folder }

    /// <summary>A tiny two-button chooser (single file vs. whole folder), in the
    /// app's inline-dialog style (cf. PickSnapshot). Esc / Cancel = do nothing.</summary>
    private ImportSource ChooseImportSource()
    {
        using var dialog = new Form
        {
            Text = "Import Records",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(372, 150),
        };
        var prompt = new Label
        {
            Text = "Import a single file, or every .mrk in a folder?",
            Location = new Point(16, 18),
            Size = new Size(340, 22),
        };
        var file = new Button { Text = "Single &File...", Size = new Size(160, 38), Location = new Point(16, 52) };
        var folder = new Button { Text = "Whole F&older...", Size = new Size(160, 38), Location = new Point(196, 52) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(90, 28), Location = new Point(266, 110) };

        var result = ImportSource.Cancel;
        file.Click += (_, _) => { result = ImportSource.File; dialog.Close(); };
        folder.Click += (_, _) => { result = ImportSource.Folder; dialog.Close(); };

        dialog.Controls.Add(prompt);
        dialog.Controls.Add(file);
        dialog.Controls.Add(folder);
        dialog.Controls.Add(cancel);
        dialog.CancelButton = cancel;

        dialog.ShowDialog(this);
        return result;
    }

    /// <summary>Pick one or more .mrk files (e.g. a single authority file MarcEdit
    /// converted into your Downloads) and import just those. Each record routes to
    /// BIB or AUT by its leader, same as a folder import.</summary>
    private void ImportFiles()
    {
        if (!RequireCatalogue()) return;

        using var dialog = new OpenFileDialog
        {
            Title = "Import Records",
            InitialDirectory = StartFolder(),
            Filter = "MARC text (*.mrk)|*.mrk|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var files = dialog.FileNames;
        RememberFolder(Path.GetDirectoryName(files[0]));
        string source = files.Length == 1 ? files[0] : $"{files.Length} files";
        RunImport(source, new ImportEngine(_repo).Analyze(files));
    }

    /// <summary>Import every .mrk in a folder tree (reached from the Import
    /// Records chooser).</summary>
    private void ImportFolder()
    {
        if (!RequireCatalogue()) return;

        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose a folder; every .mrk file in it (and its subfolders) will be imported.",
            UseDescriptionForTitle = true,
            SelectedPath = StartFolder(),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        RememberFolder(dialog.SelectedPath);
        RunImport(dialog.SelectedPath, new ImportEngine(_repo).AnalyzeFolder(dialog.SelectedPath));
    }

    /// <summary>The shared analyze → wizard → commit path for both import
    /// commands. The commit is one all-or-nothing transaction; Cancel writes
    /// nothing.</summary>
    private void RunImport(string source, ImportPlan plan)
    {
        var report = plan.Report;
        if (report.TotalRecords == 0)
        {
            MessageBox.Show(this, "No records found (no .mrk files, or all empty).",
                "Import", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var wizard = new ImportWizardForm(source, report);
        if (wizard.ShowDialog(this) != DialogResult.OK) return; // nothing committed

        try
        {
            var result = new ImportEngine(_repo!).Commit(plan, wizard.SelectedMode);
            SetMessage($"Imported {result.RecordsImported} record(s) — BIB {result.BibCount}, AUT {result.AutCount}.");
        }
        catch (Microsoft.Data.Sqlite.SqliteException e)
        {
            // e.g. a record inserted between Analyze and Commit now collides;
            // the transaction rolled back — the catalogue is untouched.
            MessageBox.Show(this, $"Import failed and nothing was committed.\n\n{e.Message}",
                "Import", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------- export ----------

    private void ExportBase()
    {
        if (!RequireCatalogue()) return;
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
        if (!RequireCatalogue()) return;
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
            InitialDirectory = StartFolder(),
            FileName = suggestedName,
            Filter = "MARC text (*.mrk)|*.mrk",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        RememberFolder(Path.GetDirectoryName(dialog.FileName));
        int count = write(dialog.FileName);
        SetMessage($"Exported {count} record(s) to {dialog.FileName}.");
    }

    private void SetMessage(string text) => _messageLabel.Text = text;

    /// <summary>Guard for the many commands that need an open catalogue. With none
    /// open it shows a clear popup and returns false so the caller bails out — the
    /// message-bar note alone was easy to miss, so a command like Set Server or Set
    /// Organization Code just looked broken (user report 2026-08-01).</summary>
    [MemberNotNullWhen(true, nameof(_repo))]
    private bool RequireCatalogue()
    {
        if (_repo is not null) return true;
        MessageBox.Show(this,
            "First open a catalogue.\n\nUse File → New Catalogue to create one, " +
            "or File → Open Catalogue to open an existing one.",
            "No catalogue open", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return false;
    }
}
