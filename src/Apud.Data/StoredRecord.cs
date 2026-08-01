using Marc.Core;

namespace Apud.Data;

public enum RecordStatus
{
    Draft,
    Pushed,
}

/// <summary>A MARC record together with its database identity and lifecycle metadata.</summary>
public sealed class StoredRecord
{
    public long Id { get; internal set; }

    /// <summary>"BIB" or "AUT" — which base the record lives in.</summary>
    public string Base { get; }

    public RecordStatus Status { get; internal set; }
    public DateTime CreatedUtc { get; internal set; }
    public DateTime UpdatedUtc { get; internal set; }

    public MarcRecord Record { get; }

    public StoredRecord(string @base, MarcRecord record)
    {
        if (@base is not ("BIB" or "AUT"))
            throw new ArgumentException("Base must be 'BIB' or 'AUT'.", nameof(@base));
        Base = @base;
        Record = record;
    }
}

/// <summary>One row of a record list (navigation pane, search results) — no field data loaded.
/// Author is the 1XX heading (7XX when there is none); Year comes from 008/07-10,
/// falling back to 260/264 $c. Either may be empty — display what's there.</summary>
public sealed record RecordSummary(
    long Id, string Base, string? ControlNumber, RecordStatus Status, string Title,
    string Author, string Year, DateTime UpdatedUtc);

/// <summary>One line of the authority browse list (Module 8): which authority
/// record it belongs to, whether it is the authorized/see/see-also form, the tag
/// it came from, its normalized sort key and its display text.</summary>
public sealed record BrowseHeading(
    long AuthRecordId, HeadingKind Kind, string Tag, string Normalized, string Display);

/// <summary>A positioned browse window: the entries in normalized order and the
/// index of the row the cursor should land on (first entry ≥ the search point).</summary>
public sealed record BrowseResult(IReadOnlyList<BrowseHeading> Entries, int Position);
