using System.Globalization;
using System.Text;

namespace Marc.Core;

/// <summary>
/// The single NACO-flavored normalization used everywhere headings are compared:
/// the authority browse index, the positioned-browse lookup, and (Module 9) the
/// validator. Diacritics and casing are where cataloguing software rots, so there
/// is exactly ONE function and it is exhaustively tested (docs/PLAN.md §6.3.6,
/// docs/STATE.md Module 8 handoff).
///
/// Rules, in order:
///   1. Unicode-decompose (NFD) and drop every combining mark — this folds
///      accents away and turns ñ→n, á→a, ü→u the NACO way.
///   2. Casefold to lower (invariant).
///   3. Keep letters, digits and the FIRST comma; every other punctuation mark
///      (including Spanish ¿ ¡ and the period, colon, slash, brackets…) becomes a
///      space, so words never fuse. The retained first comma keeps the
///      inverted-name distinction ("Preciado, Amado") that Aleph browse relies on.
///   4. Collapse runs of whitespace and trim.
///
/// The result is a comparison/sort key, never shown to the cataloguer — the
/// human-readable heading is carried separately as the display string.
/// </summary>
public static class HeadingNormalization
{
    public static string Normalize(string heading)
    {
        if (string.IsNullOrEmpty(heading)) return "";

        string decomposed = heading.Normalize(NormalizationForm.FormD);

        var sb = new StringBuilder(decomposed.Length);
        bool commaKept = false;
        foreach (char ch in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.SpacingCombiningMark)
                continue; // a stripped diacritic

            if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToLowerInvariant(ch));
            else if (ch == ',' && !commaKept)
            {
                sb.Append(',');
                commaKept = true;
            }
            else
                sb.Append(' '); // any other punctuation/space keeps words apart
        }

        return CollapseSpaces(sb.ToString());
    }

    /// <summary>Runs of whitespace → one space, trimmed. A space left before the
    /// retained comma is squeezed out so "Preciado , Amado" and "Preciado, Amado"
    /// normalize identically.</summary>
    private static string CollapseSpaces(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool pendingSpace = false;
        foreach (char ch in s)
        {
            if (ch == ' ')
            {
                if (sb.Length > 0) pendingSpace = true;
                continue;
            }
            if (pendingSpace && ch != ',') sb.Append(' ');
            pendingSpace = false;
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
