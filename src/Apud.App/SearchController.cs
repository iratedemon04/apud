using Apud.Data;

namespace Apud.App;

/// <summary>
/// The Search screen (docs/MAINFORM-REFACTOR-PLAN.md step 2), extracted from
/// <see cref="MainForm"/> so the search form, results grid, history, paging and
/// base selector read in isolation. It owns the whole search <see cref="View"/>
/// panel (search bar + results + history + the "load more" bar) and every piece
/// of search state; MainForm docks <see cref="View"/> into the layout and drives
/// it through a handful of commands.
///
/// Like <see cref="SyncCoordinator"/> it never gets a <see cref="MainForm"/>
/// reference: it is handed only a repo getter, the catalogue guard, the message
/// sink, an "open this record id" callback (the results→editor bridge, which
/// lives on the MainForm side), and an "the base changed" callback so the Base
/// menu's checkmarks stay in step. The FTS query layer underneath
/// (<see cref="RecordRepository"/>) is already unit-tested; this is the UI glue.
///
/// It also owns the active base (BIB/AUT): the base determines which scopes and
/// result columns are shown and what a search queries, so that state is cohesive
/// with search. MainForm reads it back via <see cref="CurrentBase"/> (Export
/// targets the active base) and flips it via <see cref="SetBase"/>.
/// </summary>
public sealed class SearchController
{
    private const int ListPageSize = 1000;

    private readonly Func<RecordRepository?> _repo;
    private readonly Func<bool> _requireCatalogue;
    private readonly Action<string> _setMessage;
    private readonly Action<long> _openRecordById;
    private readonly Action<string> _onBaseChanged;

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
        ("Local (9XX)", SearchScope.Local9xx),
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

    private readonly Label _searchBaseLabel; // read-only "which base" tag in the search bar (was a redundant dropdown)
    private readonly ComboBox _searchScope;
    private readonly ComboBox _sortBox;
    private readonly TextBox _searchBox;
    private readonly ListView _resultsList;
    private readonly ListView _historyList;
    private readonly SearchHistory _history = new();
    private readonly Button _moreButton;
    private IReadOnlyList<RecordSummary> _currentResults = Array.Empty<RecordSummary>();
    private bool _listAllMode;            // true while a paged whole-base listing is shown
    private int _listAllTotal;            // total records in the base being paged

    private string _currentBase = "BIB"; // the single source of truth for the active base (menu + Ctrl+B drive it)

    /// <summary>The panel MainForm docks into the layout — the entire search screen.</summary>
    public Panel View { get; }

    /// <summary>The active base (BIB/AUT). Read by Export, which targets it.</summary>
    public string CurrentBase => _currentBase;

    public SearchController(
        Func<RecordRepository?> repo,
        Func<bool> requireCatalogue,
        Action<string> setMessage,
        Action<long> openRecordById,
        Action<string> onBaseChanged)
    {
        _repo = repo;
        _requireCatalogue = requireCatalogue;
        _setMessage = setMessage;
        _openRecordById = openRecordById;
        _onBaseChanged = onBaseChanged;

        // ----- search view -----
        // The base is chosen from the Base menu (or Ctrl+B) — one control, not two.
        // Here we only SHOW which base is active, so the search bar isn't ambiguous.
        _searchBaseLabel = new Label
        {
            AutoSize = false,
            Width = 46,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Anchor = AnchorStyles.Left,
            Text = _currentBase,
        };

        _searchScope = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        PopulateScopes(_currentBase);

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
        searchForm.Controls.Add(_searchBaseLabel, 0, 0);
        searchForm.Controls.Add(_searchScope, 1, 0);
        searchForm.Controls.Add(_sortBox, 2, 0);
        searchForm.Controls.Add(_searchBox, 3, 0);
        searchForm.Controls.Add(searchButton, 4, 0);
        searchForm.Controls.Add(listAllButton, 5, 0);

        _resultsList = new ListView
        {
            Dock = DockStyle.Fill,
            View = System.Windows.Forms.View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
        };
        ConfigureResultColumns(_currentBase);
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
            View = System.Windows.Forms.View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
        };
        _historyList.Columns.Add("Search history", 300);
        _historyList.Columns.Add("Base", 50);
        _historyList.Columns.Add("Scope", 80);
        _historyList.Columns.Add("Hits", 50, HorizontalAlignment.Right);
        _historyList.DoubleClick += (_, _) => RerunFromHistory();

