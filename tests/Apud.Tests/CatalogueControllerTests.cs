using Apud.App;
using Apud.Data;

namespace Apud.Tests;

/// <summary>
/// CatalogueController (MainForm refactor step 3) through its callbacks, on the paths
/// that open no dialog: attaching a catalogue, the per-base MARC output folder settings,
/// the organization code via the prompt callback, the export guards, and the
/// no-catalogue-open guard every command checks first.
/// </summary>
public class CatalogueControllerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"apud-catalogue-{Guid.NewGuid():N}");
    private readonly ApudDatabase _memoryDb = ApudDatabase.OpenInMemory();
    private readonly List<ApudDatabase> _attached = new();
    private readonly List<string> _messages = new();
    private readonly List<(string Title, string Prompt, string Initial, bool Mask)> _prompts = new();

    private RecordRepository? _repo;
    private bool _catalogueOpen = true;
    private string? _promptAnswer;
    private string _currentBase = "BIB";
    private IReadOnlyList<long> _selectedIds = Array.Empty<long>();

    public CatalogueControllerTests()
    {
        Directory.CreateDirectory(_dir);
        _repo = new RecordRepository(_memoryDb);
    }

    public void Dispose()
    {
        foreach (var db in _attached) db.Dispose();
        _memoryDb.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class NoOwner : IWin32Window
    {
        public IntPtr Handle => IntPtr.Zero;
    }

    private CatalogueController Controller() => new(
        owner: new NoOwner(),
        appState: new AppState(),
        requireCatalogue: () => _catalogueOpen,
        repo: () => _repo,
        catalogPath: () => null,
        currentBase: () => _currentBase,
        selectedRecordIds: () => _selectedIds,
        promptText: (title, prompt, initial, mask) =>
        {
            _prompts.Add((title, prompt, initial, mask));
            return _promptAnswer;
        },
        setMessage: _messages.Add,
        startFolder: () => _dir,
        rememberFolder: _ => { },
        attachCatalogue: (db, path) => _attached.Add(db),
        openRecordById: _ => throw new InvalidOperationException("not expected"),
        openImportedDrafts: _ => throw new InvalidOperationException("not expected"));

    // ---------- open ----------

    [Fact]
    public void Open_hands_an_existing_catalogue_to_attach()
    {
        string path = Path.Combine(_dir, "existing.db");
        ApudDatabase.Open(path).Dispose();

        ApudDatabase? attached = null;
        string? attachedPath = null;
        var controller = new CatalogueController(new NoOwner(), new AppState(), () => true, () => null,
            () => null, () => "BIB", () => Array.Empty<long>(), (_, _, _, _) => null, _ => { },
            () => _dir, _ => { },
            attachCatalogue: (db, p) => { attached = db; attachedPath = p; _attached.Add(db); },
            openRecordById: _ => { }, openImportedDrafts: _ => { });

        controller.Open(path);

        Assert.NotNull(attached);
        Assert.Equal(path, attachedPath);
    }

    [Fact]
    public void Open_with_createNew_creates_the_file_and_attaches_it()
    {
        string path = Path.Combine(_dir, "brand-new.db");

        Controller().Open(path, createNew: true);

        Assert.True(File.Exists(path));
        Assert.Single(_attached);
    }

    // ---------- MARC output folders ----------

    [Fact]
    public void MarcOutFolder_reads_each_base_from_its_own_setting_trimmed()
    {
        _repo!.SetSetting("marc_out_folder", "  C:\\out\\bib  ");
        _repo.SetSetting("marc_out_folder_aut", "C:\\out\\aut");
        var controller = Controller();

        Assert.Equal("C:\\out\\bib", controller.MarcOutFolder("BIB"));
        Assert.Equal("C:\\out\\aut", controller.MarcOutFolder("AUT"));
    }

    [Fact]
    public void MarcOutFolder_is_null_when_unset_blank_or_no_catalogue_is_open()
    {
        var controller = Controller();
        Assert.Null(controller.MarcOutFolder("BIB"));

        _repo!.SetSetting("marc_out_folder", "   ");
        Assert.Null(controller.MarcOutFolder("BIB"));

        _repo = null;
        Assert.Null(controller.MarcOutFolder("BIB"));
    }

    // ---------- organization code ----------

    [Fact]
    public void SetOrgCode_offers_the_current_code_and_stores_the_trimmed_answer()
    {
        _repo!.SetSetting("org_code", "OLD");
        _promptAnswer = "  MX-MxBAC ";

        Controller().SetOrgCode();

        var prompt = Assert.Single(_prompts);
        Assert.Equal("OLD", prompt.Initial);
        Assert.False(prompt.Mask);
        Assert.Equal("MX-MxBAC", _repo.GetSetting("org_code"));
        Assert.Equal("Organization code set to MX-MxBAC.", Assert.Single(_messages));
    }

    [Fact]
    public void SetOrgCode_cancel_changes_nothing()
    {
        _repo!.SetSetting("org_code", "OLD");
        _promptAnswer = null;

        Controller().SetOrgCode();

        Assert.Equal("OLD", _repo.GetSetting("org_code"));
        Assert.Empty(_messages);
    }

    [Fact]
    public void SetOrgCode_blank_clears_it()
    {
        _repo!.SetSetting("org_code", "OLD");
        _promptAnswer = "  ";

        Controller().SetOrgCode();

        Assert.Equal("", _repo.GetSetting("org_code"));
        Assert.Equal("Organization code cleared.", Assert.Single(_messages));
    }

    // ---------- export guards ----------

    [Fact]
    public void ExportBase_on_an_empty_active_base_says_so_without_a_dialog()
    {
        _currentBase = "AUT";

        Controller().ExportBase();

        Assert.Equal("AUT is empty — nothing to export.", Assert.Single(_messages));
    }

    [Fact]
    public void ExportSelected_with_no_saved_record_selected_says_so()
    {
        _selectedIds = Array.Empty<long>();

        Controller().ExportSelected();

        Assert.Equal("Select one or more saved records in the sidebar first.", Assert.Single(_messages));
    }

    // ---------- no catalogue open ----------

    /// <summary>With no catalogue open every catalogue command stops at the guard (which
    /// shows its own popup in the app) before any dialog, prompt, message or write.</summary>
    [Fact]
    public void Every_catalogue_command_stops_at_the_guard_when_no_catalogue_is_open()
    {
        _catalogueOpen = false;
        _repo = null;
        _promptAnswer = "SHOULD-NOT-BE-ASKED";
        var controller = Controller();

        controller.Import();
        controller.SetOrgCode();
        controller.SetMarcOutFolder("BIB");
        controller.SetMarcOutFolder("AUT");
        controller.ExportBase();
        controller.ExportSelected();

        Assert.Empty(_prompts);
        Assert.Empty(_messages);
    }
}
