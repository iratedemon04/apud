using System.Text.Json;
using Marc.Core.Validation;

namespace Apud.App;

/// <summary>
/// Loads the per-base validation profile from a user-editable
/// <c>profile-bib.json</c> / <c>profile-aut.json</c> beside the exe — the same
/// contract as keymap.json and tagnames.json (docs/PLAN.md §8 stage 3): a missing
/// file means the built-in <see cref="ValidationProfile.Default"/> applies; a
/// broken file is reported and the default stands; never crashes. The shipped
/// starter files carry exactly the defaults, documented, so a cataloguer can see
/// the shape and tighten the rules to their house style.
/// </summary>
public static class ValidationProfileConfig
{
    public static string FileName(string @base) => $"profile-{@base.ToLowerInvariant()}.json";

    private static readonly Dictionary<string, ValidationProfile> _profiles = new();

    /// <summary>The profile in force for a base — the loaded file's, or the default.</summary>
    public static ValidationProfile For(string @base) =>
        _profiles.TryGetValue(@base, out var p) ? p : ValidationProfile.Default(@base);

    /// <summary>Loads one base's profile file. Returns null when all is well (a
    /// missing file is well), else a one-line report for the message bar.</summary>
    public static string? LoadFile(string path, string @base)
    {
        if (!File.Exists(path)) { _profiles.Remove(@base); return null; }
        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception ex)
        {
            _profiles.Remove(@base);
            return $"{FileName(@base)} not read ({ex.Message}) — using built-in rules.";
        }
        return ApplyJson(json, @base);
    }

    /// <summary>Parses and installs a base's profile; file I/O split off so tests
    /// run headless. Returns null or a one-line report.</summary>
    internal static string? ApplyJson(string json, string @base)
    {
        var options = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        try
        {
            using var doc = JsonDocument.Parse(json, options);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                _profiles.Remove(@base);
                return $"{FileName(@base)} ignored (expected a JSON object) — using built-in rules.";
            }

            var root = doc.RootElement;
            var skipped = new List<string>();

            var mandatory = new List<string[]>();
            foreach (var group in Array(root, "mandatory", skipped))
            {
                var tags = group.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToArray();
                if (tags.Length > 0) mandatory.Add(tags);
            }

            var nonRepeatable = Strings(root, "nonRepeatable", skipped);

            var requiredSubfields = new Dictionary<string, char[]>();
            if (root.TryGetProperty("requiredSubfields", out var rs) && rs.ValueKind == JsonValueKind.Object)
                foreach (var prop in rs.EnumerateObject())
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        requiredSubfields[prop.Name] = prop.Value.GetString()!.ToCharArray();
                    else
                        skipped.Add($"requiredSubfields.{prop.Name}");

            bool single = root.TryGetProperty("singleHeading1xx", out var s)
                          && s.ValueKind is JsonValueKind.True;

            _profiles[@base] = new ValidationProfile
            {
                Mandatory = mandatory,
                NonRepeatable = new HashSet<string>(nonRepeatable),
                RequiredSubfields = requiredSubfields,
                SingleHeading1xx = single,
            };

            return skipped.Count == 0
                ? null
                : $"{FileName(@base)}: skipped malformed entr{(skipped.Count == 1 ? "y" : "ies")} " +
                  $"{string.Join(", ", skipped)} — built-in rules used for those.";
        }
        catch (JsonException ex)
        {
            _profiles.Remove(@base);
            return $"{FileName(@base)} ignored (line {ex.LineNumber + 1}: not valid JSON) — using built-in rules.";
        }
    }

    private static IEnumerable<JsonElement> Array(JsonElement root, string name, List<string> skipped)
    {
        if (!root.TryGetProperty(name, out var arr)) yield break;
        if (arr.ValueKind != JsonValueKind.Array) { skipped.Add(name); yield break; }
        foreach (var e in arr.EnumerateArray())
            if (e.ValueKind == JsonValueKind.Array) yield return e;
    }

    private static List<string> Strings(JsonElement root, string name, List<string> skipped)
    {
        var list = new List<string>();
        if (!root.TryGetProperty(name, out var arr)) return list;
        if (arr.ValueKind != JsonValueKind.Array) { skipped.Add(name); return list; }
        foreach (var e in arr.EnumerateArray())
            if (e.ValueKind == JsonValueKind.String) list.Add(e.GetString()!);
        return list;
    }
}
