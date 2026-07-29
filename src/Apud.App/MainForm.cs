using Apud.Data;

namespace Apud.App;

/// <summary>
/// Application shell (Module 5b): catalogue open/new, BIB/AUT base menu
/// (Aleph "Connect to..." style — radio check, not tabs), navigation pane
/// (001 / title / status), read-only record viewer per docs/ALEPH-WORKFLOW.md,
/// and plain File→Import Folder over the 5a engine. Editing arrives in Module 6;
/// search and the import wizard in 5c. Menus are still hand-built placeholders
/// until the command table / keymap engine exists (Module 6).
/// </summary>
public sealed class MainForm : Form
{
    private readonly MenuStrip _menu;
    private readonly StatusStrip _messageBar;
    private readonly ToolStripStatusLabel _messageLabel;
    private readonly ToolStripMenuItem _bibItem;
    private readonly ToolStripMenuItem _autItem;
    private readonly ToolStripMenuItem _importItem;
    private readonly ToolStripMenuItem _exportSelectedItem;
    private readonly SplitContainer _split;
    private readonly ListView _navList;
    private readonly Label _recordHeader;
    private readonly DataGridView _viewer;
    private readonly ComboBox _searchScope;
    private readonly TextBox _searchBox;
    private readonly ListView _historyList;
    private readonly SearchHistory _history = new();

    private static readonly (string Label, SearchScope Scope)[] Scopes =
    {
        ("All fields", SearchScope.All),
        ("Title", SearchScope.Title),
        ("Author", SearchScope.Author),
        ("Subjects", SearchScope.Subjects),
        ("Control No.", SearchScope.ControlNumber),
    };

    private ApudDatabase? _db;
    private RecordRepository? _repo;
    private string _base = "BIB";

    public MainForm()
    {
        Text = "Apud";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 600);

