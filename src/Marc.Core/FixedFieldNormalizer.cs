namespace Marc.Core;

/// <summary>
/// The coded fixed fields — the leader and 006/007/008 — hold coded values or
/// blanks, never free text. Different sources draw a *blank* position differently:
/// LC exports use '\', some hand-keyed or MarcEdit-visualised records use '^', and
/// a clean record uses a literal space. All three mean the same empty position, but
/// only a literal space is correct: once the record becomes binary MARC a stray '\'
/// or '^' turns into a garbage byte in a coded position.
///
/// This rewrites those blank placeholders back to spaces in the leader and in every
/// fixed control field, leaving everything else untouched — the real MARC fill
/// character '|' (a deliberate "no attempt to code", which is meaningful and must
/// survive), every coded value, and all variable fields (where '\' is the .mrk
/// blank-indicator convention the reader already handles, not data). Length is
/// preserved character-for-character, so the leader stays 24 bytes.
/// </summary>
public static class FixedFieldNormalizer
{
    /// <summary>Control tags whose data is a coded fixed field. 001–005/009 carry
    /// free text (control numbers, dates, sources) and are deliberately excluded.</summary>
    private static readonly HashSet<string> FixedControlTags = new() { "006", "007", "008" };

    /// <summary>True for the characters some sources use to draw a blank fixed-field
    /// position. The MARC fill character '|' is intentionally NOT one of them.</summary>
    private static bool IsBlankPlaceholder(char c) => c == '\\' || c == '^';

    /// <summary>Rewrites blank placeholders ('\' and '^') as spaces in the leader and
    /// in 006/007/008. Mutates the record in place and returns the number of characters
    /// changed (0 = already clean), so a caller can report what a run touched.</summary>
    public static int Normalize(MarcRecord record)
    {
        int changed = 0;

        string leader = NormalizeText(record.Leader, ref changed);
        if (!ReferenceEquals(leader, record.Leader))
            record.Leader = leader;

        foreach (var field in record.Fields)
        {
            if (field.IsControl && FixedControlTags.Contains(field.Tag) && field.ControlData is { } data)
            {
                string fixedData = NormalizeText(data, ref changed);
                if (!ReferenceEquals(fixedData, data))
                    field.ControlData = fixedData;
            }
        }

        return changed;
    }

    /// <summary>Character coding scheme, leader position 09: 'a' = UCS/Unicode.</summary>
    public const char UnicodeCoding = 'a';

    /// <summary>Forces the leader's character-coding-scheme byte (LDR/09) to 'a' (Unicode).
    /// Apud stores and exports everything as UTF-8, so a blank there (MARC-8) or any other
    /// value is a lie that makes a downstream binary-MARC converter mojibake the accents.
    /// Mutates the record in place; returns 1 if it changed the byte, else 0. A leader that
    /// is not the required 24 characters is left untouched (the reader/validator owns that).</summary>
    public static int NormalizeEncoding(MarcRecord record)
    {
        string ldr = record.Leader;
        if (ldr.Length != 24 || ldr[9] == UnicodeCoding) return 0;
        record.Leader = ldr[..9] + UnicodeCoding + ldr[10..];
        return 1;
    }

    /// <summary>Returns the same string reference when nothing changed (so callers can
    /// skip the write), otherwise a copy with placeholders turned into spaces.</summary>
    private static string NormalizeText(string s, ref int changed)
    {
        char[]? buf = null;
        for (int i = 0; i < s.Length; i++)
        {
            if (IsBlankPlaceholder(s[i]))
            {
                buf ??= s.ToCharArray();
                buf[i] = ' ';
                changed++;
            }
        }
        return buf is null ? s : new string(buf);
    }
}
