using Marc.Core;
using Marc.Core.Mrk;

namespace Apud.Tests;

/// <summary>
/// The heartbeat suite: parse → write → parse must be lossless, and writing a
/// parsed file must reproduce it byte-for-byte (modulo EOL normalization).
/// Sample records are synthetic but shaped exactly like the BAC catalogue's.
/// </summary>
public class MrkRoundTripTests
{
    // A monograph shaped like the real catalogue's records: Spanish diacritics,
    // blank + numeric indicators, repeated 650s, significant trailing content in 008.
    private const string Monograph =
        "=LDR  00766nam a22002534i 4500\n" +
        "=001  1\n" +
        "=005  20260415154259.0\n" +
        "=008  260415s2017    mx            000 0 spa d\n" +
        "=040  \\\\$aMX-MxBAC$bspa$erda\n" +
        "=082  04$a539.7$bM843 2017$220\n" +
        "=100  1\\$aMoreno, Matías$eautor\n" +
        "=245  10$aGrandes proyectos científicos: Sincrotrón\n" +
        "=250  \\\\$a1a edición\n" +
        "=264  \\1$aMéxico$bEl Colegio Nacional$c2017\n" +
        "=300  \\\\$a304 páginas$c23 cm\n" +
        "=336  \\\\$atexto$btxt$2rdacontent\n" +
        "=650  \\4$aFísica nuclear$xInvestigación$xMéxico\n" +
        "=650  \\4$aSincrotrón\n" +
        "=700  1\\$aNovaro Peñalosa, Octavio$eautor\n" +
        "=852  2\\$eBúfalo\n" +
        "=901  \\\\$aGeneral\n";

    private const string Serial =
        "=LDR  00000nas a22000004i 4500\n" +
        "=001  641\n" +
        "=008  260614c19919999mx uu p       0   a0spa d\n" +
        "=110  2\\$aHonorable Ayuntamiento Constitucional de Colima$eautor\n" +
        "=245  10$aBarco Nuevo$bHistoria, arqueología, arte, cultura y sociedad\n" +
        "=362  0\\$aNo.4 (Enero-Marzo 1991)\n" +
        "=650  \\4$aColima$xHistoria$xPublicaciones periódicas\n";

    [Fact]
    public void Monograph_survives_roundtrip_byte_for_byte()
    {
        var read = MrkReader.Read(Monograph);
        Assert.Empty(read.Diagnostics);
        Assert.Single(read.Records);

        string written = MrkWriter.Write(read.Records[0]);
        Assert.Equal(Monograph, written);
    }

    [Fact]
    public void Spanish_text_is_preserved_literally()
    {
        var rec = MrkReader.Read(Monograph).Records[0];

        Assert.Equal("Grandes proyectos científicos: Sincrotrón", rec.FieldsWithTag("245").First().Subfield('a'));
        Assert.Equal("Novaro Peñalosa, Octavio", rec.FieldsWithTag("700").First().Subfield('a'));
        Assert.Equal("1a edición", rec.FieldsWithTag("250").First().Subfield('a'));
        Assert.Equal("Búfalo", rec.FieldsWithTag("852").First().Subfield('e'));
    }

    [Fact]
    public void Control_field_trailing_and_inner_spaces_are_significant()
    {
        var rec = MrkReader.Read(Monograph).Records[0];
        Assert.Equal("260415s2017    mx            000 0 spa d", rec.FieldsWithTag("008").First().ControlData);
    }

    [Fact]
    public void Indicators_blank_and_numeric_are_parsed_and_rewritten()
    {
        var rec = MrkReader.Read(Monograph).Records[0];

        var f082 = rec.FieldsWithTag("082").First();
        Assert.Equal('0', f082.Ind1);
        Assert.Equal('4', f082.Ind2);

        var f100 = rec.FieldsWithTag("100").First();
        Assert.Equal('1', f100.Ind1);
        Assert.Equal(' ', f100.Ind2);

        var f264 = rec.FieldsWithTag("264").First();
        Assert.Equal(' ', f264.Ind1);
        Assert.Equal('1', f264.Ind2);
    }

    [Fact]
    public void Repeated_fields_keep_order_and_count()
    {
        var rec = MrkReader.Read(Monograph).Records[0];
        var subjects = rec.FieldsWithTag("650").ToList();
        Assert.Equal(2, subjects.Count);
        Assert.Equal("Física nuclear", subjects[0].Subfield('a'));
        Assert.Equal("Sincrotrón", subjects[1].Subfield('a'));
    }

    [Fact]
    public void Multi_record_file_roundtrips_with_blank_line_separator()
    {
        string file = Monograph + "\n" + Serial;
        var read = MrkReader.Read(file);

        Assert.Empty(read.Diagnostics);
        Assert.Equal(2, read.Records.Count);
        Assert.Equal("1", read.Records[0].ControlNumber);
        Assert.Equal("641", read.Records[1].ControlNumber);

        Assert.Equal(file, MrkWriter.Write(read.Records));
    }

    [Fact]
    public void CRLF_input_parses_identically_to_LF()
    {
        var lf = MrkReader.Read(Monograph);
        var crlf = MrkReader.Read(Monograph.Replace("\n", "\r\n"));

        Assert.Empty(crlf.Diagnostics);
        Assert.Equal(MrkWriter.Write(lf.Records[0]), MrkWriter.Write(crlf.Records[0]));
    }

    [Fact]
    public void Utf8_BOM_is_tolerated_on_read()
    {
        var read = MrkReader.Read("\uFEFF" + Monograph);
        Assert.Empty(read.Diagnostics);
        Assert.Single(read.Records);
    }

    [Fact]
    public void Trailing_blank_lines_are_ignored()
    {
        var read = MrkReader.Read(Serial + "\n\n\n\n");
        Assert.Empty(read.Diagnostics);
        Assert.Single(read.Records);
    }

    [Fact]
    public void Literal_dollar_uses_marcmaker_convention()
    {
        var rec = new MarcRecord();
        var f500 = new MarcField("500");
        f500.Subfields.Add(new MarcSubfield('a', "Precio original: $100 pesos"));
        rec.Fields.Add(f500);

        string written = MrkWriter.Write(rec);
        Assert.Contains("$aPrecio original: {dollar}100 pesos", written);

        var back = MrkReader.Read(written);
        Assert.Empty(back.Diagnostics.Where(d => d.Severity == MrkSeverity.Error));
        Assert.Equal("Precio original: $100 pesos", back.Records[0].FieldsWithTag("500").First().Subfield('a'));
    }

    [Fact]
    public void Serial_008_with_inner_spaces_roundtrips()
    {
        var read = MrkReader.Read(Serial);
        Assert.Empty(read.Diagnostics);
        Assert.Equal(Serial, MrkWriter.Write(read.Records[0]));
    }

    [Fact]
    public void Record_kind_is_derived_from_leader()
    {
        Assert.Equal(RecordKind.Bibliographic, MrkReader.Read(Monograph).Records[0].Kind);

        var aut = MrkReader.Read("=LDR  00000nz  a2200000n  4500\n=001  9\n");
        Assert.Equal(RecordKind.Authority, aut.Records[0].Kind);
    }

    [Fact]
    public void ToBytes_produces_utf8_without_bom()
    {
        var rec = MrkReader.Read(Monograph).Records[0];
        byte[] bytes = MrkWriter.ToBytes(new[] { rec });

        Assert.False(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "must not start with a BOM");
        Assert.Equal(Monograph, System.Text.Encoding.UTF8.GetString(bytes));
    }
}
