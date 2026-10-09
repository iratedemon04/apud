using Apud.Data;
using Marc.Core;

namespace Apud.App;

/// <summary>
/// Catalogue lifecycle and the catalogue-level File commands (docs/MAINFORM-REFACTOR-PLAN.md
/// step 3), extracted from <see cref="MainForm"/> so they read in isolation: New / Open
/// Catalogue, Import Records (the source chooser, the wizard, and the pushed import that runs
/// on a background thread behind <see cref="ImportProgressForm"/>), Export, the BIB/AUT MARC
/// output folders, and the organization code.
///
/// Like <see cref="SyncCoordinator"/> and <see cref="SearchController"/> it never gets a
/// <see cref="MainForm"/> reference — only an owner window for its dialogs and a minimal set
/// of getters and callbacks. The open catalogue's fields (<c>_db</c>, <c>_repo</c>, the draft
/// store, the path) stay in MainForm because every collaborator reads them through its own
/// getter: <see cref="Open"/> validates and opens the file, then hands the new database to
/// the <c>attachCatalogue</c> callback, which swaps those fields and resets the panels.
/// Putting records into the sidebar stays on the MainForm side too (<c>openRecordById</c>
/// for a single imported record, <c>openImportedDrafts</c> for import-as-drafts).
/// </summary>
public sealed class CatalogueController
{
    private const string MarcOutSetting = "marc_out_folder";
    private const string MarcOutSettingAut = "marc_out_folder_aut";

    private readonly IWin32Window _owner;
    private readonly AppState _appState;
    private readonly Func<bool> _requireCatalogue;
    private readonly Func<RecordRepository?> _repo;
    private readonly Func<string?> _catalogPath;
    private readonly Func<string> _currentBase;
    private readonly Func<IReadOnlyList<long>> _selectedRecordIds;
    private readonly Func<string, string, string, bool, string?> _promptText;
    private readonly Action<string> _setMessage;
    private readonly Func<string> _startFolder;
    private readonly Action<string?> _rememberFolder;
    private readonly Action<ApudDatabase, string> _attachCatalogue;
    private readonly Action<long> _openRecordById;
    private readonly Action<IReadOnlyList<(string Base, MarcRecord Record)>> _openImportedDrafts;

    public CatalogueController(
        IWin32Window owner,
        AppState appState,
        Func<bool> requireCatalogue,
        Func<RecordRepository?> repo,
        Func<string?> catalogPath,
        Func<string> currentBase,
        Func<IReadOnlyList<long>> selectedRecordIds,
        Func<string, string, string, bool, string?> promptText,
        Action<string> setMessage,
        Func<string> startFolder,
        Action<string?> rememberFolder,
        Action<ApudDatabase, string> attachCatalogue,
        Action<long> openRecordById,
        Action<IReadOnlyList<(string Base, MarcRecord Record)>> openImportedDrafts)
    {
        _owner = owner;
        _appState = appState;
        _requireCatalogue = requireCatalogue;
        _repo = repo;
        _catalogPath = catalogPath;
        _currentBase = currentBase;
        _selectedRecordIds = selectedRecordIds;
        _promptText = promptText;
        _setMessage = setMessage;
        _startFolder = startFolder;
        _rememberFolder = rememberFolder;
        _attachCatalogue = attachCatalogue;
        _openRecordById = openRecordById;
        _openImportedDrafts = openImportedDrafts;
    }

    // ---------- catalogue lifecycle ----------