        // ----- menu -----
        _menu = new MenuStrip();

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(new ToolStripMenuItem("&New Catalogue...", null, (_, _) => NewCatalog())
        {
            ShortcutKeys = Keys.Control | Keys.N
        });
        file.DropDownItems.Add(new ToolStripMenuItem("&Open Catalogue...", null, (_, _) => OpenCatalogDialog())
        {
            ShortcutKeys = Keys.Control | Keys.O
        });
        file.DropDownItems.Add(new ToolStripSeparator());
        _importItem = new ToolStripMenuItem("&Import Folder...", null, (_, _) => ImportFolder());
        file.DropDownItems.Add(_importItem);
        file.DropDownItems.Add(new ToolStripMenuItem("Export &Base...", null, (_, _) => ExportBase()));
        _exportSelectedItem = new ToolStripMenuItem("Export &Selected...", null, (_, _) => ExportSelected());
        file.DropDownItems.Add(_exportSelectedItem);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (_, _) => Close())
        {
            ShortcutKeys = Keys.Alt | Keys.F4
        });

        var @base = new ToolStripMenuItem("&Base");
        _bibItem = new ToolStripMenuItem("&BIB — Bibliographic", null, (_, _) => SwitchBase("BIB")) { Checked = true };
        _autItem = new ToolStripMenuItem("&AUT — Authority", null, (_, _) => SwitchBase("AUT"));
        @base.DropDownItems.Add(_bibItem);
        @base.DropDownItems.Add(_autItem);

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(new ToolStripMenuItem("&About Apud", null, (_, _) =>
            MessageBox.Show(this,
                $"Apud {Application.ProductVersion}\nMARC21 original cataloguing.",
                "About Apud", MessageBoxButtons.OK, MessageBoxIcon.Information)));

        _menu.Items.Add(file);
        _menu.Items.Add(@base);
        _menu.Items.Add(help);

        // ----- search bar -----
        _searchScope = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 100,
        };
        foreach (var (label, _) in Scopes) _searchScope.Items.Add(label);
        _searchScope.SelectedIndex = 0;

        _searchBox = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right };
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { RunSearch(); e.SuppressKeyPress = true; }
        };
        var searchButton = new Button { Text = "Search", Width = 58 };
        searchButton.Click += (_, _) => RunSearch();
        var clearButton = new Button { Text = "Clear", Width = 48 };
        clearButton.Click += (_, _) => { _searchBox.Text = ""; RefreshNav(); };

        var searchPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 30,
            ColumnCount = 4,
            Padding = new Padding(2),
        };
        searchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        searchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        searchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        searchPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        searchPanel.Controls.Add(_searchScope, 0, 0);
        searchPanel.Controls.Add(_searchBox, 1, 0);
        searchPanel.Controls.Add(searchButton, 2, 0);
        searchPanel.Controls.Add(clearButton, 3, 0);

        // ----- navigation pane -----
        _navList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = true, // export-selected works off this selection
            HideSelection = false,
        };
        _navList.Columns.Add("001", 70);
        _navList.Columns.Add("Title", 250);
        _navList.Columns.Add("Status", 70);
        _navList.SelectedIndexChanged += (_, _) => ShowSelectedRecord();

        // ----- session search history (in-memory only; dies with the session) -----
        _historyList = new ListView
        {
            Dock = DockStyle.Bottom,
            Height = 130,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
        };
        _historyList.Columns.Add("Search history", 170);
        _historyList.Columns.Add("Scope", 75);
        _historyList.Columns.Add("Hits", 45, HorizontalAlignment.Right);
        _historyList.DoubleClick += (_, _) => RerunFromHistory();

        // ----- viewer -----
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

        _viewer = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            GridColor = Color.FromArgb(235, 235, 235),
            ColumnHeadersVisible = false,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
        };
        var mono = new Font("Consolas", 10f);
        _viewer.Columns.Add(NewColumn("name", 150, italic: true));
        _viewer.Columns.Add(NewColumn("tag", 45, color: Color.DarkRed, font: mono, bold: true));
        _viewer.Columns.Add(NewColumn("ind", 38, font: mono));
        _viewer.Columns.Add(NewColumn("code", 30, color: Color.DarkRed, font: mono, bold: true));
        var value = NewColumn("value", 200, font: mono);
        value.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        value.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _viewer.Columns.Add(value);

        var rightPanel = new Panel { Dock = DockStyle.Fill };
        rightPanel.Controls.Add(_viewer);
        rightPanel.Controls.Add(_recordHeader);

        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterDistance = 300,
            FixedPanel = FixedPanel.Panel1,
        };
        _split.Panel1.Controls.Add(_navList);
        _split.Panel1.Controls.Add(searchPanel);
        _split.Panel1.Controls.Add(_historyList);
        _split.Panel2.Controls.Add(rightPanel);

        // ----- message bar -----
        _messageLabel = new ToolStripStatusLabel("Ready.") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _messageBar = new StatusStrip();
        _messageBar.Items.Add(_messageLabel);

        Controls.Add(_split);
        Controls.Add(_messageBar);
        Controls.Add(_menu);
        MainMenuStrip = _menu;

        // Deliberately dumb (user decision, 2026-07-28): the app never reconnects
        // to a previous catalogue, remembers nothing between sessions, and creates
        // nothing on its own — cataloguers must consciously choose the database
        // every session, exactly like Aleph's Connect to...
        Load += (_, _) => SetMessage("No catalogue open — File → New Catalogue or Open Catalogue.");
        FormClosed += (_, _) => _db?.Dispose();
    }

    private static DataGridViewTextBoxColumn NewColumn(
        string name, int width, bool italic = false, bool bold = false, Color? color = null, Font? font = null)
    {
        var col = new DataGridViewTextBoxColumn { Name = name, Width = width, ReadOnly = true };
        var style = col.DefaultCellStyle;
        if (color is Color c) style.ForeColor = c;
        Font baseFont = font ?? new Font("Segoe UI", 9f);
        var flags = (italic ? FontStyle.Italic : FontStyle.Regular) | (bold ? FontStyle.Bold : FontStyle.Regular);
        style.Font = new Font(baseFont, flags);
        return col;
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

        Text = $"Apud — {path}";
        RefreshNav();
    }

    // ---------- base & navigation ----------

    private void SwitchBase(string @base)
    {
        _base = @base;
        _bibItem.Checked = @base == "BIB";
        _autItem.Checked = @base == "AUT";
        RefreshNav();
    }

    private void RefreshNav()
    {
        _navList.Items.Clear();
        ClearViewer();
        if (_repo is null) return;

        var list = _repo.List(_base);
        _navList.BeginUpdate();
        foreach (var s in list)
        {
            var item = new ListViewItem(s.ControlNumber ?? "");
            item.SubItems.Add(s.Title);
            item.SubItems.Add(s.Status == RecordStatus.Pushed ? "pushed" : "draft");
            item.Tag = s.Id;
            _navList.Items.Add(item);
        }
        _navList.EndUpdate();
        SetMessage($"{_base}: {list.Count} record{(list.Count == 1 ? "" : "s")}.");
    }

    // ---------- search ----------

    private void RunSearch()
    {
        if (_repo is null)
        {
            SetMessage("Open a catalogue first.");
            return;
        }
        string query = _searchBox.Text.Trim();
        if (query.Length == 0) return;

        var scope = Scopes[_searchScope.SelectedIndex].Scope;
        var ids = _repo.Search(_base, query, scope);

        _history.Add(new SearchHistoryEntry(query, scope, _base, ids.Count));
        RefreshHistoryList();

        var byId = _repo.List(_base).ToDictionary(s => s.Id);
        _navList.Items.Clear();
        ClearViewer();
        _navList.BeginUpdate();
        foreach (long id in ids) // rank order, best first
        {
            if (!byId.TryGetValue(id, out var s)) continue;
            var item = new ListViewItem(s.ControlNumber ?? "");
            item.SubItems.Add(s.Title);
            item.SubItems.Add(s.Status == RecordStatus.Pushed ? "pushed" : "draft");
            item.Tag = s.Id;
            _navList.Items.Add(item);
        }
        _navList.EndUpdate();
        SetMessage($"{ids.Count} hit(s) for \"{query}\" in {_base}. Clear returns to the full list.");
    }

    private void RefreshHistoryList()
    {
        _historyList.BeginUpdate();
        _historyList.Items.Clear();
        foreach (var e in _history.Entries)
        {
            var item = new ListViewItem(e.Query);
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
        if (e.Base != _base) SwitchBase(e.Base);
        _searchBox.Text = e.Query;
        _searchScope.SelectedIndex = Array.FindIndex(Scopes, s => s.Scope == e.Scope);
        RunSearch();
    }

    // ---------- export ----------

    private void ExportBase()
    {
        if (_repo is null) { SetMessage("Open a catalogue first."); return; }
        int count = _repo.List(_base).Count;
        if (count == 0) { SetMessage($"{_base} is empty — nothing to export."); return; }
        ExportTo($"{_base}.mrk", path =>
        {
            new ExportEngine(_repo).ExportBaseToFile(_base, path);
            return count;
        });
    }

    private void ExportSelected()
    {
        if (_repo is null) { SetMessage("Open a catalogue first."); return; }
        var ids = _navList.SelectedItems.Cast<ListViewItem>().Select(i => (long)i.Tag!).ToList();
        if (ids.Count == 0) { SetMessage("Select one or more records in the list first."); return; }
        ExportTo($"{_base}-selection.mrk", path =>
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

    private void ShowSelectedRecord()
    {
        if (_repo is null || _navList.SelectedItems.Count == 0) return;

        var stored = _repo.Load((long)_navList.SelectedItems[0].Tag!);
        if (stored is null) { ClearViewer(); return; }

        _recordHeader.Text = RecordDisplay.HeaderText(stored.Base, stored.Record.ControlNumber, stored.Record);
        _viewer.Rows.Clear();
        foreach (var row in RecordDisplay.Build(stored.Record))
            _viewer.Rows.Add(row.FieldName, row.Tag, row.Indicators, row.Code, row.Value);
        _viewer.ClearSelection();
    }

    private void ClearViewer()
    {
        _recordHeader.Text = "";
        _viewer.Rows.Clear();
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
            RefreshNav();
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

    private void SetMessage(string text) => _messageLabel.Text = text;
}
