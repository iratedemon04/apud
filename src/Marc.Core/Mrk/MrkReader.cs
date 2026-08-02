namespace Marc.Core.Mrk;

/// <summary>
/// Reads MARCMaker-style .mrk text (the dialect Apud treats as canonical, matching
/// MarcEdit and other real-world files):
///
///   =LDR  00766nam a22002534i 4500
///   =008  260415s2017    mx            000 0 spa d
///   =040  \\$aXX-XxLib$bspa$erda
///   =650  \4$aFísica nuclear$xInvestigación
///
/// Rules: "=TAG" + two spaces; '\' = blank indicator; '$x' starts a subfield;
/// "{dollar}" is a literal '$' inside data (MARCMaker convention); records are
/// separated by one or more blank lines; text is plain UTF-8 (accents stored
/// literally, never escaped). Trailing spaces in control-field data are significant
/// and preserved (008 positions!). The reader is permissive — structural sins
/// (e.g. a subfield code that is a capital letter, or 'P' from a typo like
/// "$Preciado") are preserved as data; judging them is the validator's job.
/// </summary>
public static class MrkReader
{
    private const string LiteralDollar = "{dollar}";

    public static MrkReadResult Read(string text)
    {
        var result = new MrkReadResult();
        MarcRecord? current = null;

        // Split preserving nothing but line content; handle CRLF, LF, and a UTF-8 BOM.
        var lines = text.Replace("\uFEFF", "").Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            int lineNo = i + 1;
            // Only CR is stripped: trailing spaces are significant (008 data).
            string line = lines[i].TrimEnd('\r');

            if (line.Length == 0)
            {
                current = null; // blank line closes the current record
                continue;
            }

            if (line[0] != '=' || line.Length < 6 || line[4] != ' ' || line[5] != ' ')
            {
                result.Diagnostics.Add(new MrkDiagnostic(MrkSeverity.Error, lineNo,
                    $"Not a field line (expected \"=TAG  ...\"): \"{Truncate(line)}\""));
                continue;
            }

            string tag = line.Substring(1, 3);
            string rest = line.Length > 6 ? line.Substring(6) : "";

            if (tag == "LDR")
            {
                // LDR always begins a record. If one was already open (no blank line
                // between records), close it with a warning and start fresh.
                if (current != null)
                {
                    result.Diagnostics.Add(new MrkDiagnostic(MrkSeverity.Warning, lineNo,
                        "LDR without a preceding blank line; starting a new record."));
                }
                current = new MarcRecord();
                result.Records.Add(current);

                if (rest.Length == MarcConstants.LeaderLength)
                {
                    current.Leader = rest;
                }
                else
                {
                    result.Diagnostics.Add(new MrkDiagnostic(MrkSeverity.Error, lineNo,
                        $"Leader must be {MarcConstants.LeaderLength} characters, got {rest.Length}; using default."));
                }
                continue;
            }

            if (!tag.All(char.IsAsciiDigit))
            {
                result.Diagnostics.Add(new MrkDiagnostic(MrkSeverity.Error, lineNo,
                    $"Invalid tag \"{tag}\"."));
                continue;
            }

            if (current == null)
            {
                current = new MarcRecord();
                result.Records.Add(current);
                result.Diagnostics.Add(new MrkDiagnostic(MrkSeverity.Warning, lineNo,
                    $"Record starts with ={tag} instead of =LDR; using default leader."));
            }

            var field = new MarcField(tag);

            if (field.IsControl)
            {
                field.ControlData = Unescape(rest);
            }
            else
            {
                if (rest.Length < 2)
                {
                    result.Diagnostics.Add(new MrkDiagnostic(MrkSeverity.Error, lineNo,
                        $"Data field ={tag} is missing its indicators."));
                    continue;
                }

                field.Ind1 = rest[0] == '\\' ? ' ' : rest[0];
                field.Ind2 = rest[1] == '\\' ? ' ' : rest[1];

                string body = rest.Substring(2);
                if (!ParseSubfields(body, field, result, lineNo, tag))
                    continue;
            }

            current.Fields.Add(field);
        }

        return result;
    }

    private static bool ParseSubfields(string body, MarcField field, MrkReadResult result, int lineNo, string tag)
    {
        if (body.Length == 0)
        {
            result.Diagnostics.Add(new MrkDiagnostic(MrkSeverity.Error, lineNo,
                $"Data field ={tag} has no subfields."));
            return false;
        }

        if (body[0] != '$')
        {
            result.Diagnostics.Add(new MrkDiagnostic(MrkSeverity.Error, lineNo,
                $"Data field ={tag}: content after indicators must start with a subfield ('$')."));
            return false;
        }

        // Split on '$'; "{dollar}" placeholders contain no '$' so they survive the split.
        int pos = 1;
        while (pos <= body.Length)
        {
            int next = body.IndexOf('$', pos);
            string chunk = next < 0 ? body.Substring(pos) : body.Substring(pos, next - pos);

            if (chunk.Length == 0)
            {
                result.Diagnostics.Add(new MrkDiagnostic(MrkSeverity.Error, lineNo,
                    $"Data field ={tag}: empty subfield (a '$' with no code)."));
            }
            else
            {
                field.Subfields.Add(new MarcSubfield(chunk[0], Unescape(chunk.Substring(1))));
            }

            if (next < 0) break;
            pos = next + 1;
        }

        return field.Subfields.Count > 0;
    }

    private static string Unescape(string s) =>
        s.Contains(LiteralDollar) ? s.Replace(LiteralDollar, "$") : s;

    private static string Truncate(string s) => s.Length <= 60 ? s : s.Substring(0, 57) + "...";
}
