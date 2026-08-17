using Apud.App;
using Apud.Data;
using Marc.Core;
using Marc.Core.Mrk;

namespace Apud.Tests;

/// <summary>
/// The editor's contract (Module 6 step 3, user's dumbness rules): any tag may
/// be typed, nothing validated at entry, fields never reordered, display
/// conventions ('^', '_') undone on the way in. All headless — the grid is a
/// thin skin over these operations.
/// </summary>
public class EditorDocumentTests
{
    // Field indices below: the leader lives on the record, NOT in Fields, so
    // 001=0, 008=1, 100=2, 245=3, 650=4.
    private const string Monograph =
        "=LDR  00766nam a22002534i 4500\n" +
        "=001  1\n" +
        "=008  260415s2017    mx            000 0 spa d\n" +
        "=100  1\\$aMoreno, Matías$eautor\n" +
        "=245  10$aGrandes proyectos$bcientíficos\n" +
        "=650  \\4$aFísica nuclear\n";

    private static EditorDocument Doc(string mrk = Monograph) =>
        new(new StoredRecord("BIB", MrkReader.Read(mrk).Records[0]));

    // ---------- cell edits ----------

    [Fact]
    public void Value_edit_marks_dirty_and_sticks()
    {
        var doc = Doc();
        Assert.False(doc.Dirty);

        doc.SetSubfieldValue(2, 1, "coautor"); // 100 $e
        Assert.True(doc.Dirty);
        Assert.Equal("coautor", doc.Record.Fields[2].Subfields[1].Value);
    }

    [Fact]
    public void Control_data_and_leader_edits_undo_the_caret_convention()
    {
        var doc = Doc();
        doc.SetControlData(1, "260415s2017^^^^mx^^^^^^^^^^^^000^0^spa^d"); // 008
        Assert.Equal("260415s2017    mx            000 0 spa d", doc.Record.Fields[1].ControlData);

        Assert.Null(doc.SetLeader("00766nam^a22002534i^4500"));
        Assert.Equal("00766nam a22002534i 4500", doc.Record.Leader);
    }

    [Fact]
    public void Wrong_length_leader_is_refused_with_a_message()
    {
        var doc = Doc();
        Assert.NotNull(doc.SetLeader("short"));
        Assert.Equal("00766nam a22002534i 4500", doc.Record.Leader); // untouched
    }

    [Fact]
    public void Indicator_edit_understands_underscores_and_pads()
    {
        var doc = Doc();
        doc.SetIndicators(3, "0_"); // 245
        Assert.Equal('0', doc.Record.Fields[3].Ind1);
        Assert.Equal(' ', doc.Record.Fields[3].Ind2);

        doc.SetIndicators(3, "1"); // short input pads with blank
        Assert.Equal('1', doc.Record.Fields[3].Ind1);
        Assert.Equal(' ', doc.Record.Fields[3].Ind2);
    }

    // ---------- retagging ----------

    [Fact]
    public void Any_tag_may_be_typed_even_999_or_nonsense()
    {
        var doc = Doc();
        Assert.Null(doc.SetTag(4, "992")); // 650 → 992
        Assert.Equal("992", doc.Record.Fields[4].Tag);
        Assert.Equal("Física nuclear", doc.Record.Fields[4].Subfields[0].Value); // content kept

        Assert.NotNull(doc.SetTag(4, "65")); // 3 characters is structural, not judgment
    }

    [Fact]
    public void Crossing_the_control_data_boundary_needs_an_empty_field()
    {
        var doc = Doc();
        Assert.NotNull(doc.SetTag(1, "500")); // 008 with content can't become a data field
        Assert.Equal("008", doc.Record.Fields[1].Tag);

        int at = doc.InsertBlankFieldAfter(4); // blank field after 650
        Assert.Null(doc.SetTag(at, "500"));    // blank → data field, one blank ‡a
        Assert.Single(doc.Record.Fields[at].Subfields);
        Assert.Null(doc.SetTag(at, "007"));    // still empty → back across is fine
        Assert.True(doc.Record.Fields[at].IsControl);
    }

