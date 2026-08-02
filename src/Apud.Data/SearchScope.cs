namespace Apud.Data;

/// <summary>
/// Field scope of a full-text search — maps 1:1 onto the record_fts columns.
/// All = the anytext column plus every scoped column (an unfiltered match).
/// </summary>
public enum SearchScope
{
    All,
    Title,
    Author,
    Subjects,
    Notes,
    CallNumber,
    ControlNumber,
}
