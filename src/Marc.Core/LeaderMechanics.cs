using System.Text;

namespace Marc.Core;

/// <summary>
/// The mechanical leader bytes derived at push time (docs/PLAN.md §8 stage 5):
/// the record length (LDR/00-04) and base address of data (LDR/12-16), computed
/// exactly as ISO 2709 would lay the record out, plus the fixed structural
/// constants (indicator count, subfield-code count, entry map). This is one of
/// the three approved automatic writes (Decisions) — everything else in the
/// leader is the cataloguer's to type.
///
/// Apud's only file format is .mrk, but these bytes are what make a record's
/// leader correct when MarcEdit converts it to binary MARC, so they are worth
/// getting right. Lengths are counted in UTF-8 bytes (Apud is UTF-8 only).
/// </summary>
public static class LeaderMechanics
{
    private const int LeaderLength = 24;
    private const int DirectoryEntryLength = 12;

    /// <summary>Rewrites the record's leader with a freshly computed length and
    /// base address and the MARC21 structural constants, leaving every other
    /// leader position (record status, type, level, encoding...) untouched.</summary>
    public static void Recompute(MarcRecord record)
    {
        var ldr = record.Leader.ToCharArray();

        int fieldCount = record.Fields.Count;
        int directoryLength = fieldCount * DirectoryEntryLength;
        int baseAddress = LeaderLength + directoryLength + 1; // +1 for the terminator after the directory

        int variableLength = 0;
        foreach (var f in record.Fields)
            variableLength += FieldLength(f);

        int recordLength = baseAddress + variableLength + 1; // +1 for the record terminator

        Write5(ldr, 0, recordLength);
        Write5(ldr, 12, baseAddress);

        // MARC21 fixed structural constants.
        ldr[10] = '2';                    // indicator count
        ldr[11] = '2';                    // subfield code length
        ldr[20] = '4'; ldr[21] = '5';     // entry map: length-of-field / starting-char-position
        ldr[22] = '0'; ldr[23] = '0';     // implementation-defined / reserved

        record.Leader = new string(ldr);
    }

    /// <summary>Byte length one field contributes to the record, terminator
    /// included — the value the directory entry would record.</summary>
    private static int FieldLength(MarcField f)
    {
        if (f.IsControl)
            return Utf8(f.ControlData ?? "") + 1;

        int len = 2; // the two indicators (always single ASCII characters)
        foreach (var s in f.Subfields)
            len += 1 /* delimiter */ + 1 /* code */ + Utf8(s.Value);
        return len + 1; // field terminator
    }

    private static int Utf8(string s) => Encoding.UTF8.GetByteCount(s);

    /// <summary>Writes a non-negative integer as a 5-digit zero-padded field,
    /// clamped to 99999 (ISO 2709's ceiling for these positions).</summary>
    private static void Write5(char[] leader, int offset, int value)
    {
        string digits = Math.Min(value, 99999).ToString("D5");
        for (int i = 0; i < 5; i++) leader[offset + i] = digits[i];
    }
}