    [Fact]
    public void Retagging_preserves_the_authority_link()
    {
        var doc = Doc();
        doc.Record.Fields[2].AuthLinkId = 42; // 100
        doc.SetTag(2, "700");
        Assert.Equal(42, doc.Record.Fields[2].AuthLinkId);
    }

    // ---------- structure, never reordered ----------

    [Fact]
    public void New_field_is_blank_and_lands_after_the_cursor_never_sorted()
    {
        var doc = Doc();
        int at = doc.InsertBlankFieldAfter(1); // standing on 008

        Assert.Equal(2, at);
        Assert.Equal("   ", doc.Record.Fields[2].Tag);
        Assert.Equal(new[] { "001", "008", "   ", "100", "245", "650" },
            doc.Record.Fields.Select(f => f.Tag).ToArray());
    }

    [Fact]
    public void Subfield_insert_delete_and_empty_field_edits()
    {
        var doc = Doc();
        var (at, error) = doc.InsertSubfieldAfter(3, 0); // 245, after $a
        Assert.Null(error);
        Assert.Equal(1, at);
        Assert.Equal("a", doc.Record.Fields[3].Subfields[1].Code.ToString());

        Assert.NotNull(doc.InsertSubfieldAfter(1, -1).Error); // 008 control field refuses

        doc.DeleteSubfield(4, 0); // 650 down to zero subfields — field stays
        Assert.Empty(doc.Record.Fields[4].Subfields);

        doc.SetSubfieldValue(4, -1, "Sincrotrón"); // typing a value revives it as ‡a
        Assert.Equal('a', doc.Record.Fields[4].Subfields[0].Code);
        Assert.Equal("Sincrotrón", doc.Record.Fields[4].Subfields[0].Value);

        Assert.Equal(5, doc.Record.Fields.Count);
        doc.DeleteField(4); // remove 650
        Assert.Equal(4, doc.Record.Fields.Count);
    }

    // ---------- copy / paste field & subfield (user request 2026-08-01) ----------

    [Fact]
    public void Copy_and_paste_field_inserts_an_independent_clone_below()
    {
        var doc = Doc();
        var clip = doc.CopyField(4);            // 650 $aFísica nuclear
        int at = doc.PasteFieldAfter(4, clip);  // paste just below it

        Assert.Equal(5, at);
        Assert.Equal(new[] { "001", "008", "100", "245", "650", "650" },
            doc.Record.Fields.Select(f => f.Tag).ToArray());
        Assert.Equal("Física nuclear", doc.Record.Fields[5].Subfields[0].Value);

        // Independent: editing the paste leaves the original untouched.
        doc.Record.Fields[5].Subfields[0].Value = "Óptica";
        Assert.Equal("Física nuclear", doc.Record.Fields[4].Subfields[0].Value);
    }

    [Fact]
    public void A_copied_field_survives_later_edits_to_the_source()
    {
        var doc = Doc();
        var clip = doc.CopyField(4);              // snapshot of 650
        doc.SetSubfieldValue(4, 0, "changed");   // now edit the live field
        int at = doc.PasteFieldAfter(4, clip);

        Assert.Equal("Física nuclear", doc.Record.Fields[at].Subfields[0].Value); // clipboard unchanged
    }

    [Fact]
    public void Pasting_the_same_field_twice_yields_two_independent_fields()
    {
        var doc = Doc();
        var clip = doc.CopyField(4);
        int a = doc.PasteFieldAfter(4, clip);
        int b = doc.PasteFieldAfter(a, clip);

        doc.Record.Fields[a].Subfields[0].Value = "one";
        Assert.Equal("Física nuclear", doc.Record.Fields[b].Subfields[0].Value);
    }

