using Apud.Data;
using Marc.Core;
using Marc.Core.Mrk;

namespace Apud.Tests;

/// <summary>
/// The data-layer half of authority control (Module 8): the browse index built
/// over pushed AUT records, the positioned browse query, ripple across linked
/// bibs, and the refuse-to-delete-a-linked-authority guard.
/// </summary>
public class HeadingIndexTests : IDisposable
{
    private readonly ApudDatabase _db = ApudDatabase.OpenInMemory();
    private RecordRepository Repo => new(_db);

    public void Dispose() => _db.Dispose();

    private static MarcRecord Parse(string mrk) => MrkReader.Read(mrk).Records[0];

    private static string Auth(string cn, string heading, string? see = null) =>
        $"=LDR  00000nz  a2200000n  4500\n=001  {cn}\n=100  1\\$a{heading}\n" +
        (see is null ? "" : $"=400  1\\$a{see}\n");

    private StoredRecord InsertPushed(string mrk, string @base)
    {
        var stored = new StoredRecord(@base, Parse(mrk));
        Repo.Insert(stored);
        stored.Status = RecordStatus.Pushed;
        Repo.Update(stored);
        return stored;
    }

    // ---------- index build ----------

    [Fact]
    public void Pushed_authority_records_populate_the_browse_index()
    {
        InsertPushed(Auth("1", "Moreno, Matías", see: "Matías Moreno"), "AUT");

        var browse = Repo.BrowseHeadings(HeadingNormalization.Normalize("Moreno"));

        // Both the authorized form and its see-reference are browsable.
        Assert.Contains(browse.Entries, e => e.Kind == HeadingKind.Authorized && e.Display == "Moreno, Matías");
        Assert.Contains(browse.Entries, e => e.Kind == HeadingKind.See && e.Display == "Matías Moreno");
    }

    [Fact]
    public void Draft_authority_records_stay_out_of_the_browse_index()
    {
        var draft = new StoredRecord("AUT", Parse(Auth("1", "Preciado, Amado")));
        Repo.Insert(draft); // never pushed

        Assert.Empty(Repo.BrowseHeadings("").Entries);
    }

    [Fact]
    public void Editing_a_pushed_authority_updates_its_index_rows()
    {
        var aut = InsertPushed(Auth("1", "Moreno, Matias"), "AUT"); // typo: no accent yet
        aut.Record.FieldsWithTag("100").First().Subfields[0].Value = "Moreno, Matías";
        Repo.Update(aut);

        var entries = Repo.BrowseHeadings("").Entries;
        var authorized = Assert.Single(entries, e => e.Kind == HeadingKind.Authorized);
        Assert.Equal("Moreno, Matías", authorized.Display);
    }

    // ---------- positioned browse ----------

    [Fact]
    public void Browse_is_positioned_at_the_first_entry_at_or_after_the_search_point()
    {
        InsertPushed(Auth("1", "Álvarez, Ana"), "AUT");
        InsertPushed(Auth("2", "Moreno, Matías"), "AUT");
        InsertPushed(Auth("3", "Zúñiga, Zoé"), "AUT");

        var browse = Repo.BrowseHeadings(HeadingNormalization.Normalize("Moreno"));

        // Entries come back in normalized order; the cursor lands on Moreno with
        // Álvarez as context above it.
        var ordered = browse.Entries.Select(e => e.Display).ToList();
        Assert.Equal(new[] { "Álvarez, Ana", "Moreno, Matías", "Zúñiga, Zoé" }, ordered);
        Assert.Equal("Moreno, Matías", browse.Entries[browse.Position].Display);
    }

    [Fact]
    public void Browse_ignores_accents_and_case_when_positioning()
    {
        InsertPushed(Auth("1", "Física"), "AUT");
        var browse = Repo.BrowseHeadings(HeadingNormalization.Normalize("fisica"));
        Assert.Equal("Física", browse.Entries[browse.Position].Display);
    }