        _moreButton = new Button
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            Visible = false,
            FlatStyle = FlatStyle.System,
        };
        _moreButton.Click += (_, _) => LoadMoreListAll();

        View = new Panel { Dock = DockStyle.Fill };
        View.Controls.Add(_resultsList);
        View.Controls.Add(searchForm);
        View.Controls.Add(_historyList);
        View.Controls.Add(_moreButton); // docks above history, just under the results
    }

    /// <summary>Puts the keyboard in the search box (used when the Search view is shown).</summary>
    public void FocusSearchBox() => _searchBox.Focus();

    /// <summary>Clears the on-screen results and paging state for a freshly opened
    /// catalogue (the old catalogue's ids mean nothing here). The history list is
    /// cleared too; the history model itself is left as-is, matching prior behaviour.</summary>
    public void Reset()
    {
        _resultsList.Items.Clear();
        _currentResults = Array.Empty<RecordSummary>();
        _listAllMode = false;
        UpdateMoreButton();
        _historyList.Items.Clear();
    }

    // ---------- base ----------

    public void SetBase(string @base)
    {
        bool baseChanged = _currentBase != @base;
        _currentBase = @base;
        _onBaseChanged(@base);   // MainForm ticks the Base menu's BIB/AUT checkmarks
        _searchBaseLabel.Text = @base;
        PopulateScopes(@base);
        // The two bases have different result columns; switching clears the now-stale
        // hits from the other base so rows never sit under the wrong header.
        if (baseChanged)
        {
            ConfigureResultColumns(@base);
            _resultsList.Items.Clear();
            _currentResults = Array.Empty<RecordSummary>();
        }
        _listAllMode = false; // a paged listing belongs to the base it was started on
        UpdateMoreButton();
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
        if (!_requireCatalogue()) return;
        var repo = _repo()!;
        string query = _searchBox.Text.Trim();
        if (query.Length == 0) return;

        var scope = _scopes[_searchScope.SelectedIndex].Scope;
        var ids = repo.Search(CurrentBase, query, scope);

        _history.Add(new SearchHistoryEntry(query, scope, CurrentBase, ids.Count));
        RefreshHistoryList();

        // Hydrate ONLY the hits (never the whole base): FTS returns ≤200 ids, so this
        // stays fast at any catalogue size. ids arrive in FTS relevance order; keep that
        // as the base order so the Relevance sort can reproduce it (other sorts reorder
        // in RenderResults).
        var byId = repo.ListByIds(ids).ToDictionary(s => s.Id);
        _currentResults = ids.Select(id => byId.GetValueOrDefault(id)).Where(s => s != null).Cast<RecordSummary>().ToList();
        _listAllMode = false; // a search is not a paged list
        UpdateMoreButton();
        RenderResults();
        _setMessage($"{ids.Count} hit(s) for \"{query}\" in {CurrentBase}.");
    }

    /// <summary>The explicit whole-base listing, paged: shows the first
    /// <see cref="ListPageSize"/> records and offers a "Load next N" bar to keep going —
    /// so opening a 500,000-record base never tries to build one giant list. Feeds the
    /// same sort dropdown; natural order is control-number, which is also the default.</summary>
    private void ListAll()
    {
        if (!_requireCatalogue()) return;
        var repo = _repo()!;
        _listAllMode = true;
        _listAllTotal = repo.Count(CurrentBase);
        _currentResults = repo.ListPage(CurrentBase, ListPageSize, 0);
        UpdateMoreButton();
        RenderResults();
        _setMessage($"{CurrentBase}: showing {_currentResults.Count:N0} of {_listAllTotal:N0} record(s).");
    }

    /// <summary>Loads and appends the next page of a List All, then updates the bar.</summary>
    private void LoadMoreListAll()
    {
        var repo = _repo();
        if (!_listAllMode || repo is null) return;
        var next = repo.ListPage(CurrentBase, ListPageSize, _currentResults.Count);
        _currentResults = _currentResults.Concat(next).ToList();
        UpdateMoreButton();
        RenderResults();
        _setMessage($"{CurrentBase}: showing {_currentResults.Count:N0} of {_listAllTotal:N0} record(s).");
    }

    /// <summary>Shows the "Load next N" bar with the remaining count while a paged List
    /// All has more to load; hides it otherwise (including for searches).</summary>
    private void UpdateMoreButton()
    {
        if (_moreButton is null) return;
        int remaining = _listAllMode ? _listAllTotal - _currentResults.Count : 0;
        if (remaining > 0)
        {
            _moreButton.Text =
                $"Load next {Math.Min(ListPageSize, remaining):N0}   (showing {_currentResults.Count:N0} of {_listAllTotal:N0})";
            _moreButton.Visible = true;
        }
        else
        {
            _moreButton.Visible = false;
        }
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

    /// <summary>The result-list columns differ by base: a bibliographic record is
    /// Title / Author / Year, an authority is Classification / Heading / Source keyed
    /// by its accession number (task 2). Called on base switch, so the header always
    /// matches what's shown.</summary>
    private void ConfigureResultColumns(string @base)
    {
        _resultsList.Columns.Clear();
        if (@base == "AUT")
        {
            _resultsList.Columns.Add("aut-000", 70);
            _resultsList.Columns.Add("Classification", 110);
            _resultsList.Columns.Add("Heading", 300);
            _resultsList.Columns.Add("Source", 220);
        }
        else
        {
            _resultsList.Columns.Add("bib-000", 70);
            _resultsList.Columns.Add("Title", 300);
            _resultsList.Columns.Add("Author", 180);
            _resultsList.Columns.Add("Year", 50);
            _resultsList.Columns.Add("Status", 60);
        }
    }

    private void FillResults(IEnumerable<RecordSummary> summaries)
    {
        _resultsList.BeginUpdate();
        _resultsList.Items.Clear();
        foreach (var s in summaries)
        {
            var item = new ListViewItem(s.ControlNumber ?? "");
            if (s.Base == "AUT")
            {
                item.SubItems.Add(s.Classification);
                item.SubItems.Add(s.Title);   // the full 1XX heading
                item.SubItems.Add(s.Source);
            }
            else
            {
                item.SubItems.Add(s.Title);
                item.SubItems.Add(s.Author);
                item.SubItems.Add(s.Year);
                item.SubItems.Add(s.Status == RecordStatus.Pushed ? "pushed" : "draft");
            }
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

    private void OpenSelectedResult()
    {
        if (_repo() is null || _resultsList.SelectedItems.Count == 0) return;
        var s = (RecordSummary)_resultsList.SelectedItems[0].Tag!;
        _openRecordById(s.Id);
    }
}