    [Fact]
    public void Copy_and_paste_subfield_clones_after_the_cursor()
    {
        var doc = Doc();                          // 100: $aMoreno, Matías $eautor
        var clip = doc.CopySubfield(2, 0);        // copy $a
        var (index, error) = doc.PasteSubfieldAfter(2, 0, clip);

        Assert.Null(error);
        Assert.Equal(1, index);
        Assert.Equal('a', doc.Record.Fields[2].Subfields[1].Code);
        Assert.Equal("Moreno, Matías", doc.Record.Fields[2].Subfields[1].Value);
        Assert.Equal("autor", doc.Record.Fields[2].Subfields[2].Value); // $e pushed down
    }

    [Fact]
    public void Pasting_a_subfield_into_a_control_field_is_refused()
    {
        var doc = Doc();
        var clip = doc.CopySubfield(2, 0);
        Assert.NotNull(doc.PasteSubfieldAfter(1, 0, clip).Error); // 008 is a control field
    }

    [Fact]
    public void Paste_is_undoable_like_any_edit()
    {
        var doc = Doc();
        var clip = doc.CopyField(4);
        doc.PasteFieldAfter(4, clip);
        Assert.Equal(6, doc.Record.Fields.Count);

        doc.Undo();
        Assert.Equal(5, doc.Record.Fields.Count);
    }

    // ---------- delete several fields at once (user request 2026-08-01) ----------

    [Fact]
    public void DeleteFields_removes_all_of_them_in_one_undoable_step()
    {
        var doc = Doc();
        doc.DeleteFields(new[] { 2, 4 }); // 100 and 650

        Assert.Equal(new[] { "001", "008", "245" },
            doc.Record.Fields.Select(f => f.Tag).ToArray());

        doc.Undo(); // one step brings both back
        Assert.Equal(new[] { "001", "008", "100", "245", "650" },
            doc.Record.Fields.Select(f => f.Tag).ToArray());
    }

    [Fact]
    public void DeleteFields_ignores_the_leader_and_duplicates_and_out_of_order()
    {
        var doc = Doc();
        doc.DeleteFields(new[] { 4, -1, 2, 2 }); // leader ignored, dup collapsed, any order

        Assert.Equal(new[] { "001", "008", "245" },
            doc.Record.Fields.Select(f => f.Tag).ToArray());
    }

    [Fact]
    public void DeleteFields_with_nothing_to_do_records_no_undo()
    {
        var doc = Doc();
        doc.DeleteFields(new[] { -1 }); // only the leader — a no-op
        Assert.False(doc.CanUndo);
        Assert.Equal(5, doc.Record.Fields.Count);
    }

    // ---------- undo / redo ----------

    [Fact]
    public void Undo_and_redo_a_value_edit()
    {
        var doc = Doc();
        doc.SetSubfieldValue(3, 0, "Otro título"); // 245 $a
        Assert.Equal("Otro título", doc.Record.Fields[3].Subfields[0].Value);

        Assert.True(doc.Undo());
        Assert.Equal("Grandes proyectos", doc.Record.Fields[3].Subfields[0].Value);

        Assert.True(doc.Redo());
        Assert.Equal("Otro título", doc.Record.Fields[3].Subfields[0].Value);
    }

    [Fact]
    public void Undo_reverts_structural_edits_without_reordering()
    {
        var doc = Doc();
        doc.InsertBlankFieldAfter(1); // blank after 008
        Assert.Equal(6, doc.Record.Fields.Count);

        doc.Undo();
        Assert.Equal(new[] { "001", "008", "100", "245", "650" },
            doc.Record.Fields.Select(f => f.Tag).ToArray());
    }

    [Fact]
    public void Undo_restores_a_deleted_field_with_its_content_and_place()
    {
        var doc = Doc();
        doc.DeleteField(2); // 100
        Assert.DoesNotContain(doc.Record.Fields, f => f.Tag == "100");

        doc.Undo();
        Assert.Equal(2, doc.Record.Fields.FindIndex(f => f.Tag == "100")); // back in place
        Assert.Equal("Moreno, Matías", doc.Record.Fields[2].Subfields[0].Value);
    }

