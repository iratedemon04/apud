namespace Apud.Data;

/// <summary>
/// Field scope of a full-text search — maps 1:1 onto the record_fts columns.
/// All = the anytext column plus every scoped column (an unfiltered match).
/// </summary>
public enum SearchScope
{
    All,
    // BIB
    Title,
    Author,
    Subjects,
    Series,
    Publisher,
    Notes,
    CallNumber,
    Isbn,
    // AUT — one per 1XX heading type (a 130 lookup is nothing like a 100 lookup)
    HeadingPersonal,
    HeadingCorporate,
    HeadingMeeting,
    HeadingUniform,
    HeadingTopical,
    HeadingGeographic,
    HeadingGenre,
    SeeFrom,
    SeeAlso,
    Sources,
    // shared
    ControlNumber,
}