    [Fact]
    public void Authorized_display_resolves_from_any_of_a_records_headings()
    {
        // The variant sorts far from its authorized form; the browse list still needs
        // the authorized target for the "→ see:" annotation, by record id not by window.
        var aut = InsertPushed(Auth("1", "México--Historia", see: "Historia de México"), "AUT");

        Assert.Equal("México--Historia", Repo.AuthorizedDisplayFor(aut.Id));
    }

    [Fact]
    public void Authorized_display_is_null_when_the_record_has_no_authorized_heading()
    {
        // No pushed authority at all → nothing to resolve.
        Assert.Null(Repo.AuthorizedDisplayFor(9999));
    }

    // ---------- ripple ----------

    [Fact]
    public void Ripple_rewrites_every_linked_bib_field_and_reports_the_count()
    {
        var aut = InsertPushed(Auth("9", "Moreno, Matías"), "AUT");

        // Two bib records, each with a 700 linked to the authority (wrong form).
        var bib1 = LinkedBib("1", "Matías Moreno", aut.Id);
        var bib2 = LinkedBib("2", "M. Moreno", aut.Id);

        // The authority heading is corrected, then rippled out.
        aut.Record.FieldsWithTag("100").First().Subfields[0].Value = "Moreno Pérez, Matías";
        Repo.Update(aut);
        int count = Repo.RewriteLinkedBibHeadings(aut.Id);

        Assert.Equal(2, count);
        Assert.Equal("Moreno Pérez, Matías", Repo.Load(bib1.Id)!.Record.FieldsWithTag("700").First().Subfield('a'));
        Assert.Equal("Moreno Pérez, Matías", Repo.Load(bib2.Id)!.Record.FieldsWithTag("700").First().Subfield('a'));
    }

    private StoredRecord LinkedBib(string cn, string wrongName, long authId)
    {
        var bib = new StoredRecord("BIB",
            Parse($"=LDR  00000nam a2200000 i 4500\n=001  {cn}\n=245  10$aObra {cn}\n=700  0\\$a{wrongName}$eautor\n"));
        Repo.Insert(bib);
        bib.Record.FieldsWithTag("700").First().AuthLinkId = authId;
        Repo.Update(bib);
        return bib;
    }

    [Fact]
    public void Ripple_preserves_the_relator_on_each_linked_field()
    {
        var aut = InsertPushed(Auth("9", "Moreno, Matías"), "AUT");
        var bib = LinkedBib("1", "Matías Moreno", aut.Id);

        Repo.RewriteLinkedBibHeadings(aut.Id);

        Assert.Equal("autor", Repo.Load(bib.Id)!.Record.FieldsWithTag("700").First().Subfield('e'));
    }

    // ---------- refuse delete ----------

    [Fact]
    public void Deleting_a_linked_authority_is_refused()
    {
        var aut = InsertPushed(Auth("9", "Moreno, Matías"), "AUT");
        LinkedBib("1", "Matías Moreno", aut.Id);

        var ex = Assert.Throws<InvalidOperationException>(() => Repo.Delete(aut.Id));
        Assert.Contains("linked", ex.Message);
        Assert.NotNull(Repo.Load(aut.Id)); // still there
    }

    [Fact]
    public void An_unlinked_authority_deletes_and_takes_its_index_rows_with_it()
    {
        var aut = InsertPushed(Auth("9", "Solo, Han"), "AUT");
        Repo.Delete(aut.Id);

        Assert.Null(Repo.Load(aut.Id));
        Assert.Empty(Repo.BrowseHeadings("").Entries); // cascaded away
    }

    // ---------- migration ----------

    [Fact]
    public void Reopening_rebuilds_the_index_from_pushed_authorities()
    {
        // A pushed authority whose index rows are then wiped, simulating a
        // catalogue imported before Module 8 existed.
        InsertPushed(Auth("1", "Moreno, Matías"), "AUT");
        _db.Execute("DELETE FROM heading_index;");
        Assert.Empty(Repo.BrowseHeadings("").Entries);

        // heading_index rebuild is exactly what the v2→v3 migration runs.
        HeadingIndexer.Rebuild(_db.Connection, Repo);

        Assert.Contains(Repo.BrowseHeadings("").Entries, e => e.Display == "Moreno, Matías");
    }
}
