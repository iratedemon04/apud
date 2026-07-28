using System.Text;

namespace Marc.Core.Mrk;

/// <summary>
/// Writes records as .mrk text in exactly the dialect <see cref="MrkReader"/> reads:
/// "=TAG  " prefix, '\' for blank indicators, '$x' subfields, "{dollar}" for a
/// literal '$' in data, one blank line between records. Output is plain UTF-8
/// (accents literal); encoding/EOL are decided where the file is written —
/// <see cref="ToBytes"/> gives UTF-8 without BOM, LF, matching the BAC files.
/// </summary>
public static class MrkWriter
{
    public static string Write(MarcRecord record)
    {
        var sb = new StringBuilder();
        WriteRecord(sb, record, eol: "\n");
        return sb.ToString();
    }

    public static string Write(IEnumerable<MarcRecord> records)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (var r in records)
        {
            if (!first) sb.Append('\n');
            WriteRecord(sb, r, eol: "\n");
            first = false;
        }
        return sb.ToString();
    }

    /// <summary>UTF-8 without BOM, LF line endings — the canonical on-disk form.</summary>
    public static byte[] ToBytes(IEnumerable<MarcRecord> records) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(Write(records));

    private static void WriteRecord(StringBuilder sb, MarcRecord r, string eol)
    {
        sb.Append("=LDR  ").Append(r.Leader).Append(eol);

        foreach (var f in r.Fields)
        {
            sb.Append('=').Append(f.Tag).Append("  ");

            if (f.IsControl)
            {
                sb.Append(Escape(f.ControlData ?? ""));
            }
            else
            {
                sb.Append(f.Ind1 == ' ' ? '\\' : f.Ind1);
                sb.Append(f.Ind2 == ' ' ? '\\' : f.Ind2);
                foreach (var s in f.Subfields)
                    sb.Append('$').Append(s.Code).Append(Escape(s.Value));
            }

            sb.Append(eol);
        }
    }

    private static string Escape(string s) =>
        s.Contains('$') ? s.Replace("$", "{dollar}") : s;
}
