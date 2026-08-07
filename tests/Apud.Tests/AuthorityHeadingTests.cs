using Marc.Core;
using Marc.Core.Mrk;

namespace Apud.Tests;

/// <summary>
/// Pulling headings out of authority records and writing an authorized heading
/// back into a bib field — the Marc.Core half of authority control (Module 8),
/// tested without a database.
/// </summary>
public class AuthorityHeadingTests
{
    private static MarcRecord Parse(string mrk) => MrkReader.Read(mrk).Records[0];

    private const string PersonalName =
        "=LDR  00000nz  a2200000n  4500\n" +
        "=001  9\n" +
        "=100  1\\$aPreciado, Amado$d1962-\n" +
        "=400  1\\$aAmado Preciado\n" +
        "=500  1\\$aPreciado Hernández, Amado\n";

    [Fact]
    public void Extract_yields_authorized_see_and_seealso_entries()
    {
        var entries = Headings.Extract(Parse(PersonalName)).ToList();

        Assert.Equal(3, entries.Count);
        Assert.Equal(HeadingKind.Authorized, entries[0].Kind);
        Assert.Equal("100", entries[0].Tag);
        Assert.Equal("Preciado, Amado--1962-", entries[0].Display); // subfields shown "--"-joined (task #12)
        Assert.Equal("preciado, amado 1962", entries[0].Normalized);

        Assert.Equal(HeadingKind.See, entries[1].Kind);
        Assert.Equal("Amado Preciado", entries[1].Display);

        Assert.Equal(HeadingKind.SeeAlso, entries[2].Kind);
    }

    [Fact]
    public void HeadingText_drops_relator_subfields()
    {
        var field = Parse("=LDR  00766nam a22002534i 4500\n=700  1\\$aMoreno, Matías$eautor$4aut\n")
            .FieldsWithTag("700").First();
        Assert.Equal("Moreno, Matías", Headings.HeadingText(field));
    }

    [Fact]
    public void HeadingText_ignores_control_subfields_for_linkage()
    {
        // $0 (auth number), $2 (source), $3, $4, $6 (linkage) and $8 must not become
        // part of the heading being browsed/linked — otherwise the same name links
        // differently depending on how those technical subfields are filled (task #14).
        var field = Parse(
            "=LDR  00766nam a22002534i 4500\n" +
            "=650  \\0$aAbogados$xMéxico$0(DE-101)123$2lcsh$6880-04\n")
            .FieldsWithTag("650").First();
        Assert.Equal("Abogados--México", Headings.HeadingText(field));
    }

    [Fact]
    public void ApplyAuthorizedHeading_writes_the_1XX_and_preserves_the_relator()
    {
        // A bib added-entry with the WRONG form of the name and a relator the
        // cataloguer typed. Linking must adopt the authorized subfields and Ind1
        // but keep the $e.
        var bib = Parse("=LDR  00766nam a22002534i 4500\n=700  0\\$aAmado Preciado$eautor\n");
        var field = bib.FieldsWithTag("700").First();

        bool ok = Headings.ApplyAuthorizedHeading(field, Parse(PersonalName));

        Assert.True(ok);
        Assert.Equal('1', field.Ind1); // adopted from the auth 1XX
        Assert.Equal("Preciado, Amado", field.Subfield('a'));
        Assert.Equal("1962-", field.Subfield('d'));
        Assert.Equal("autor", field.Subfield('e')); // relator preserved, at the end
        Assert.Equal('e', field.Subfields[^1].Code);
    }

    [Fact]
    public void ApplyAuthorizedHeading_refuses_a_record_with_no_1XX()
    {
        var bib = Parse("=LDR  00766nam a22002534i 4500\n=700  1\\$aMoreno\n");
        var field = bib.FieldsWithTag("700").First();
        var authNo1xx = Parse("=LDR  00000nz  a2200000n  4500\n=001  9\n=400  1\\$aMoreno\n");

        Assert.False(Headings.ApplyAuthorizedHeading(field, authNo1xx));
        Assert.Equal("Moreno", field.Subfield('a')); // untouched
    }

    [Theory]
    [InlineData("100", true)]
    [InlineData("650", true)]
    [InlineData("700", true)]
    [InlineData("830", true)]
    [InlineData("245", false)]
    [InlineData("500", false)]
    [InlineData("020", false)]
    public void Controlled_bib_tags_are_recognised(string tag, bool controlled) =>
        Assert.Equal(controlled, Headings.IsControlledBibTag(tag));
}
