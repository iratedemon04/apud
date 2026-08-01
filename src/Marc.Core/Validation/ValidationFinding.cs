namespace Marc.Core.Validation;

/// <summary>An error blocks a push (Ctrl+L); a warning is reported but lets the
/// push through — the cataloguer is trusted to judge it (docs/PLAN.md §8).</summary>
public enum Severity
{
    Error,
    Warning,
}

/// <summary>
/// Where a finding lives, in the editor's own index convention (matching
/// DisplayRow / EditorDocument): <see cref="FieldIndex"/> -1 = the leader,
/// otherwise an index into <see cref="MarcRecord.Fields"/>; <see cref="SubfieldIndex"/>
/// -1 = the whole field. It is what lets the message list jump the cursor to the
/// offending place. Record-level findings (a missing mandatory field) carry no ref.
/// </summary>
public readonly record struct FieldRef(int FieldIndex, int SubfieldIndex)
{
    public static readonly FieldRef Leader = new(-1, -1);
    public static FieldRef Field(int fieldIndex) => new(fieldIndex, -1);
    public static FieldRef Subfield(int fieldIndex, int subfieldIndex) => new(fieldIndex, subfieldIndex);
}

/// <summary>
/// One line of validator output (docs/PLAN.md §8): a severity, an optional
/// pointer at the offending field/subfield, a stable machine <see cref="Code"/>
/// (tests assert on it, not on wording) and a human message.
/// </summary>
public sealed record ValidationFinding(Severity Severity, FieldRef? Ref, string Code, string Message)
{
    public bool IsError => Severity == Severity.Error;
}