    [Fact]
    public void Undo_reverts_a_retag_and_its_reshaped_subfields()
    {
        var doc = Doc();
        int at = doc.InsertBlankFieldAfter(4); // blank field, control-shaped
        doc.SetTag(at, "500");                 // becomes a data field with a blank ‡a
        Assert.Single(doc.Record.Fields[at].Subfields);

        doc.Undo();                            // back to the blank field
        Assert.Equal("   ", doc.Record.Fields[at].Tag);
        Assert.Empty(doc.Record.Fields[at].Subfields);
    }

    [Fact]
    public void Dirty_clears_when_undo_returns_to_the_saved_state()
    {
        var doc = Doc(); // loaded from the catalogue → clean
        Assert.False(doc.Dirty);

        doc.SetSubfieldValue(3, 0, "x");
        Assert.True(doc.Dirty);

        doc.Undo();
        Assert.False(doc.Dirty); // exactly back to the saved bytes
    }

    [Fact]
    public void Saving_then_undoing_below_the_save_point_is_dirty_again()
    {
        var doc = Doc();
        doc.SetSubfieldValue(3, 0, "x");
        doc.MarkSaved();
        Assert.False(doc.Dirty);

        doc.Undo();
        Assert.True(doc.Dirty);  // undone past where we saved
        doc.Redo();
        Assert.False(doc.Dirty); // and back
    }

    [Fact]
    public void A_new_edit_discards_the_redo_stack()
    {
        var doc = Doc();
        doc.SetSubfieldValue(3, 0, "a");
        doc.Undo();
        doc.SetSubfieldValue(3, 0, "b"); // diverge from the undone branch

        Assert.False(doc.Redo());
        Assert.Equal("b", doc.Record.Fields[3].Subfields[0].Value);
    }

    [Fact]
    public void Undo_and_redo_are_noops_when_empty()
    {
        var doc = Doc();
        Assert.False(doc.CanUndo);
        Assert.False(doc.Undo());
        Assert.False(doc.CanRedo);
        Assert.False(doc.Redo());
    }

    [Fact]
    public void A_no_op_edit_records_nothing_to_undo()
    {
        var doc = Doc();
        string same = doc.Record.Fields[3].Subfields[0].Value;
        doc.SetSubfieldValue(3, 0, same); // sets the same value back

        Assert.False(doc.CanUndo);
        Assert.False(doc.Dirty);
    }

    // ---------- Ctrl+N copy semantics ----------

    [Fact]
    public void Copy_drops_001_keeps_everything_else_including_links()
    {
        var source = MrkReader.Read(Monograph).Records[0];
        source.Fields[2].AuthLinkId = 9; // 100

        var copy = EditorDocument.CopyWithout001(source);

        Assert.Null(copy.ControlNumber);
        Assert.Equal(9, copy.Fields.First(f => f.Tag == "100").AuthLinkId);
        // Everything but the 001 round-trips byte-identically (check before mutating).
        var expected = MrkWriter.Write(source).Replace("=001  1\n", "");
        Assert.Equal(expected, MrkWriter.Write(copy));
        // Deep copy: editing the copy must not touch the source.
        copy.Fields.First(f => f.Tag == "245").Subfields[0].Value = "changed";
        Assert.Equal("Grandes proyectos", source.Fields[3].Subfields[0].Value); // 245
    }

    // ---------- order fields (Enter, tasks 8/17) ----------

    [Fact]
    public void OrderFields_stable_sorts_by_tag_and_reports_movement()
    {
        var doc = Doc();
        // Put a 500 after the 650 so ordering has work to do.
        int at = doc.InsertBlankFieldAfter(4);
        doc.SetTag(at, "500");

        bool moved = doc.OrderFields();

        Assert.True(moved);
        Assert.Equal(new[] { "001", "008", "100", "245", "500", "650" },
            doc.Record.Fields.Select(f => f.Tag).ToArray());
    }

