namespace Marc.Core.Mrk;

public enum MrkSeverity
{
    Warning,
    Error,
}

/// <summary>
/// One problem found while reading .mrk text. Diagnostics are recoverable by design:
/// the reader always produces its best-effort records and reports what it saw,
/// so the import wizard (Module 5) can show a per-record report instead of dying
/// on the first bad line.
/// </summary>
public sealed record MrkDiagnostic(MrkSeverity Severity, int Line, string Message)
{
    public override string ToString() => $"{Severity} (line {Line}): {Message}";
}

/// <summary>Result of reading .mrk text: the records plus everything worth complaining about.</summary>
public sealed class MrkReadResult
{
    public List<MarcRecord> Records { get; } = new();
    public List<MrkDiagnostic> Diagnostics { get; } = new();

    public bool HasErrors => Diagnostics.Any(d => d.Severity == MrkSeverity.Error);
}
