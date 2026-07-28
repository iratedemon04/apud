using Marc.Core;
using Marc.Core.Mrk;

namespace Apud.Tests;

/// <summary>
/// The reader must never throw on bad input: it reports located diagnostics and
/// keeps going, because the import wizard's report is built from these.
/// </summary>
public class MrkReaderDiagnosticsTests
{
    [Fact]
    public void Junk_line_is_an_error_with_line_number()
    {
        var read = MrkReader.Read("=LDR  00766nam a22002534i 4500\nno es un campo\n=001  7\n");

        var d = Assert.Single(read.Diagnostics);
        Assert.Equal(MrkSeverity.Error, d.Severity);
        Assert.Equal(2, d.Line);

        // The good fields around it still parsed.
        Assert.Equal("7", read.Records[0].ControlNumber);
    }

    [Fact]
    public void Missing_subfield_code_typo_is_preserved_as_data_not_rejected()
    {
        // The real-world slip: "$Preciado" — delimiter followed by a name, no code.
        // Parse level is permissive: 'P' becomes the code. The validator (Module 9)
        // is what flags it as an illegal subfield code.
        var read = MrkReader.Read("=LDR  00000nam a2200000 i 4500\n=700  1\\$Preciado Vallejo, Gregorio I.$edirector\n");

        Assert.Empty(read.Diagnostics.Where(d => d.Severity == MrkSeverity.Error));
        var f = read.Records[0].FieldsWithTag("700").First();
        Assert.Equal('P', f.Subfields[0].Code);
        Assert.Equal("reciado Vallejo, Gregorio I.", f.Subfields[0].Value);

        // And it must roundtrip unchanged — we never silently rewrite data.
        Assert.Contains("$Preciado Vallejo, Gregorio I.$edirector", MrkWriter.Write(read.Records[0]));
    }

    [Fact]
    public void Record_without_leader_gets_default_and_warning()
    {
        var read = MrkReader.Read("=245  10$aSin líder\n");

        var d = Assert.Single(read.Diagnostics);
        Assert.Equal(MrkSeverity.Warning, d.Severity);
        Assert.Equal(MarcRecord.DefaultBibLeader, read.Records[0].Leader);
        Assert.Equal("Sin líder", read.Records[0].FieldsWithTag("245").First().Subfield('a'));
    }

    [Fact]
    public void Short_leader_is_error_but_record_continues()
    {
        var read = MrkReader.Read("=LDR  0076nam\n=001  3\n");

        Assert.Contains(read.Diagnostics, d => d.Severity == MrkSeverity.Error && d.Line == 1);
        Assert.Equal("3", read.Records[0].ControlNumber);
        Assert.Equal(MarcRecord.DefaultBibLeader, read.Records[0].Leader);
    }

    [Fact]
    public void Missing_blank_line_between_records_warns_but_splits_correctly()
    {
        var read = MrkReader.Read(
            "=LDR  00000nam a2200000 i 4500\n=001  1\n" +
            "=LDR  00000nam a2200000 i 4500\n=001  2\n");

        Assert.Contains(read.Diagnostics, d => d.Severity == MrkSeverity.Warning && d.Line == 3);
        Assert.Equal(2, read.Records.Count);
        Assert.Equal("1", read.Records[0].ControlNumber);
        Assert.Equal("2", read.Records[1].ControlNumber);
    }

    [Fact]
    public void Data_field_without_subfields_is_error()
    {
        var read = MrkReader.Read("=LDR  00000nam a2200000 i 4500\n=245  10\n");
        Assert.Contains(read.Diagnostics, d => d.Severity == MrkSeverity.Error && d.Line == 2);
    }

    [Fact]
    public void Data_field_content_not_starting_with_delimiter_is_error()
    {
        var read = MrkReader.Read("=LDR  00000nam a2200000 i 4500\n=245  10Título sin subcampo\n");
        Assert.Contains(read.Diagnostics, d => d.Severity == MrkSeverity.Error && d.Line == 2);
    }

    [Fact]
    public void Invalid_tag_is_error()
    {
        var read = MrkReader.Read("=LDR  00000nam a2200000 i 4500\n=24A  10$aMal\n");
        Assert.Contains(read.Diagnostics, d => d.Severity == MrkSeverity.Error && d.Message.Contains("24A"));
    }

    [Fact]
    public void Empty_input_gives_no_records_no_diagnostics()
    {
        var read = MrkReader.Read("");
        Assert.Empty(read.Records);
        Assert.Empty(read.Diagnostics);
    }
}