    /// <summary>File → New Catalogue.</summary>
    public void New()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "New Catalogue",
            InitialDirectory = _startFolder(),
            FileName = "catalog.db",
            Filter = "Apud catalogue (*.db)|*.db",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(_owner) != DialogResult.OK) return;

        _rememberFolder(Path.GetDirectoryName(dialog.FileName));

        // The dialog already asked about overwriting; a stale file must not be
        // silently opened as an existing catalogue.
        if (File.Exists(dialog.FileName)) File.Delete(dialog.FileName);
        Open(dialog.FileName, createNew: true);
    }

    /// <summary>File → Open Catalogue.</summary>
    public void OpenDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Open Catalogue",
            InitialDirectory = _startFolder(),
            Filter = "Apud catalogue (*.db)|*.db|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(_owner) != DialogResult.OK) return;

        _rememberFolder(Path.GetDirectoryName(dialog.FileName));
        Open(dialog.FileName);
    }

    /// <summary>Opens (or, with <paramref name="createNew"/>, creates) the catalogue at
    /// <paramref name="path"/> and hands it to MainForm to attach. A missing or unreadable
    /// file reports in a dialog and leaves the current catalogue as it was. Also used by
    /// launch (reopen the last catalogue) and by Restore (open the restored copy).</summary>
    public void Open(string path, bool createNew = false)
    {
        // Opening never creates: SQLite would happily conjure an empty database
        // at a mistyped path, and this software creates nothing on its own.
        if (!createNew && !File.Exists(path))
        {
            MessageBox.Show(_owner, $"There is no catalogue at:\n{path}", "Cannot open catalogue",
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
            MessageBox.Show(_owner, e.Message, "Cannot open catalogue",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _attachCatalogue(db, path);
    }

    // ---------- MARC output folder ----------

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
    public string? MarcOutFolder(string @base)
    {
        var (key, _) = MarcOutSpec(@base);
        string? configured = _repo()?.GetSetting(key);
        return string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();
    }

    /// <summary>File → Set BIB / Authority Output Folder: pick the folder Apud
    /// writes each pushed record's .mrk into for that base. Stored in the
    /// catalogue's settings (remembered per catalogue). Cancelling leaves the
    /// current choice; picking the same place is idempotent.</summary>
    public void SetMarcOutFolder(string @base)
    {
        if (!_requireCatalogue()) return;
        var repo = _repo()!;

        var (key, defaultName) = MarcOutSpec(@base);
        string? current = repo.GetSetting(key);
        using var dialog = new FolderBrowserDialog
        {
            Description = $"Choose the folder Apud writes each pushed {@base} record to (as <001>.mrk).",
            UseDescriptionForTitle = true,
            SelectedPath = !string.IsNullOrWhiteSpace(current) && Directory.Exists(current)
                ? current
                : RecordMirror.DefaultFolderFor(_catalogPath(), defaultName) is string d && Directory.Exists(d) ? d : "",
        };
        if (dialog.ShowDialog(_owner) != DialogResult.OK) return;

        repo.SetSetting(key, dialog.SelectedPath);
        _setMessage("Output folder set.");
    }

    /// <summary>File → Set Organization Code: the MARC org code Apud stamps into 003
    /// on push (per-catalogue, stored in the setting table). A per-catalogue constant,
    /// not per-record content — the one org-level value Apud fills for you. Blank turns
    /// 003 auto-fill back off. This is a single focused command, not a Settings dialog
    /// (that was cut Module 10); it mirrors the Set … Output Folder commands.</summary>
    public void SetOrgCode()
    {
        if (!_requireCatalogue()) return;
        var repo = _repo()!;

        string current = repo.GetSetting("org_code") ?? "";
        if (_promptText(
                "Set Organization Code",
                "MARC organization code for field 003. Leave blank for none.",
                current, false) is not string entered)
            return; // cancelled, nothing changes

        string code = entered.Trim();
        repo.SetSetting("org_code", code);
        _setMessage(code.Length > 0
            ? $"Organization code set to {code}."
            : "Organization code cleared.");
    }

    // ---------- import ----------

    /// <summary>File → Import Records: one menu entry that first asks whether to
    /// import a single file or a whole folder, then routes to the matching picker
    /// (user request 2026-08-01 — the two former commands collapsed into one, the
    /// scope chosen inside Apud rather than by which menu item was clicked).</summary>
    public void Import()
    {
        if (!_requireCatalogue()) return;
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

        dialog.ShowDialog(_owner);
        return result;
    }

    /// <summary>Pick one or more .mrk files (e.g. a single authority file MarcEdit
    /// converted into your Downloads) and import just those. Each record routes to
    /// BIB or AUT by its leader, same as a folder import.</summary>
    private void ImportFiles()
    {
        if (!_requireCatalogue()) return;

        using var dialog = new OpenFileDialog
        {
            Title = "Import Records",
            InitialDirectory = _startFolder(),
            Filter = "MARC text (*.mrk)|*.mrk|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(_owner) != DialogResult.OK) return;

        var files = dialog.FileNames;
        _rememberFolder(Path.GetDirectoryName(files[0]));
        string source = files.Length == 1 ? files[0] : $"{files.Length} files";
        RunImport(source, new ImportEngine(_repo()!).Analyze(files));
    }

    /// <summary>Import every .mrk in a folder tree (reached from the Import
    /// Records chooser).</summary>
    private void ImportFolder()
    {
        if (!_requireCatalogue()) return;

        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose a folder; every .mrk file in it (and its subfolders) will be imported.",
            UseDescriptionForTitle = true,
            SelectedPath = _startFolder(),
        };
        if (dialog.ShowDialog(_owner) != DialogResult.OK) return;

        _rememberFolder(dialog.SelectedPath);
        RunImport(dialog.SelectedPath, new ImportEngine(_repo()!).AnalyzeFolder(dialog.SelectedPath));
    }

    /// <summary>The shared analyze → wizard → commit path for both import
    /// commands. The commit is one all-or-nothing transaction; Cancel writes
    /// nothing.</summary>
    private void RunImport(string source, ImportPlan plan)
    {
        var report = plan.Report;
        if (report.TotalRecords == 0)
        {
            MessageBox.Show(_owner, "No records found (no .mrk files, or all empty).",
                "Import", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var wizard = new ImportWizardForm(source, report,
            _appState.NormalizeFixedFieldsOnImport, _appState.NormalizeEncodingOnImport);
        if (wizard.ShowDialog(_owner) != DialogResult.OK) return; // nothing committed

        // Remember the normalize choices and, when on, apply them before anything else
        // touches the parsed records — this feeds both the drafts and the pushed path below.
        if (wizard.NormalizeFixedFields != _appState.NormalizeFixedFieldsOnImport ||
            wizard.NormalizeEncoding != _appState.NormalizeEncodingOnImport)
        {
            _appState.NormalizeFixedFieldsOnImport = wizard.NormalizeFixedFields;
            _appState.NormalizeEncodingOnImport = wizard.NormalizeEncoding;
            _appState.Save();
        }
        // Import-as-drafts: bring dirty LC records in as UNSAVED working drafts to
        // clean up, not into the catalogue (user, 2026-08-08). They live only in the
        // session — Ctrl+D saves one as a draft file, Ctrl+L pushes it, and any left
        // unsaved are discarded on close (correct behaviour). This is the small-batch
        // path (a handful of dirty LC records), so the normalize + sidebar build stay
        // on the UI thread.
        if (wizard.SelectedMode == ImportMode.AsDrafts)
        {
            if (wizard.NormalizeFixedFields) ImportEngine.Normalize(plan);
            if (wizard.NormalizeEncoding) ImportEngine.NormalizeEncoding(plan);
            _openImportedDrafts(new ImportEngine(_repo()!).ParsedRecords(plan));
            return;
        }

        // Pushed path: this is the whole-catalogue case (tens of thousands of records
        // seen in the wild). Normalize + Commit run on a background thread behind a modal
        // progress dialog, so the window keeps repainting (no "Not Responding") and the
        // cataloguer watches it move instead of guessing whether it hung.
        CommitImportWithProgress(plan, wizard.NormalizeFixedFields, wizard.NormalizeEncoding);
    }

    /// <summary>Runs the pushed-import Commit (and any normalize passes) off the UI thread
    /// behind a modal progress bar. The dialog owns a nested message loop while
    /// <see cref="Task.Run"/> does the work, so the UI stays live; the background task
    /// closes the dialog when it finishes or throws. Nothing else can touch the repo while
    /// the modal is up, so the single-connection SQLite repo is never hit concurrently.</summary>
    private void CommitImportWithProgress(ImportPlan plan, bool normalizeFixed, bool normalizeEncoding)
    {
        int total = plan.Report.TotalRecords;

        using var dialog = new ImportProgressForm(total);

        // Progress<T> captures this (UI) thread's context, so its callback marshals back here.
        var progress = new Progress<ImportProgress>(dialog.Report);

        ImportResult? result = null;
        Exception? failure = null;

        dialog.Shown += async (_, _) =>
        {
            try
            {
                result = await Task.Run(() =>
                {
                    if (normalizeFixed) ImportEngine.Normalize(plan);
                    if (normalizeEncoding) ImportEngine.NormalizeEncoding(plan);
                    return new ImportEngine(_repo()!).Commit(plan, ((IProgress<ImportProgress>)progress).Report);
                });
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                dialog.Close();
            }
        };
        dialog.ShowDialog(_owner); // blocks until the background task closes it

        if (failure is Microsoft.Data.Sqlite.SqliteException e)
        {
            // e.g. a record inserted between Analyze and Commit now collides;
            // the transaction rolled back — the catalogue is untouched.
            MessageBox.Show(_owner, $"Import failed and nothing was committed.\n\n{e.Message}",
                "Import", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (failure is not null)
        {
            MessageBox.Show(_owner, $"Import failed and nothing was committed.\n\n{failure.Message}",
                "Import", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (result is null) return;

        _setMessage($"Imported {result.RecordsImported} record(s) — BIB {result.BibCount}, AUT {result.AutCount}.");
        // A single pushed record opens straight into the editor — no hunting for
        // it in search afterwards (task 1).
        if (result.ImportedIds.Count == 1)
            _openRecordById(result.ImportedIds[0]);
    }

    // ---------- export ----------

    /// <summary>File → Export Base: every record in the active base to one .mrk file.</summary>
    public void ExportBase()
    {
        if (!_requireCatalogue()) return;
        var repo = _repo()!;
        int count = repo.Count(_currentBase());
        if (count == 0) { _setMessage($"{_currentBase()} is empty — nothing to export."); return; }
        ExportTo($"{_currentBase()}.mrk", path =>
        {
            new ExportEngine(repo).ExportBaseToFile(_currentBase(), path);
            return count;
        });
    }

    /// <summary>File → Export Selected: the saved records selected in the sidebar.</summary>
    public void ExportSelected()
    {
        if (!_requireCatalogue()) return;
        var ids = _selectedRecordIds();
        if (ids.Count == 0)
        {
            _setMessage("Select one or more saved records in the sidebar first.");
            return;
        }
        ExportTo("selection.mrk", path =>
        {
            new ExportEngine(_repo()!).ExportToFile(ids, path);
            return ids.Count;
        });
    }

    private void ExportTo(string suggestedName, Func<string, int> write)
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Export",
            InitialDirectory = _startFolder(),
            FileName = suggestedName,
            Filter = "MARC text (*.mrk)|*.mrk",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(_owner) != DialogResult.OK) return;
        _rememberFolder(Path.GetDirectoryName(dialog.FileName));
        int count = write(dialog.FileName);
        _setMessage($"Exported {count} record(s) to {dialog.FileName}.");
    }
}