    [Fact]
    public void OrderFields_keeps_repeated_tags_in_written_order()
    {
        var doc = Doc();
        int a = doc.InsertBlankFieldAfter(doc.Record.Fields.Count - 1);
        doc.SetTag(a, "650");                          // retag adds one blank ‡a
        doc.SetSubfieldValue(a, 0, "Segundo tema");
        int b = doc.InsertBlankFieldAfter(doc.Record.Fields.Count - 1);
        doc.SetTag(b, "650");
        doc.SetSubfieldValue(b, 0, "Tercer tema");

        doc.OrderFields();

        var subjects = doc.Record.Fields.Where(f => f.Tag == "650")
            .Select(f => f.Subfields[0].Value).ToArray();
        Assert.Equal(new[] { "Física nuclear", "Segundo tema", "Tercer tema" }, subjects);
    }

    [Fact]
    public void OrderFields_is_a_single_undo_step()
    {
        var doc = Doc();
        int at = doc.InsertBlankFieldAfter(4);
        doc.SetTag(at, "010");
        doc.MarkSaved();

        doc.OrderFields();
        Assert.Equal("010", doc.Record.Fields[2].Tag); // moved up after 008

        doc.Undo();
        Assert.Equal(new[] { "001", "008", "100", "245", "650", "010" },
            doc.Record.Fields.Select(f => f.Tag).ToArray());
    }

    [Fact]
    public void OrderFields_reports_no_movement_when_already_sorted()
    {
        var doc = Doc();
        Assert.False(doc.OrderFields());
    }

    // ---------- strip empty fields (validate, task 17) ----------

    [Fact]
    public void StripEmptyFields_removes_contentless_fields_regardless_of_indicators_or_code()
    {
        var doc = Doc();
        // A blank data field with indicators + a codeful-but-valueless subfield: no
        // content, so it must go despite carrying indicators and a code.
        int at = doc.InsertBlankFieldAfter(doc.Record.Fields.Count - 1);
        doc.SetTag(at, "999");
        doc.SetIndicators(at, "12");
        doc.SetSubfieldCode(at, 0, "z"); // ‡z but empty value

        int removed = doc.StripEmptyFields();

        Assert.Equal(1, removed);
        Assert.DoesNotContain(doc.Record.Fields, f => f.Tag == "999");
        // The real fields survive.
        Assert.Equal(new[] { "001", "008", "100", "245", "650" },
            doc.Record.Fields.Select(f => f.Tag).ToArray());
    }

    [Fact]
    public void StripEmptyFields_keeps_fields_that_have_any_content()
    {
        var doc = Doc();
        int before = doc.Record.Fields.Count;
        Assert.Equal(0, doc.StripEmptyFields()); // the monograph has no empty fields
        Assert.Equal(before, doc.Record.Fields.Count);
    }

    [Fact]
    public void StripEmptyFields_is_a_single_undo_step()
    {
        var doc = Doc();
        int a = doc.InsertBlankFieldAfter(4);
        doc.SetTag(a, "888"); // empty
        int b = doc.InsertBlankFieldAfter(doc.Record.Fields.Count - 1);
        doc.SetTag(b, "777"); // empty
        doc.MarkSaved();

        Assert.Equal(2, doc.StripEmptyFields());
        doc.Undo();
        Assert.Contains(doc.Record.Fields, f => f.Tag == "888");
        Assert.Contains(doc.Record.Fields, f => f.Tag == "777");
    }

    // ---------- the templates we ship ----------

    [Theory]
    [InlineData("spa-m.mrk", RecordKind.Bibliographic)]
    [InlineData("spa-s.mrk", RecordKind.Bibliographic)]
    [InlineData("spa-a.mrk", RecordKind.Bibliographic)]
    [InlineData("authority.mrk", RecordKind.Authority)]
    public void Shipped_templates_parse_with_sane_skeletons(string file, RecordKind kind)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "templates", file);
        Assert.True(File.Exists(path), $"expected {file} copied to test output");

        var result = MrkReader.Read(File.ReadAllText(path));
        var record = Assert.Single(result.Records);
        Assert.Equal(kind, record.Kind);
        Assert.Equal(24, record.Leader.Length);
        Assert.Equal(40, record.FieldsWithTag("008").Single().ControlData!.Length);
        Assert.Null(record.ControlNumber); // templates never carry a 001
    }
}
