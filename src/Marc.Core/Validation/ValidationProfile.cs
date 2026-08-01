namespace Marc.Core.Validation;

/// <summary>
/// The per-base profile rules the validator checks (docs/PLAN.md §8 stage 3):
/// which fields must be present, which may not repeat, and which subfields a
/// field requires. Plain data so it is trivially loadable from a user-editable
/// <c>profile-&lt;base&gt;.json</c> (the App layer honours the same missing→default,
/// bad→report contract as keymap.json/tagnames.json) — but the built-in
/// <see cref="Default"/> is deliberately lean: a sane MARC21 floor that will not
/// flag a real, imperfect catalogue on every push.
/// </summary>
public sealed class ValidationProfile
{
    /// <summary>Each inner group means "at least one of these tags must be present"
    /// (a single-tag group is a plain mandatory field). BIB: a 245. AUT: one 1XX.</summary>
    public List<string[]> Mandatory { get; init; } = new();

    /// <summary>Tags that may appear at most once.</summary>
    public HashSet<string> NonRepeatable { get; init; } = new();

    /// <summary>Tag → subfield codes every occurrence of it must carry.</summary>
    public Dictionary<string, char[]> RequiredSubfields { get; init; } = new();

    /// <summary>Authority rule: a record must carry exactly one 1XX heading. Off
    /// for BIB (a 100 is optional and the other 1XX-shaped tags do not apply).</summary>
    public bool SingleHeading1xx { get; init; }

    /// <summary>The shipped default for a base — the floor validation applies when
    /// no profile-&lt;base&gt;.json overrides it.</summary>
    public static ValidationProfile Default(string @base) =>
        @base == "AUT" ? DefaultAut() : DefaultBib();

    private static ValidationProfile DefaultBib() => new()
    {
        Mandatory = new() { new[] { "245" } },
        NonRepeatable = new() { "245", "100", "110", "111", "130", "240", "250" },
        RequiredSubfields = new() { ["245"] = new[] { 'a' } },
    };

    private static readonly string[] Aut1xx = { "100", "110", "111", "130", "150", "151", "155" };

    private static ValidationProfile DefaultAut() => new()
    {
        Mandatory = new() { Aut1xx },
        NonRepeatable = new(Aut1xx),
        RequiredSubfields = Aut1xx.ToDictionary(t => t, _ => new[] { 'a' }),
        SingleHeading1xx = true,
    };
}
