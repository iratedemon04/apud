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
    private readonly Panel _recordView;
    private readonly SearchController _search;  // the whole Search screen (search bar + results + history + paging + base)

    private readonly Label _recordHeader;
    private readonly RecordGrid _grid = new();     // textbox-grid editor (replaces the DataGridView); inited here so the command lambdas can capture it
    private readonly ListView _findings;          // Ctrl+W/Ctrl+L output, click to jump

    private readonly CommandRegistry _commands = new();
    private readonly Keymap _keymap;
    private readonly AppState _appState = AppState.Load();
    private FieldHelpForm? _fieldHelp;   // the F1 help panel, created on first use, reused thereafter

    private ApudDatabase? _db;
    private RecordRepository? _repo;
    private DraftStore? _drafts;            // per-catalogue draft .mrk files (not in the DB)
    private string? _catalogPath;          // the open .db path; MARC_OUT sits beside it
    private EditorDocument? _currentDoc; // the record showing in the editor
    private MarcField? _fieldClipboard;      // Ctrl+T copy field / Alt+T paste
    private MarcSubfield? _subfieldClipboard; // Ctrl+S copy subfield / Alt+S paste
    private readonly SyncCoordinator _sync;  // server backup/restore + the pushes-since-sync counter

    private CommandContext ActiveContext =>
        _recordView.Visible ? CommandContext.Editor : CommandContext.Search;

    public MainForm()
    {
        Text = "Apud";
        var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("Apud.App.apud.ico");
        if (iconStream != null) Icon = new Icon(iconStream);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(950, 620);

        // Server backup/restore lives in its own collaborator (docs/MAINFORM-REFACTOR-PLAN.md
        // step 1). It gets only what it needs — never `this` — via lazy getters and callbacks;
        // the lambdas are deferred, so fields still null here (e.g. _messageBar) are fine.
        _sync = new SyncCoordinator(
            owner: this,
            requireCatalogue: RequireCatalogue,
            repo: () => _repo,
            db: () => _db,
            catalogPath: () => _catalogPath,
            promptText: PromptForText,
            setMessage: SetMessage,
            refreshStatus: () => _messageBar?.Refresh(), // assigned later in this ctor; the lambda only runs post-construction
            startFolder: StartFolder,
            rememberFolder: RememberFolder,
            openCatalogue: path => OpenCatalog(path));

        // The Search screen is its own collaborator too (docs/MAINFORM-REFACTOR-PLAN.md
        // step 2). Built early, before the command table binds to it — like _sync it
        // never gets `this`, only a repo getter, the catalogue guard, the message sink,
        // the "open this record id" bridge to the editor, and a callback to keep the
        // Base menu's checkmarks in step (deferred, so _bibItem/_autItem are fine null here).
        _search = new SearchController(
            repo: () => _repo,
            requireCatalogue: RequireCatalogue,
            setMessage: SetMessage,
            openRecordById: OpenRecordById,
            onBaseChanged: UpdateBaseChecks);

        // ----- command table (Module 6 step 1) -----
        // Menus render from this table and keymap.json binds against it.
        // Catalogue commands are menu-only (§6.2: record commands own the keyboard).
        _commands.Add(new Command { Id = "catalogue.new", Name = "&New Catalogue...", Execute = NewCatalog });
        _commands.Add(new Command { Id = "catalogue.open", Name = "&Open Catalogue...", DefaultKey = "Ctrl+O", Execute = OpenCatalogDialog });
        _commands.Add(new Command { Id = "catalogue.import", Name = "&Import Records...", DefaultKey = "Ctrl+I", Execute = ImportRecords });
        _commands.Add(new Command { Id = "catalogue.marc-out", Name = "Set &BIB Output Folder...", Execute = () => SetMarcOutFolder("BIB") });
        _commands.Add(new Command { Id = "catalogue.marc-out-aut", Name = "Set &Authority Output Folder...", Execute = () => SetMarcOutFolder("AUT") });
        _commands.Add(new Command { Id = "catalogue.org-code", Name = "Set &Organization Code...", Execute = SetOrgCode });
        _commands.Add(new Command { Id = "sync.configure", Name = "&Set Server...", Execute = _sync.Configure });
        _commands.Add(new Command { Id = "sync.upload", Name = "&Back Up to Server", DefaultKey = "Ctrl+U", Execute = _sync.Upload });
        _commands.Add(new Command { Id = "sync.restore", Name = "&Restore from Server...", Execute = _sync.Restore });
        _commands.Add(new Command { Id = "catalogue.export-base", Name = "Export &Base...", Execute = ExportBase });
        _commands.Add(new Command { Id = "catalogue.export-selected", Name = "Export &Selected...", Execute = ExportSelected });
        _commands.Add(new Command { Id = "app.exit", Name = "E&xit", DefaultKey = "Alt+F4", Execute = Close });
        _commands.Add(new Command { Id = "base.bib", Name = "&BIB — Bibliographic", Execute = () => _search.SetBase("BIB") });
        _commands.Add(new Command { Id = "base.aut", Name = "&AUT — Authority", Execute = () => _search.SetBase("AUT") });
        _commands.Add(new Command { Id = "base.toggle", Name = "&Switch Base (BIB ⇄ AUT)", DefaultKey = "Ctrl+B", Execute = () => _search.SetBase(_search.CurrentBase == "BIB" ? "AUT" : "BIB") });
        _commands.Add(new Command { Id = "search.focus", Name = "&Search", DefaultKey = "F2", Execute = ShowSearchView });
        _commands.Add(new Command { Id = "help.field", Name = "&Field Help", DefaultKey = "F1", Execute = ShowFieldHelp });
        _commands.Add(new Command { Id = "help.intro", Name = "&Getting Started", Execute = ShowIntro });
        _commands.Add(new Command { Id = "help.backup-time", Name = "Bac&kup Times", Execute = ShowBackupTime });
        _commands.Add(new Command { Id = "help.about", Name = "&About Apud", Execute = ShowAbout });
        // Editor commands (Module 6 steps 3-7). §6.2: record commands own the keyboard.
        _commands.Add(new Command { Id = "record.new", Name = "&New Record / Copy", DefaultKey = "Ctrl+N", Execute = NewRecord });
        _commands.Add(new Command { Id = "record.save-draft", Name = "&Save Draft", Context = CommandContext.Editor, DefaultKey = "Ctrl+D", Execute = SaveDraft });
        _commands.Add(new Command { Id = "record.save-template", Name = "Save as &Template...", Context = CommandContext.Editor, DefaultKey = "Ctrl+Shift+T", Execute = SaveTemplate });
        _commands.Add(new Command { Id = "record.undo", Name = "&Undo", Context = CommandContext.Editor, DefaultKey = "Ctrl+Z", Execute = UndoEdit });
        _commands.Add(new Command { Id = "record.redo", Name = "&Redo", Context = CommandContext.Editor, DefaultKey = "Ctrl+Y", Execute = RedoEdit });
        _commands.Add(new Command { Id = "field.edit", Name = "&Edit Field (cursor)", Context = CommandContext.Editor, DefaultKey = "Insert", Execute = BeginEditCurrentCell });
        _commands.Add(new Command { Id = "field.order", Name = "&Order Fields", Context = CommandContext.Editor, DefaultKey = "Enter", Execute = OrderFieldsCommand });
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
        // Close = remove from the open-records list (not destructive). Ctrl+Delete
        // closes the selected record(s), Ctrl+C closes them all; either warns first
        // if anything is unsaved, and Enter confirms (tasks #5, #6). Both Global so
        // they work from the editor or the search screen.
        _commands.Add(new Command { Id = "record.close", Name = "&Close Record", Execute = RemoveSelectedOpenRecords });
        _commands.Add(new Command { Id = "record.close-all", Name = "Close &All Records", DefaultKey = "Ctrl+X", Execute = RemoveAllOpenRecords });
        // Catalogue-delete is destructive, so it always confirms; on Ctrl+Delete now
        // (user, 2026-08-08 — close moved to Ctrl+C / Ctrl+Alt+C).
        _commands.Add(new Command { Id = "record.delete", Name = "&Delete Record/Draft...", Context = CommandContext.Editor, DefaultKey = "Ctrl+Delete", Execute = DeleteRecord });

        // View / Window — editor text zoom (rebindable, persisted). The chords use the
        // "Plus"/"Minus" aliases because "Ctrl++" can't be parsed (splitting on '+').
        _commands.Add(new Command { Id = "view.zoom-in", Name = "Zoom &In", Context = CommandContext.Editor, DefaultKey = "Ctrl+Plus", Execute = () => _grid.ZoomIn() });
        _commands.Add(new Command { Id = "view.zoom-out", Name = "Zoom &Out", Context = CommandContext.Editor, DefaultKey = "Ctrl+Minus", Execute = () => _grid.ZoomOut() });
        _commands.Add(new Command { Id = "view.zoom-reset", Name = "&Reset Zoom", Context = CommandContext.Editor, DefaultKey = "Ctrl+0", Execute = () => _grid.ZoomReset() });

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
        @base.DropDownItems.Add(new ToolStripSeparator());
        @base.DropDownItems.Add(MenuItem("base.toggle"));

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
        record.DropDownItems.Add(MenuItem("record.close"));
        record.DropDownItems.Add(MenuItem("record.close-all"));
        record.DropDownItems.Add(MenuItem("record.delete"));

        var window = new ToolStripMenuItem("&Window");
        window.DropDownItems.Add(MenuItem("view.zoom-in"));
        window.DropDownItems.Add(MenuItem("view.zoom-out"));
        window.DropDownItems.Add(MenuItem("view.zoom-reset"));

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(MenuItem("help.field"));
        help.DropDownItems.Add(MenuItem("help.intro"));
        help.DropDownItems.Add(MenuItem("help.backup-time"));
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(MenuItem("help.about"));

        _menu.Items.Add(file);
        _menu.Items.Add(@base);
        _menu.Items.Add(record);
        _menu.Items.Add(window);
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
        _openList.Columns.Add("Title", 130);
        _openList.Columns.Add("Status", 50);
        _openList.SelectedIndexChanged += (_, _) => ShowSelectedOpenRecord();
        // Close All is on Ctrl+X (record.close-all); Close Record is menu-only;
        // Ctrl+Delete deletes (record.delete). Bare Delete does nothing here.
        var openMenu = new ContextMenuStrip();
        openMenu.Items.Add("Close", null, (_, _) => RemoveSelectedOpenRecords());
        openMenu.Items.Add("Close All", null, (_, _) => RemoveAllOpenRecords());
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
        // (_grid is constructed at its field declaration so the zoom command lambdas
        // can capture it; here we only wire it up.)
        // Each committed edit refreshes the header/sidebar/dirty marker; a refused
        // edit (bad leader length, control/data boundary cross) shows its note.
        _grid.EditCommitted += (_, _) =>
        {
            if (_currentDoc is null) return;
            UpdateHeader();
            UpdateSidebarItem(_currentDoc);
        };
        _grid.Message += SetMessage;
        // Restore the persisted editor zoom (Ctrl++/Ctrl+-) and save each user change.
        _grid.FontScale = _appState.FontScale;
        _grid.ZoomChanged += (_, _) => { _appState.FontScale = _grid.FontScale; _appState.Save(); };

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
        _recordView.Controls.Add(_grid);
        _recordView.Controls.Add(_findings);
        _recordView.Controls.Add(_recordHeader);

        // ----- composition -----
        var rightPanel = new Panel { Dock = DockStyle.Fill };
        rightPanel.Controls.Add(_search.View);
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
            // Reopen the last catalogue on launch when it still exists (user,
            // 2026-08-08). A moved/deleted one is silently skipped; a corrupt one
            // reports through OpenCatalog's own dialog and leaves nothing open.
            if (!string.IsNullOrEmpty(_appState.LastCatalogue) && File.Exists(_appState.LastCatalogue))
                OpenCatalog(_appState.LastCatalogue);

            // Config diagnostics (bad keymap/profile entries) always win the message
            // bar; otherwise OpenCatalog's own "Catalogue open…" line stands, and only
            // when nothing opened do we prompt to open one.
            if (configReports.Count > 0)
                SetMessage(string.Join("  |  ", configReports));
            else if (_repo is null)
                SetMessage("No catalogue open — File → New Catalogue or Open Catalogue.");

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
        // Unsaved work is lost on close: a brand-new record never saved, or a record
        // with edits since its last save (both are Dirty — a never-saved record has
        // no saved signature). A SAVED, unedited draft is not dirty and reloads from
        // its file next time, so it does not warn. The Warning icon sounds the ding;
        // No/Cancel keeps Apud open so the user can Ctrl+D.
        int unsaved = _openList.Items.Cast<ListViewItem>()
            .Count(i => i.Tag is EditorDocument { Dirty: true });
        if (unsaved > 0 && MessageBox.Show(this,
                $"{unsaved} record(s) have unsaved changes that will be lost.\n\n" +
                "Save them as drafts (Ctrl+D) to keep them. Close Apud anyway?",
                "Unsaved drafts", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        // The "back up first?" prompt (and its push counter) belongs to the sync
        // collaborator; it returns false only when the user chose Cancel.
        if (!_sync.OfferBackupBeforeClose()) e.Cancel = true;
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
        _grid.Focus();
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

    /// <summary>Help → Backup Time: the first-backup notice + estimate table, viewable
    /// any time. Also shown once before a catalogue's first backup by the gate in
    /// <see cref="SyncCoordinator"/>.</summary>
    private void ShowBackupTime()
    {
        using var form = new BackupTimeForm(preBackup: false);
        form.ShowDialog(this);
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
        var ctx = ActiveContext;
        Keys chord = NormalizeChord(keyData);
        // A binding that resolves to field.order (Enter by default) fires even
        // while a grid cell is being typed in — the typing guard would otherwise
        // swallow it. This overrides the grid's default "commit and drop to the
        // row below": Enter orders the fields and keeps the cursor on the field
        // you were on (tasks 8, 17). Gated to the record grid so Enter in the
        // findings list / search box is untouched.
        if (ctx == CommandContext.Editor && _currentDoc is not null
            && _grid.EditorHasFocus
            && _keymap.Lookup(chord, ctx) == "field.order")
        {
            OrderFieldsCommand();
            return true;
        }
        if (ShouldDispatch(keyData) && _keymap.Lookup(chord, ctx) is string id)
        {
            _commands.Find(id)!.Execute();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Folds the physical variants of a chord onto the one form the keymap
    /// binds against, so a single binding fires however the key is actually produced:
    /// the numeric keypad +/-/0 map onto the main-row keys, and Shift is ignored on
    /// the +/= key (on most layouts '+' IS Shift+'=', so "Ctrl++" and "Ctrl+=" are the
    /// same intent — this is why Ctrl++ appeared dead while the menu worked).</summary>
    private static Keys NormalizeChord(Keys keyData)
    {
        Keys code = keyData & Keys.KeyCode;
        Keys mods = keyData & Keys.Modifiers;
        code = code switch
        {
            Keys.Add => Keys.Oemplus,
            Keys.Subtract => Keys.OemMinus,
            Keys.NumPad0 => Keys.D0,
            _ => code,
        };
        if (code == Keys.Oemplus) mods &= ~Keys.Shift;
        return code | mods;
    }

    /// <summary>While the cursor is in a text control, only modified chords and
    /// F-keys dispatch — a plain letter, digit, Del or Enter is typing, not a
    /// command (documented in keymap.json's header).</summary>
    private bool ShouldDispatch(Keys keyData)
    {
        if ((keyData & (Keys.Control | Keys.Alt)) != 0) return true;
        var code = keyData & Keys.KeyCode;
        if (code is >= Keys.F1 and <= Keys.F24) return true;
        // A grid box is a real TextBox, so the TextBoxBase test already routes plain
        // keystrokes to typing and modified/F-keys to commands.
        return FocusedControl() is not (TextBoxBase or ComboBox);
    }

    private Control? FocusedControl()
    {
        Control? c = ActiveControl;
        while (c is ContainerControl container && container.ActiveControl != null)
            c = container.ActiveControl;
        return c;
    }

    // ---------- base ----------

    /// <summary>Keeps the Base menu's BIB/AUT checkmarks in step with the active base.
    /// Passed to <see cref="SearchController"/>, which owns the base and calls this on
    /// every switch.</summary>
    private void UpdateBaseChecks(string @base)
    {
        _bibItem.Checked = @base == "BIB";
        _autItem.Checked = @base == "AUT";
    }

    // ---------- view switching ----------

    private void ShowSearchView()
    {
        _search.View.Visible = true;
        _recordView.Visible = false;
        _searchViewButton.Font = new Font(_searchViewButton.Font, FontStyle.Bold);
        _recordViewButton.Font = new Font(_recordViewButton.Font, FontStyle.Regular);
        _search.FocusSearchBox();
    }

    private void ShowRecordView()
    {
        _search.View.Visible = false;
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
        _drafts = new DraftStore(path);
        _catalogPath = path;

        // Drafts are app files now, not DB rows: purge any legacy status='draft'
        // rows an older build may have left (user, 2026-08-08 — "they don't exist
        // anymore"). Pushed records, the real catalogue, are untouched.
        _repo.DeleteAllDrafts();

        // A different catalogue means different record ids: everything on
        // screen belonged to the old one.
        _openList.Items.Clear();
        _search.Reset();
        ClearViewer();
        ShowSearchView();

        LoadDraftsIntoSidebar(); // drafts aren't searchable — reopen them so work-in-progress survives a restart

        // Remember it so Apud reopens it on next launch.
        _appState.LastCatalogue = path;
        _appState.Save();

        Text = $"Apud — {path}";
        int drafts = _openList.Items.Count;
        SetMessage($"Catalogue open — BIB: {_repo.Count("BIB")}, AUT: {_repo.Count("AUT")} record(s)."
            + (drafts > 0 ? $" {drafts} draft(s) reopened." : ""));
    }

    // ---------- open records (sidebar) ----------

    /// <summary>Opens a stored record into the sidebar by id — selecting it if it
    /// is already open rather than duplicating. Shared by result double-click and
    /// the single-record import "open it immediately" path (task 1).</summary>
    private void OpenRecordById(long id)
    {
        if (_repo is null) return;
        foreach (ListViewItem existing in _openList.Items)
        {
            if (existing.Tag is EditorDocument d && d.Stored.Id == id)
            {
                existing.Selected = true;
                ShowRecordView();
                return;
            }
        }
        var stored = _repo.Load(id);
        if (stored is null) return;
        AddToSidebar(new EditorDocument(stored));
    }

    /// <summary>Adds an open record to the sidebar and selects it (which shows
    /// it). The document lives on the list item; edits persist in memory while
    /// switching records, until saved or removed.</summary>
    private void AddToSidebar(EditorDocument doc)
    {
        _openList.Items.Add(MakeSidebarItem(doc));
        _openList.SelectedItems.Clear();
        _openList.Items[_openList.Items.Count - 1].Selected = true; // → ShowSelectedOpenRecord → record view
    }

    private ListViewItem MakeSidebarItem(EditorDocument doc)
    {
        var item = new ListViewItem(doc.Stored.Base);
        item.SubItems.Add(AccessionSlot(doc) ?? "");
        item.SubItems.Add(TitleOf(doc.Record));
        item.SubItems.Add(SidebarStatus(doc));
        item.Tag = doc;
        return item;
    }

    /// <summary>Repopulates the sidebar with every saved draft for this catalogue,
    /// read from its draft files. This is how an unfinished draft survives a close +
    /// reopen. Added quietly (no selection) so opening a catalogue still lands on the
    /// search view, not a record.</summary>
    private void LoadDraftsIntoSidebar()
    {
        if (_drafts is null) return;
        foreach (var (draftId, @base, record) in _drafts.LoadAll())
        {
            var doc = new EditorDocument(new StoredRecord(@base, record)) { DraftId = draftId };
            _openList.Items.Add(MakeSidebarItem(doc));
        }
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
        // Closing a record removes it from the workspace for good. A saved draft's
        // "workspace" is its .mrk file (the draft folder IS the reopen set), so closing
        // one must delete that file — otherwise it silently reloads next launch (user,
        // 2026-08-17: closed drafts kept coming back). Warn about anything that would be
        // lost: unsaved edits, and saved drafts whose files will be discarded.
        int dirty = items.Count(i => i.Tag is EditorDocument { Dirty: true });
        int savedDrafts = items.Count(i => i.Tag is EditorDocument { Dirty: false, DraftId: not null });

        var warn = new List<string>();
        if (dirty > 0) warn.Add($"{dirty} record(s) have unsaved changes that will be lost");
        if (savedDrafts > 0) warn.Add($"{savedDrafts} saved draft(s) will be discarded");
        if (warn.Count > 0 && MessageBox.Show(this,
                string.Join(", and ", warn) + ".\n\n" +
                "Closing a draft deletes its saved file, so it will not reopen. Remove anyway?",
                "Remove Records", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        foreach (var item in items)
        {
            if (item.Tag is EditorDocument { DraftId: { } draftId } doc)
            {
                _drafts?.Delete(draftId); // remove the persisted file so it won't reload
                doc.DraftId = null;
            }
            if (ReferenceEquals(item.Tag, _currentDoc)) _currentDoc = null;
            _openList.Items.Remove(item);
        }
        if (_currentDoc is null) ClearViewer();
    }

    private void ClearViewer()
    {
        _currentDoc = null;
        _recordHeader.Text = "";
        _grid.Clear();
    }

    private static string TitleOf(Marc.Core.MarcRecord record)
    {
        foreach (var tag in new[] { "245", "100", "110", "111", "130", "150", "151" })
        {
            var f = record.FieldsWithTag(tag).FirstOrDefault();
            if (f?.Subfields.Count > 0)
                // 245 shows its title proper ($a); an authority heading shows in full,
                // subfields joined by "--" (Física--Investigación), so subdivisions
                // aren't dropped (task 5).
                return tag == "245"
                    ? f.Subfields[0].Value
                    : string.Join("--", f.Subfields.Select(s => s.Value));
        }
        return "";
    }

    /// <summary>The sidebar's pushed/draft marker — a `*` prefix flags unsaved edits.
    /// Shown for every open record so an authority, too, reads its status (task 18).</summary>
    private static string SidebarStatus(EditorDocument doc)
    {
        string status = doc.Stored.Status == RecordStatus.Pushed ? "pushed" : "draft";
        return doc.Dirty ? "*" + status : status;
    }

    // ---------- editor (Module 6 steps 3-6) ----------

    /// <summary>Redraws the editor from the current document through the textbox
    /// grid. <paramref name="preservePosition"/> keeps the caret on the same
    /// element across the rebuild (a structural edit made while typing); switching
    /// to a different record passes false.</summary>
    private void RenderRecord(bool preservePosition = true)
    {
        if (_currentDoc is null) { _recordHeader.Text = ""; _grid.Clear(); return; }
        UpdateHeader();
        if (!ReferenceEquals(_grid.Document, _currentDoc))
            _grid.Document = _currentDoc; // switch record: sync + rebuild fresh
        else
            _grid.Rebuild(preserveFocus: preservePosition);
    }

    private void UpdateHeader()
    {
        if (_currentDoc is null) return;
        // An edited record with no accession number yet shows "***" in the number
        // slot; once pushed, its assigned 001 replaces it (task 15). HeaderText only
        // special-cases null, so passing a non-null "***" flows straight through.
        string? number = AccessionSlot(_currentDoc);
        _recordHeader.Text =
            RecordDisplay.HeaderText(_currentDoc.Stored.Base, number, _currentDoc.Record)
            + (_currentDoc.Dirty ? "  *" : "");
    }

    /// <summary>The accession-number display for a record: its 001 when it has one,
    /// otherwise "***" while it is being edited (a placeholder for the number it will
    /// earn on push), or null/blank when clean and unnumbered (task 15).</summary>
    private static string? AccessionSlot(EditorDocument doc)
    {
        var cn = doc.Record.ControlNumber;
        if (!string.IsNullOrEmpty(cn)) return cn;
        return doc.Dirty ? "***" : null;
    }

    private void UndoEdit()
    {
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _grid.CommitFocused(); // commit any in-progress edit so Ctrl+Z reverts it too
        if (!_currentDoc.Undo()) { SetMessage("Nothing to undo."); return; }
        RenderRecord();
        UpdateSidebarItem(_currentDoc);
    }

    private void RedoEdit()
    {
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _grid.CommitFocused();
        if (!_currentDoc.Redo()) { SetMessage("Nothing to redo."); return; }
        RenderRecord();
        UpdateSidebarItem(_currentDoc);
    }

    /// <summary>The field/subfield the caret is on, in model indices.</summary>
    private (int FieldIndex, int SubfieldIndex)? CurrentRef() => _grid.CurrentRef();

    /// <summary>Maps a legacy column name to a grid box part for focus calls.</summary>
    private static BoxPart PartOf(string column) => column switch
    {
        "tag" => BoxPart.Tag,
        "ind" => BoxPart.Ind,
        "code" => BoxPart.Code,
        _ => BoxPart.Value,
    };

    /// <summary>Puts the caret on a field/subfield element after a change — e.g. the
    /// field below after a delete (task 2a). Delegates to the grid's reliable focus.</summary>
    private void SelectCell(int fieldIndex, int subfieldIndex, string column) =>
        _grid.FocusElement(fieldIndex, subfieldIndex, PartOf(column));

    /// <summary>Lands the caret on a field's first row whatever the field's shape —
    /// the reliable "put me on this field" after a multi-field delete.</summary>
    private void SelectFieldRow(int fieldIndex, string column = "tag") =>
        _grid.FocusField(fieldIndex, PartOf(column));

    /// <summary>Insert: make sure the caret is in an editable box, dropped at the
    /// start of a value so a filled field is prepended to (task #3). In the textbox
    /// grid every box is always editable when focused, so there is no edit mode to
    /// enter — this just parks the caret.</summary>
    private void BeginEditCurrentCell()
    {
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _grid.FocusForEdit();
    }

    /// <summary>Enter: order the fields (stable sort by tag) and keep the caret on
    /// the field you were on — it follows to its new position instead of dropping to
    /// the row below (tasks 8, 17). Undoable in one step.</summary>
    private void OrderFieldsCommand()
    {
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _grid.CommitFocused(); // commit the box first (a just-typed tag counts)

        // Remember the field by REFERENCE (its index changes when fields move) plus
        // the part and subfield so the caret lands back on the same spot.
        var cur = _grid.CurrentElement();
        BoxPart part = cur?.Part ?? BoxPart.Tag;
        MarcField? field = cur is { FieldIndex: >= 0 } c ? _currentDoc.Record.Fields[c.FieldIndex] : null;
        int sub = cur?.SubfieldIndex ?? -1;

        bool moved = _currentDoc.OrderFields();
        // Only rebuild when the order actually changed — a no-op Enter shouldn't
        // redraw the whole record (the caret just stays put).
        if (moved)
        {
            RenderRecord(preservePosition: false);
            UpdateSidebarItem(_currentDoc);
            if (field is not null)
            {
                int idx = _currentDoc.Record.Fields.IndexOf(field);
                if (idx >= 0) _grid.FocusElement(idx, sub, part);
            }
        }
    }

    private void NewField()
    {
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _grid.CommitFocused();
        int after = CurrentRef()?.FieldIndex ?? _currentDoc.Record.Fields.Count - 1;
        int at = _currentDoc.InsertBlankFieldAfter(after);
        RenderRecord();
        // The rebuild is synchronous, so land in the new field's tag box at once —
        // no BeginInvoke race. The blank field is data-shaped, so type the tag then
        // Tab straight through indicators, code and value with no further rebuild.
        _grid.FocusField(at, BoxPart.Tag);
    }

    private void NewSubfield()
    {
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _grid.CommitFocused();
        if (CurrentRef() is not { } at || at.FieldIndex < 0)
        {
            SetMessage("Stand in a field first.");
            return;
        }
        var (index, error) = _currentDoc.InsertSubfieldAfter(at.FieldIndex, at.SubfieldIndex);
        if (error != null) { SetMessage(error); return; }
        RenderRecord();
        // Land in the new subfield's code box. Its default ‡a opens selected (grid's
        // micro-box rule), so typing "e" rewrites it to ‡e, then Tab into the body.
        _grid.FocusElement(at.FieldIndex, index, BoxPart.Code);
    }

    private void DeleteCurrentField()
    {
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _grid.CommitFocused();

        // If fields are selected (drag across rows, or Shift/Ctrl-click from any
        // box), delete all of them in one undoable step; otherwise delete the field
        // under the caret. The leader is never included.
        var selected = _grid.SelectedFieldIndices.Where(i => i >= 0).Distinct().OrderBy(i => i).ToList();
        if (selected.Count == 0 && CurrentRef() is { FieldIndex: >= 0 } at)
            selected.Add(at.FieldIndex);

        if (selected.Count == 0)
        {
            SetMessage("Stand in a field, or drag across fields to select several (the leader cannot be deleted).");
            return;
        }

        // No confirmation dialog (and so no system ding): a delete is one Ctrl+Z away.
        int land = selected[0]; // survivors shift up into the topmost deleted slot
        _currentDoc.DeleteFields(selected);
        RenderRecord();
        UpdateSidebarItem(_currentDoc);
        UpdateHeader();
        // Land on the BODY of the field that shifted up (task #7).
        if (_currentDoc.Record.Fields.Count > 0)
            SelectFieldRow(Math.Min(land, _currentDoc.Record.Fields.Count - 1), "value");
    }

    /// <summary>Ctrl+Shift+F5: same selection-aware delete as Ctrl+F5, kept as a
    /// separate binding for the bulk prune.</summary>
    private void DeleteSelectedFields() => DeleteCurrentField();

    private void DeleteCurrentSubfield()
    {
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _grid.CommitFocused();
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
        _grid.CommitFocused();
        if (CurrentRef() is not { } at || at.FieldIndex < 0)
        {
            SetMessage("Stand in a field first (the leader cannot be copied).");
            return;
        }
        _fieldClipboard = _currentDoc.CopyField(at.FieldIndex);
        SetMessage("Field copied.");
    }

    /// <summary>Alt+T: paste the copied field as a new field just below the cursor
    /// (a fresh clone each time). Never reorders — like every other editor edit,
    /// the cataloguer places it.</summary>
    private void PasteField()
    {
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        if (_fieldClipboard is null) { SetMessage("No field copied yet."); return; }
        _grid.CommitFocused();
        int after = CurrentRef()?.FieldIndex ?? _currentDoc.Record.Fields.Count - 1;
        int at = _currentDoc.PasteFieldAfter(after, _fieldClipboard);
        RenderRecord();
        UpdateSidebarItem(_currentDoc);
        UpdateHeader();
        // Land the caret IN the pasted field ready to edit (its first row whatever
        // its shape — data fields start at SubfieldIndex 0).
        _grid.FocusField(at, BoxPart.Tag);
    }

    /// <summary>Ctrl+S: copy the subfield under the cursor onto the subfield
    /// clipboard.</summary>
    private void CopySubfield()
    {
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _grid.CommitFocused();
        if (CurrentRef() is not { } at || at.FieldIndex < 0 || at.SubfieldIndex < 0)
        {
            SetMessage("Stand on a subfield first.");
            return;
        }
        _subfieldClipboard = _currentDoc.CopySubfield(at.FieldIndex, at.SubfieldIndex);
        SetMessage("Subfield copied.");
    }

    /// <summary>Alt+S: paste the copied subfield just after the cursor's subfield
    /// (or at the top of the field when standing on an empty one).</summary>
    private void PasteSubfield()
    {
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        if (_subfieldClipboard is null) { SetMessage("No subfield copied yet."); return; }
        _grid.CommitFocused();
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
        _grid.CommitFocused();
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
        _grid.CommitFocused();

        var doc = _currentDoc;
        if (doc.Stored.Base != "BIB")
        {
            SetMessage("Open a bibliographic record to link headings.");
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
            SetMessage($"{field.Tag} is not a controlled heading field.");
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

        using var form = new AuthorityBrowseForm(fieldText, initial, Position, _repo.AuthorizedDisplayFor);
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
        SetMessage("Heading linked.");
    }

    // ---------- validate + push (Ctrl+W / Ctrl+L, Module 9) ----------

    /// <summary>Ctrl+W: run the whole pipeline as a dry run — nothing is written.
    /// Errors and warnings both show in the findings list; a clean record just
    /// says so.</summary>
    /// <summary>Strips contentless fields from the record (task 17) and, if any
    /// went, redraws the grid and sidebar so the cleanup is visible before the
    /// findings are shown. The removal rides the undo stack (Ctrl+Z brings them
    /// back).</summary>
    private void StripEmptyFieldsAndRefresh(EditorDocument doc)
    {
        if (doc.StripEmptyFields() > 0)
        {
            RenderRecord(preservePosition: false);
            UpdateSidebarItem(doc);
        }
    }

    private void ValidateRecord()
    {
        if (!RequireCatalogue()) return;
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _grid.CommitFocused();
        StripEmptyFieldsAndRefresh(_currentDoc); // validate removes contentless fields (task 17)

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

        SetMessage(findings.Count == 0 ? "Record is valid." : FindingSummary(findings));
    }

    /// <summary>Ctrl+L: validate and push. On any error nothing is written and the
    /// findings list stays up so the cataloguer can click straight to each one; a
    /// clean record is promoted to pushed (001/005/leader derived) and, for an
    /// authority record, ripples into its linked bibs.</summary>
    private void PushRecord()
    {
        if (!RequireCatalogue()) return;
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _grid.CommitFocused();

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

        StripEmptyFieldsAndRefresh(doc); // validate removes contentless fields before pushing (task 17)

        var profile = ValidationProfileConfig.For(doc.Stored.Base);

        // A brief, visible beat so the push reads as a real action (same as Ctrl+W).
        SetMessage("Validating and pushing…");
        _messageBar.Refresh();
        Cursor.Current = Cursors.WaitCursor;
        System.Threading.Thread.Sleep(200);
        Cursor.Current = Cursors.Default;

        // Gate on findings BEFORE committing. Errors block outright; warnings no
        // longer ride silently into the catalogue — they raise a confirmation popup
        // where a plain Enter pushes anyway and Cancel backs out (task #11).
        var pre = new PushService(_repo).Check(doc.Stored, profile);
        if (pre.Any(f => f.IsError))
        {
            ShowFindings(pre);
            SetMessage($"{FindingSummary(pre)} — push blocked; nothing was written.");
            return;
        }
        var warns = pre.Where(f => !f.IsError).ToList();
        if (warns.Count > 0)
        {
            ShowFindings(pre);
            if (!ConfirmPushWithWarnings(warns))
            {
                SetMessage($"Push cancelled — {warns.Count} warning(s) to review below.");
                return;
            }
        }

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
        // The record is now in the catalogue — its draft file (if it had one) has
        // done its job and would otherwise reopen as a duplicate. Remove it.
        if (doc.DraftId is not null) { _drafts?.Delete(doc.DraftId); doc.DraftId = null; }
        _sync.NotePush(); // for the on-exit "back up first?" prompt
        RenderRecord();
        UpdateSidebarItem(doc);
        UpdateHeader();
        ClearFindings();

        // Mirror the pushed record to <output folder>\<001>.mrk (user request).
        // The push itself is already committed; only a write FAILURE is worth a note
        // (success and "no output folder" are silent — expected, covered in the manual).
        string mirrorNote = "";
        try
        {
            RecordMirror.Write(MarcOutFolder(doc.Stored.Base), doc.Record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            mirrorNote = $" (.mrk file not written: {ex.Message})";
        }

        int warnings = result.Warnings.Count();
        string msg = $"Pushed as {doc.Record.ControlNumber} in {doc.Stored.Base}.";
        if (warnings > 0) msg += $" {warnings} warning(s).";
        SetMessage(msg + mirrorNote);
    }

    /// <summary>The Ctrl+L warning gate (task #11): lists the warnings and asks
    /// whether to push anyway. Yes is the default button, so a plain Enter pushes;
    /// No/Escape backs out. The findings are already in the list below for detail.</summary>
    private bool ConfirmPushWithWarnings(IReadOnlyList<ValidationFinding> warnings)
    {
        const int shown = 8;
        string list = string.Join("\n", warnings.Take(shown).Select(w => "•  " + w.Message));
        if (warnings.Count > shown) list += $"\n…  and {warnings.Count - shown} more (see the list below).";
        return MessageBox.Show(this,
            $"This record has {warnings.Count} warning(s):\n\n{list}\n\nPush anyway?",
            "Warnings — Push?", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button1) == DialogResult.Yes;
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
        // Land on the value box of the offending field/subfield (a record-level ref
        // with SubfieldIndex -1 falls back to the field's first row).
        _grid.FocusElement(r.FieldIndex, r.SubfieldIndex, BoxPart.Value);
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
        SetMessage("Output folder set.");
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

        // A saved draft is a working file, not catalogue data — but Ctrl+Delete is a
        // fast key, so confirm before discarding its file (this cannot be undone).
        if (doc.DraftId is not null)
        {
            if (MessageBox.Show(this,
                    $"Discard draft \"{TitleOf(doc.Record)}\"?\n\n" +
                    "This deletes its saved draft file. This cannot be undone.",
                    "Delete Draft", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            _drafts?.Delete(doc.DraftId);
            doc.DraftId = null;
            CloseOpenRecord(doc);
            return;
        }

        if (doc.Stored.Id == 0)
        {
            SetMessage("This record was never saved — use Remove in the sidebar to close it.");
            return;
        }

        string cn = doc.Record.ControlNumber ?? "(no 001)";
        // A pushed record is live catalogue data — deleting it warrants a warning.
        // A draft was never in the catalogue proper, so it deletes without one
        // (user request: the warning is for records, not drafts — task 16).
        if (doc.Stored.Status == RecordStatus.Pushed && MessageBox.Show(this,
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
        SetMessage($"Deleted {cn}.");
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
            _grid.CommitFocused();
            var copy = EditorDocument.CopyWithout001(_currentDoc.Record);
            AddToSidebar(new EditorDocument(new StoredRecord(_currentDoc.Stored.Base, copy), dirty: true));
            _grid.FocusElement(-1, -1, BoxPart.Leader); // start in the leader, no mouse needed
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
        _grid.FocusElement(-1, -1, BoxPart.Leader); // start in the leader, no mouse needed
    }

    private void SaveDraft()
    {
        if (!RequireCatalogue()) return;
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _grid.CommitFocused();
        var doc = _currentDoc;

        // A pushed record IS in the catalogue — "save a draft" of it doesn't apply;
        // its save action is Ctrl+L, which updates the catalogue copy in place.
        // Drafts (Ctrl+D) are only for records not yet in the catalogue.
        if (doc.Stored.Id != 0)
        {
            SetMessage("This record is in the catalogue — press Ctrl+L to save your changes to it.");
            return;
        }

        try
        {
            doc.DraftId = _drafts!.Save(doc.DraftId, doc.Stored.Base, doc.Record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetMessage($"Draft not saved: {ex.Message}");
            return;
        }

        doc.MarkSaved();
        UpdateSidebarItem(doc);
        UpdateHeader();
        SetMessage("Draft saved.");
    }

    private void UpdateSidebarItem(EditorDocument doc)
    {
        foreach (ListViewItem item in _openList.Items)
        {
            if (!ReferenceEquals(item.Tag, doc)) continue;
            item.SubItems[1].Text = AccessionSlot(doc) ?? "";
            item.SubItems[2].Text = TitleOf(doc.Record);
            item.SubItems[3].Text = SidebarStatus(doc);
            return;
        }
    }

    private void SaveTemplate()
    {
        if (_currentDoc is null) { SetMessage("No record on screen."); return; }
        _grid.CommitFocused();

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
        SetMessage("Template saved.");
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

        using var wizard = new ImportWizardForm(source, report,
            _appState.NormalizeFixedFieldsOnImport, _appState.NormalizeEncodingOnImport);
        if (wizard.ShowDialog(this) != DialogResult.OK) return; // nothing committed

        // Remember the normalize choices and, when on, apply them before anything else
        // touches the parsed records — this feeds both the drafts and the pushed path below.
        if (wizard.NormalizeFixedFields != _appState.NormalizeFixedFieldsOnImport ||
            wizard.NormalizeEncoding != _appState.NormalizeEncodingOnImport)
        {
            _appState.NormalizeFixedFieldsOnImport = wizard.NormalizeFixedFields;
            _appState.NormalizeEncodingOnImport = wizard.NormalizeEncoding;
            _appState.Save();
        }
        if (wizard.NormalizeFixedFields) ImportEngine.Normalize(plan);
        if (wizard.NormalizeEncoding) ImportEngine.NormalizeEncoding(plan);

        // Import-as-drafts: bring dirty LC records in as UNSAVED working drafts to
        // clean up, not into the catalogue (user, 2026-08-08). They live only in the
        // session — Ctrl+D saves one as a draft file, Ctrl+L pushes it, and any left
        // unsaved are discarded on close (correct behaviour).
        if (wizard.SelectedMode == ImportMode.AsDrafts)
        {
            OpenImportedDrafts(new ImportEngine(_repo!).ParsedRecords(plan));
            return;
        }

        try
        {
            var result = new ImportEngine(_repo!).Commit(plan);
            SetMessage($"Imported {result.RecordsImported} record(s) — BIB {result.BibCount}, AUT {result.AutCount}.");
            // A single pushed record opens straight into the editor — no hunting for
            // it in search afterwards (task 1).
            if (result.ImportedIds.Count == 1)
                OpenRecordById(result.ImportedIds[0]);
        }
        catch (Microsoft.Data.Sqlite.SqliteException e)
        {
            // e.g. a record inserted between Analyze and Commit now collides;
            // the transaction rolled back — the catalogue is untouched.
            MessageBox.Show(this, $"Import failed and nothing was committed.\n\n{e.Message}",
                "Import", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Import-as-drafts: open each parsed record as an UNSAVED working draft
    /// in the sidebar. Added quietly (via <see cref="MakeSidebarItem"/>); the first is
    /// selected so the cataloguer can start cleaning it up at once. Nothing is written —
    /// each is dirty until Ctrl+D (save to a draft file) or Ctrl+L (push).</summary>
    private void OpenImportedDrafts(IReadOnlyList<(string Base, MarcRecord Record)> records)
    {
        ListViewItem? first = null;
        foreach (var (@base, record) in records)
        {
            var item = MakeSidebarItem(new EditorDocument(new StoredRecord(@base, record), dirty: true));
            _openList.Items.Add(item);
            first ??= item;
        }
        if (first is not null) { _openList.SelectedItems.Clear(); first.Selected = true; } // → record view
        SetMessage($"Imported {records.Count} record(s) as drafts.");
    }

    // ---------- export ----------

    private void ExportBase()
    {
        if (!RequireCatalogue()) return;
        int count = _repo.Count(_search.CurrentBase);
        if (count == 0) { SetMessage($"{_search.CurrentBase} is empty — nothing to export."); return; }
        ExportTo($"{_search.CurrentBase}.mrk", path =>
        {
            new ExportEngine(_repo).ExportBaseToFile(_search.CurrentBase, path);
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
