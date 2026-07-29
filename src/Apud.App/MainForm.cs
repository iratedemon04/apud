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
    private readonly SplitContainer _split;
    private readonly ListView _navList;
    private readonly Label _recordHeader;
    private readonly DataGridView _viewer;

    private readonly AppSettings _settings = AppSettings.Load(AppSettings.DefaultFilePath);

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

        // ----- navigation pane -----
        _navList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
        };
        _navList.Columns.Add("001", 70);
        _navList.Columns.Add("Title", 250);
        _navList.Columns.Add("Status", 70);
        _navList.SelectedIndexChanged += (_, _) => ShowSelectedRecord();

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
        _split.Panel2.Controls.Add(rightPanel);

        // ----- message bar -----
        _messageLabel = new ToolStripStatusLabel("Ready.") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _messageBar = new StatusStrip();
        _messageBar.Items.Add(_messageLabel);

        Controls.Add(_split);
        Controls.Add(_messageBar);
        Controls.Add(_menu);
        MainMenuStrip = _menu;

        Load += (_, _) => OpenLastCatalog();
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

    private void OpenLastCatalog()
    {
        if (_settings.LastCatalogPath is string last && File.Exists(last))
            OpenCatalog(last);
        else
            SetMessage("No catalogue open — File → New Catalogue or Open Catalogue.");
    }

    private void NewCatalog()
    {
        Directory.CreateDirectory(AppSettings.DefaultCatalogFolder);
        using var dialog = new SaveFileDialog
        {
            Title = "New Catalogue",
            InitialDirectory = AppSettings.DefaultCatalogFolder,
            FileName = "catalog.db",
            Filter = "Apud catalogue (*.db)|*.db",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        // The dialog already asked about overwriting; a stale file must not be
        // silently opened as an existing catalogue.
        if (File.Exists(dialog.FileName)) File.Delete(dialog.FileName);
        OpenCatalog(dialog.FileName);
    }

    private void OpenCatalogDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Open Catalogue",
            InitialDirectory = Directory.Exists(AppSettings.DefaultCatalogFolder)
                ? AppSettings.DefaultCatalogFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Filter = "Apud catalogue (*.db)|*.db|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            OpenCatalog(dialog.FileName);
    }

    private void OpenCatalog(string path)
    {
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
        _settings.LastCatalogPath = path;
        _settings.Save(AppSettings.DefaultFilePath);
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

        // Plain import (5b): any error aborts; the full report grid with
        // record-level choices is the 5c wizard.
        if (!report.CanCommitAsPushed)
        {
            MessageBox.Show(this, BuildErrorText(report), "Import blocked",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        int warnings = report.Files.Sum(f => f.Diagnostics.Count);
        string summary =
            $"{report.Files.Count} file(s), {report.TotalRecords} record(s), " +
            $"{warnings} warning(s), no errors.\n\n" +
            "Yes — import as PUSHED (trusted migration; records enter search)\n" +
            "No — import as DRAFTS (records stay out of search until pushed)";
        var choice = MessageBox.Show(this, summary, "Import Folder",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (choice == DialogResult.Cancel) return;

        var result = engine.Commit(plan,
            choice == DialogResult.Yes ? ImportMode.AsPushed : ImportMode.AsDrafts);

        RefreshNav();
        SetMessage($"Imported {result.RecordsImported} record(s) — BIB {result.BibCount}, AUT {result.AutCount}.");
    }

    private static string BuildErrorText(ImportReport report)
    {
        var lines = new List<string> { "The import was NOT run. Problems found:", "" };
        foreach (var e in report.RunErrors)
            lines.Add("• " + e);
        foreach (var f in report.Files.Where(f => f.Diagnostics.Count > 0))
        {
            lines.Add(Path.GetFileName(f.FilePath) + ":");
            lines.AddRange(f.Diagnostics.Select(d => "   " + d));
        }
        if (lines.Count > 32)
        {
            int extra = lines.Count - 32;
            lines = lines.Take(32).ToList();
            lines.Add($"... and {extra} more line(s).");
        }
        return string.Join("\n", lines);
    }

    private void SetMessage(string text) => _messageLabel.Text = text;
}
