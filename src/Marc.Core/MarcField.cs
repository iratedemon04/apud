namespace Marc.Core;

/// <summary>
/// One MARC field. Control fields (tag &lt; 010, and LDR is handled separately on the record)
/// carry raw <see cref="ControlData"/>; data fields carry indicators and subfields.
/// Blank indicators are stored as ' ' (space); the .mrk form writes them as '\'.
/// </summary>
public sealed class MarcField
{
    public string Tag { get; }

    /// <summary>True for 001–009: no indicators, no subfields, raw data only.</summary>
    public bool IsControl => string.CompareOrdinal(Tag, "010") < 0;

    /// <summary>Raw data of a control field; null for data fields.</summary>
    public string? ControlData { get; set; }

    public char Ind1 { get; set; } = ' ';
    public char Ind2 { get; set; } = ' ';

    public List<MarcSubfield> Subfields { get; } = new();

    /// <summary>
    /// Internal id of the AUT record this field's heading is linked to (authority control).
    /// Set by validation/browse from Module 8 on; persisted with the field.
    /// </summary>
    public long? AuthLinkId { get; set; }

    public MarcField(string tag)
    {
        if (tag.Length != 3)
            throw new ArgumentException($"MARC tag must be 3 characters, got '{tag}'.", nameof(tag));
        Tag = tag;
    }

    /// <summary>Value of the first subfield with the given code, or null.</summary>
    public string? Subfield(char code) =>
        Subfields.FirstOrDefault(s => s.Code == code)?.Value;
}
