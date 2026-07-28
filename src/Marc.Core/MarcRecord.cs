namespace Marc.Core;

public enum RecordKind
{
    Bibliographic,
    Authority,
}

/// <summary>
/// One MARC21 record: a 24-character leader and an ordered list of fields.
/// Field order is significant and is never changed implicitly; sorting is an
/// explicit command in the editor, not a side effect.
/// </summary>
public sealed class MarcRecord
{
    public const string DefaultBibLeader = "00000nam a2200000 i 4500";

    private string _leader = DefaultBibLeader;

    /// <summary>
    /// The 24-character leader. Length is enforced here; per-position validity
    /// is the validator's job (Module 9), not the model's.
    /// </summary>
    public string Leader
    {
        get => _leader;
        set
        {
            if (value.Length != MarcConstants.LeaderLength)
                throw new ArgumentException(
                    $"Leader must be exactly {MarcConstants.LeaderLength} characters, got {value.Length}.");
            _leader = value;
        }
    }

    public List<MarcField> Fields { get; } = new();

    /// <summary>Record kind per LDR/06: 'z' = authority, everything else bibliographic.</summary>
    public RecordKind Kind => Leader[6] == 'z' ? RecordKind.Authority : RecordKind.Bibliographic;

    /// <summary>The 001 control number, or null if the record has none yet.</summary>
    public string? ControlNumber =>
        Fields.FirstOrDefault(f => f.Tag == "001")?.ControlData?.Trim();

    /// <summary>All fields with the given tag, in record order.</summary>
    public IEnumerable<MarcField> FieldsWithTag(string tag) =>
        Fields.Where(f => f.Tag == tag);
}
