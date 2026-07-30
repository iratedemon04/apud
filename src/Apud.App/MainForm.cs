using Apud.Data;

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

    private ApudDatabase? _db;
    private RecordRepository? _repo;
    private bool _syncingBase;

    private string CurrentBase => _searchBase.SelectedIndex == 1 ? "AUT" : "BIB";

    public MainForm()
    {
        Text = "Apud";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(950, 620);

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
        file.DropDownItems.Add(new ToolStripMenuItem("&Import Folder...", null, (_, _) => ImportFolder()));
        file.DropDownItems.Add(new ToolStripMenuItem("Export &Base...", null, (_, _) => ExportBase()));
        file.DropDownItems.Add(new ToolStripMenuItem("Export &Selected...", null, (_, _) => ExportSelected()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (_, _) => Close())
        {
            ShortcutKeys = Keys.Alt | Keys.F4
        });

        var @base = new ToolStripMenuItem("&Base");
        _bibItem = new ToolStripMenuItem("&BIB — Bibliographic", null, (_, _) => SetBase("BIB")) { Checked = true };
        _autItem = new ToolStripMenuItem("&AUT — Authority", null, (_, _) => SetBase("AUT"));
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
        openMenu.Items.Add("Remove All", null, (_, _) => { _openList.Items.Clear(); ClearViewer(); });
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
        _resultsList.Columns.Add("Title", 420);
        _resultsList.Columns.Add("Status", 70);
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
            CellBorderStyle = DataGridViewCellBorderStyle.None,
            ColumnHeadersVisible = false,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
        };
        _viewer.DefaultCellStyle.Padding = new Padding(0);
        _viewer.DefaultCellStyle.SelectionBackColor = Color.FromArgb(225, 238, 250);
        _viewer.DefaultCellStyle.SelectionForeColor = Color.Black;
        _viewer.RowTemplate.Height = 17;

        var mono = new Font("Consolas", 9.75f);
        _viewer.Columns.Add(NewColumn("name", 140, italic: true, color: Color.Maroon,
            font: new Font("Segoe UI", 8.25f)));
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
        Load += (_, _) => SetMessage("No catalogue open — File → New Catalogue or Open Catalogue.");
        FormClosed += (_, _) => _db?.Dispose();
    }

    private static DataGridViewTextBoxColumn NewColumn(
        string name, int width, bool italic = false, bool bold = false, bool underline = false,
        Color? color = null, Font? font = null)
    {
        var col = new DataGridViewTextBoxColumn { Name = name, Width = width, ReadOnly = true };
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
        if (_resultsList.SelectedItems.Count == 0) return;
        var s = (RecordSummary)_resultsList.SelectedItems[0].Tag!;

        // Already open? Select it instead of duplicating.
        foreach (ListViewItem existing in _openList.Items)
        {
            if ((long)existing.Tag! == s.Id)
            {
                existing.Selected = true;
                ShowRecordView();
                return;
            }
        }

        var item = new ListViewItem(s.Base);
        item.SubItems.Add(s.ControlNumber ?? "");
        item.SubItems.Add(s.Title);
        item.Tag = s.Id;
        _openList.Items.Add(item);
        _openList.SelectedItems.Clear();
        item.Selected = true; // triggers ShowSelectedOpenRecord → record view
    }

    private void ShowSelectedOpenRecord()
    {
        if (_repo is null || _openList.SelectedItems.Count == 0) return;

        var stored = _repo.Load((long)_openList.SelectedItems[0].Tag!);
        if (stored is null) { ClearViewer(); return; }

        _recordHeader.Text = RecordDisplay.HeaderText(stored.Base, stored.Record.ControlNumber, stored.Record);
        _viewer.Rows.Clear();
        foreach (var row in RecordDisplay.Build(stored.Record))
            _viewer.Rows.Add(row.FieldName, row.Tag, row.Indicators, row.Code, row.Value);
        _viewer.ClearSelection();
        ShowRecordView();
    }

    private void RemoveSelectedOpenRecords()
    {
        foreach (ListViewItem item in _openList.SelectedItems.Cast<ListViewItem>().ToList())
            _openList.Items.Remove(item);
        if (_openList.Items.Count == 0) ClearViewer();
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
        var ids = _openList.SelectedItems.Cast<ListViewItem>().Select(i => (long)i.Tag!).ToList();
        if (ids.Count == 0)
        {
            SetMessage("Select one or more records in the sidebar first.");
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
