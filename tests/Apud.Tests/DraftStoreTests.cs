using Apud.App;
using Marc.Core;

namespace Apud.Tests;

/// <summary>
/// Drafts live as per-catalogue .mrk files, not DB rows (user, 2026-08-08). The
/// store round-trips a record, routes by base, deletes on demand, keeps two
/// catalogues apart, and never crashes on a bad file.
/// </summary>
public class DraftStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"apud-drafts-{Guid.NewGuid():N}");
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private DraftStore Store(string catalog = @"C:\cat\catalog.db") => new(catalog, _root);

    private static MarcRecord Bib(string title)
    {
        var r = new MarcRecord { Leader = "00000nam a2200000 i 4500" };
        r.Fields.Add(new MarcField("245") { Ind1 = '1', Ind2 = '0' });
        r.Fields[0].Subfields.Add(new MarcSubfield('a', title));
        return r;
    }

    [Fact]
    public void Save_then_LoadAll_round_trips_the_record_and_base()
    {
        var store = Store();
        string id = store.Save(null, "BIB", Bib("Borrador uno"));

        var all = store.LoadAll().ToList();
        Assert.Single(all);
        Assert.Equal(id, all[0].DraftId);
        Assert.Equal("BIB", all[0].Base);
        Assert.Contains(all[0].Record.Fields, f => f.Tag == "245");
    }

    [Fact]
    public void Save_with_an_existing_id_overwrites_the_same_file()
    {
        var store = Store();
        string id = store.Save(null, "AUT", Bib("first"));
        store.Save(id, "AUT", Bib("second")); // same id → same file, not a second draft

        var all = store.LoadAll().ToList();
        Assert.Single(all);
        Assert.Equal("AUT", all[0].Base);
    }

    [Fact]
    public void Delete_removes_the_draft()
    {
        var store = Store();
        string id = store.Save(null, "BIB", Bib("gone"));
        store.Delete(id);
        Assert.Empty(store.LoadAll());
    }

    [Fact]
    public void Two_catalogues_keep_separate_drafts()
    {
        Store(@"C:\one\catalog.db").Save(null, "BIB", Bib("one"));
        var two = Store(@"C:\two\catalog.db");
        Assert.Empty(two.LoadAll());               // a different catalogue sees none of the first's drafts
        two.Save(null, "BIB", Bib("two"));
        Assert.Single(two.LoadAll());
    }

    [Fact]
    public void A_missing_folder_loads_nothing_and_a_bad_file_is_skipped()
    {
        var store = Store();
        Assert.Empty(store.LoadAll());             // nothing saved yet, no folder
        store.Save(null, "BIB", Bib("good"));

        // Drop a junk .mrk beside the good one: it is skipped, the good one survives.
        string dir = Directory.GetDirectories(_root)[0];
        File.WriteAllText(Path.Combine(dir, "BIB_garbage.mrk"), "this is not a MARC record");
        Assert.Single(store.LoadAll());
    }
}
