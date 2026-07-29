using Apud.Data;

namespace Apud.App;

public sealed record SearchHistoryEntry(string Query, SearchScope Scope, string Base, int Hits);

/// <summary>
/// The Aleph-style session search history (docs/ALEPH-WORKFLOW.md): every search
/// lands here with its hit count so queries can be iterated and compared
/// ("fuero politico" 0 → "fuero" 26). Newest first; repeats are kept — a re-run
/// is a new event, exactly as Aleph shows it. In-memory ONLY: the no-smart rule
/// means history dies with the session, nothing is written to disk.
/// </summary>
public sealed class SearchHistory
{
    private readonly List<SearchHistoryEntry> _entries = new();

    public IReadOnlyList<SearchHistoryEntry> Entries => _entries;

    public void Add(SearchHistoryEntry entry) => _entries.Insert(0, entry);
}
