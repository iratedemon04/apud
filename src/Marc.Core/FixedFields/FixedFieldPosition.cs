namespace Marc.Core.FixedFields;

/// <summary>
/// One byte range of a fixed field (LDR/008/006/007), described by data, not
/// code (PLAN §3.3). Loaded from the embedded JSON layouts; the same objects
/// drive the F-menu dialog (Module 7) and, later, the validator (Module 9) — a
/// single source of truth so the two can never disagree.
/// </summary>
public sealed class FixedFieldPosition
{
    /// <summary>Zero-based offset into the fixed field.</summary>
    public int Off { get; init; }

    /// <summary>Number of characters this position spans (default 1).</summary>
    public int Len { get; init; } = 1;

    /// <summary>Human label, e.g. "Type of date".</summary>
    public string Name { get; init; } = "";

    /// <summary>Auto-fill hint. "yymmdd" = today's date (008/00-05).</summary>
    public string? Auto { get; init; }

    /// <summary>Computed or constant positions (record length, base address,
    /// the 20-23 entry map). Shown but not editable — the validator owns them.</summary>
    public bool Protected { get; init; }

    /// <summary>Name of a shipped code table this position is checked against
    /// (e.g. "marc-countries"). The tables themselves arrive with the validator
    /// (Module 9); the attribute travels in the schema from day one.</summary>
    public string? Lookup { get; init; }

    /// <summary>For single-character coded positions: code → meaning. Blank
    /// (a space code, shown as "#" in MARC docs) is keyed as " ".</summary>
    public Dictionary<string, string>? Values { get; init; }

    /// <summary>Last byte this position touches.</summary>
    public int End => Off + Len - 1;

    /// <summary>Label with its MARC position number, Aleph-style:
    /// "Type of date (06)" or "Date entered on file (00-05)".</summary>
    public string Label => Len == 1 ? $"{Name} ({Off:00})" : $"{Name} ({Off:00}-{End:00})";
}
