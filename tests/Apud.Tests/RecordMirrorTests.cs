using Apud.App;
using Marc.Core;
using Marc.Core.Mrk;

namespace Apud.Tests;

/// <summary>
/// The MARC_OUT mirror (user request 2026-07-31): a pushed record is written as
/// its own .mrk beside the catalogue, and deleting a record removes that file.
/// </summary>
public class RecordMirrorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "apud-mirror-" + Guid.NewGuid().ToString("N"));
    private string CatalogPath => Path.Combine(_dir, "catalog.db");

    public RecordMirrorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static MarcRecord Record(string? cn)
    {
        var r = new MarcRecord { Leader = "00000nam a2200000 i 4500" };
        if (cn != null) r.Fields.Add(new MarcField("001") { ControlData = cn });
        var t = new MarcField("245"); t.Subfields.Add(new MarcSubfield('a', "Física cuántica"));
        r.Fields.Add(t);
        return r;
    }

    [Fact]
    public void FolderFor_is_MARC_OUT_beside_the_db()
    {
        Assert.Equal(Path.Combine(_dir, "MARC_OUT"), RecordMirror.FolderFor(CatalogPath));
        Assert.Null(RecordMirror.FolderFor(null));
        Assert.Null(RecordMirror.FolderFor(":memory:"));
    }

    [Fact]
    public void Write_creates_the_folder_and_a_file_named_for_the_001()
    {
        var path = RecordMirror.Write(CatalogPath, Record("758"));

        Assert.Equal(Path.Combine(_dir, "MARC_OUT", "758.mrk"), path);
        Assert.True(File.Exists(path));

        // Round-trips: the file is a real .mrk holding the record.
        var reread = MrkReader.Read(File.ReadAllText(path!)).Records[0];
        Assert.Equal("758", reread.ControlNumber);
        Assert.Equal("Física cuántica", reread.FieldsWithTag("245").Single().Subfield('a'));
    }

    [Fact]
    public void Write_overwrites_the_same_001_on_re_push()
    {
        RecordMirror.Write(CatalogPath, Record("100"));
        var second = Record("100");
        second.FieldsWithTag("245").Single().Subfields[0].Value = "Edited title";
        var path = RecordMirror.Write(CatalogPath, second)!;

        Assert.Equal("Edited title",
            MrkReader.Read(File.ReadAllText(path)).Records[0].FieldsWithTag("245").Single().Subfield('a'));
    }

    [Fact]
    public void Write_skips_a_record_with_no_001()
    {
        Assert.Null(RecordMirror.Write(CatalogPath, Record(null)));
    }

    [Fact]
    public void Delete_removes_the_file_and_a_missing_one_is_harmless()
    {
        var path = RecordMirror.Write(CatalogPath, Record("758"))!;
        Assert.True(File.Exists(path));

        RecordMirror.Delete(CatalogPath, "758");
        Assert.False(File.Exists(path));

        RecordMirror.Delete(CatalogPath, "758"); // already gone — no throw
        RecordMirror.Delete(CatalogPath, "999"); // never existed — no throw
    }
}
