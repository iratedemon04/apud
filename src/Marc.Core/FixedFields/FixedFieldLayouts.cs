using System.Reflection;
using System.Text.Json;

namespace Marc.Core.FixedFields;

/// <summary>
/// The shipped fixed-field layouts, loaded once from JSON embedded in this
/// assembly (no external files to lose; the validator in Module 9 reads the very
/// same objects). Which layout applies is derived from the record — the leader
/// for LDR, and LDR/06-07 for the 008 material type, exactly as MARC21 config
/// specifies.
/// </summary>
public static class FixedFieldLayouts
{
    private static readonly IReadOnlyDictionary<string, FixedFieldLayout> ByKey = Load();

    /// <summary>All loaded layouts, keyed "field/material".</summary>
    public static IReadOnlyDictionary<string, FixedFieldLayout> All => ByKey;

    public static FixedFieldLayout? Get(string field, string material) =>
        ByKey.GetValueOrDefault($"{field}/{material}");

    /// <summary>The leader layout for a record: authority when LDR/06 = 'z',
    /// otherwise bibliographic.</summary>
    public static FixedFieldLayout? Leader(string leader) =>
        Get("LDR", Char(leader, 6) == 'z' ? "authority" : "bib");

    /// <summary>The 008 layout for a record, chosen from the leader.</summary>
    public static FixedFieldLayout? For008(string leader) =>
        Get("008", Material008(leader));

    /// <summary>
    /// MARC21 "configuration of the 008" — the material type that selects the
    /// 008 byte map, derived from LDR/06 (type of record) and LDR/07
    /// (bibliographic level). Every LDR/06 value maps to exactly one material.
    /// </summary>
    public static string Material008(string leader)
    {
        char type = Char(leader, 6);
        char level = Char(leader, 7);
        return type switch
        {
            'z' => "authority",
            'a' => level is 'b' or 'i' or 's' ? "CR" : "BK", // serial/integrating language material
            't' => "BK",
            'c' or 'd' or 'i' or 'j' => "MU",  // notated/manuscript music, sound recordings
            'e' or 'f' => "MP",                // cartographic
            'g' or 'k' or 'o' or 'r' => "VM",  // projected/graphic/kit/3-D
            'm' => "CF",                       // computer file
            'p' => "MX",                       // mixed materials
            _ => "BK",
        };
    }

    private static char Char(string s, int i) => i < s.Length ? s[i] : ' ';

    private static Dictionary<string, FixedFieldLayout> Load()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var asm = Assembly.GetExecutingAssembly();
        var map = new Dictionary<string, FixedFieldLayout>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.Contains(".FixedFields.layouts.", StringComparison.Ordinal) ||
                !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            using var stream = asm.GetManifestResourceStream(name)!;
            var layout = JsonSerializer.Deserialize<FixedFieldLayout>(stream, options)
                ?? throw new InvalidOperationException($"Empty fixed-field layout resource: {name}");
            map[layout.Key] = layout;
        }
        return map;
    }
}
