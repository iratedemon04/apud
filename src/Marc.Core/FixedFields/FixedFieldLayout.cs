namespace Marc.Core.FixedFields;

/// <summary>
/// The complete byte map of one fixed field for one material type — LDR/bib,
/// 008/BK, and so on. Positions cover the whole field contiguously (a test
/// enforces this), so "every position gets a box" in the dialog and nothing is
/// hidden.
/// </summary>
public sealed class FixedFieldLayout
{
    /// <summary>"LDR" or the control tag ("008").</summary>
    public string Field { get; init; } = "";

    /// <summary>Material/kind selector: "bib"/"authority" for LDR;
    /// "BK","CR","MP","MU","VM","CF","MX","authority" for 008.</summary>
    public string Material { get; init; } = "";

    /// <summary>Total length: 24 for LDR, 40 for 008.</summary>
    public int Length { get; init; }

    public List<FixedFieldPosition> Positions { get; init; } = new();

    /// <summary>Lookup key, "008/BK" form.</summary>
    public string Key => $"{Field}/{Material}";
}
