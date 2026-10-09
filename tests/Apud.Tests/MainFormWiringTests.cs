using System.Reflection;
using System.Reflection.Emit;
using Apud.App;

namespace Apud.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MainFormWiringCollection
{
    public const string Name = "MainForm wiring";
}

/// <summary>
/// Guards the command wiring through the MainForm refactor
/// (docs/MAINFORM-REFACTOR-PLAN.md). Builds the real <see cref="MainForm"/> once and pins
/// everything a cataloguer reaches by key or menu: every command's id, context and key,
/// the handler it routes to, the menus rendered from the table, and that the shipped
/// keymap.json loads clean against it. A command dropped, rebound or re-routed by an
/// extraction fails here instead of in a cataloguing session. When a handler moves to a
/// collaborator on purpose, update its route below — the diff shows exactly what moved.
///
/// The form is constructed but never shown, so its Load handler (which reopens the last
/// catalogue) never runs. It runs alone (non-parallel collection) because the constructor
/// loads the shipped tag names / field help / validation profiles into static tables that
/// other tests also use; the snapshot resets them afterwards.
/// </summary>
[Collection(MainFormWiringCollection.Name)]
public sealed class MainFormWiringTests : IClassFixture<MainFormWiringTests.Snapshot>
{
    private readonly Snapshot _s;

    public MainFormWiringTests(Snapshot snapshot) => _s = snapshot;

    [Fact]
    public void Command_table_keeps_every_id_context_key_and_name()
    {
        AssertLines("""
            catalogue.new | Global | - | &New Catalogue...
            catalogue.open | Global | Ctrl+O | &Open Catalogue...
            catalogue.import | Global | Ctrl+I | &Import Records...
            catalogue.marc-out | Global | - | Set &BIB Output Folder...
            catalogue.marc-out-aut | Global | - | Set &Authority Output Folder...
            catalogue.org-code | Global | - | Set &Organization Code...
            sync.configure | Global | - | &Set Server...
            sync.upload | Global | Ctrl+U | &Back Up to Server
            sync.restore | Global | - | &Restore from Server...
            catalogue.export-base | Global | - | Export &Base...
            catalogue.export-selected | Global | - | Export &Selected...
            app.exit | Global | Alt+F4 | E&xit
            base.bib | Global | - | &BIB — Bibliographic
            base.aut | Global | - | &AUT — Authority
            base.toggle | Global | Ctrl+B | &Switch Base (BIB ⇄ AUT)
            search.focus | Global | F2 | &Search
            help.field | Global | F1 | &Field Help
            help.intro | Global | - | &Getting Started
            help.backup-time | Global | - | Bac&kup Times
            help.about | Global | - | &About Apud
            record.new | Global | Ctrl+N | &New Record / Copy
            record.save-draft | Editor | Ctrl+D | &Save Draft
            record.save-template | Editor | Ctrl+Shift+T | Save as &Template...
            record.undo | Editor | Ctrl+Z | &Undo
            record.redo | Editor | Ctrl+Y | &Redo
            field.edit | Editor | Insert | &Edit Field (cursor)
            field.order | Editor | Enter | &Order Fields
            field.new | Editor | F6 | New &Field
            subfield.new | Editor | F7 | New Su&bfield
            field.delete | Editor | Ctrl+F5 | &Delete Field
            field.delete-selected | Editor | Ctrl+Shift+F5 | Delete Se&lected Fields
            subfield.delete | Editor | Ctrl+F7 | Delete Subf&ield
            field.copy | Editor | Ctrl+T | &Copy Field
            field.paste | Editor | Alt+T | &Paste Field
            subfield.copy | Editor | Ctrl+S | Copy Subfield
            subfield.paste | Editor | Alt+S | Paste Subfield
            field.fixed-edit | Editor | Ctrl+F3 | Edit Fixed Field by &Position...
            field.validate | Editor | Ctrl+F4 | Browse && Link &Heading
            record.validate | Editor | Ctrl+W | Validate &Record
            record.push | Editor | Ctrl+L | Validate && &Push
            record.close | Global | - | &Close Record
            record.close-all | Global | Ctrl+X | Close &All Records
            record.delete | Editor | Ctrl+Delete | &Delete Record/Draft...
            view.zoom-in | Editor | Ctrl+Plus | Zoom &In
            view.zoom-out | Editor | Ctrl+Minus | Zoom &Out
            view.zoom-reset | Editor | Ctrl+0 | &Reset Zoom
            """,
            _s.Commands.Select(c => $"{c.Id} | {c.Context} | {c.DefaultKey ?? "-"} | {c.Name}").ToList());
    }

    /// <summary>Which method each command runs. A method-group handler shows as
    /// Type.Method; a lambda shows "λ" plus the strings it passes and the methods it
    /// calls, read from its IL — so a BIB/AUT swap or a call to the wrong collaborator
    /// is caught too.</summary>
    [Fact]
    public void Every_command_routes_to_its_handler()
    {
        AssertLines("""
            catalogue.new -> MainForm.NewCatalog
            catalogue.open -> MainForm.OpenCatalogDialog
            catalogue.import -> MainForm.ImportRecords
            catalogue.marc-out -> λ "BIB" MainForm.SetMarcOutFolder
            catalogue.marc-out-aut -> λ "AUT" MainForm.SetMarcOutFolder
            catalogue.org-code -> MainForm.SetOrgCode
            sync.configure -> SyncCoordinator.Configure
            sync.upload -> SyncCoordinator.Upload
            sync.restore -> SyncCoordinator.Restore
            catalogue.export-base -> MainForm.ExportBase
            catalogue.export-selected -> MainForm.ExportSelected
            app.exit -> Form.Close
            base.bib -> λ "BIB" SearchController.SetBase
            base.aut -> λ "AUT" SearchController.SetBase
            base.toggle -> λ SearchController.get_CurrentBase "BIB" String.op_Equality "BIB" "AUT" SearchController.SetBase
            search.focus -> MainForm.ShowSearchView
            help.field -> MainForm.ShowFieldHelp
            help.intro -> MainForm.ShowIntro
            help.backup-time -> MainForm.ShowBackupTime
            help.about -> MainForm.ShowAbout
            record.new -> MainForm.NewRecord
            record.save-draft -> MainForm.SaveDraft
            record.save-template -> MainForm.SaveTemplate
            record.undo -> MainForm.UndoEdit
            record.redo -> MainForm.RedoEdit
            field.edit -> MainForm.BeginEditCurrentCell
            field.order -> MainForm.OrderFieldsCommand
            field.new -> MainForm.NewField
            subfield.new -> MainForm.NewSubfield
            field.delete -> MainForm.DeleteCurrentField
            field.delete-selected -> MainForm.DeleteSelectedFields
            subfield.delete -> MainForm.DeleteCurrentSubfield
            field.copy -> MainForm.CopyField
            field.paste -> MainForm.PasteField
            subfield.copy -> MainForm.CopySubfield
            subfield.paste -> MainForm.PasteSubfield
            field.fixed-edit -> MainForm.EditFixedField
            field.validate -> MainForm.BrowseAndLinkHeading
            record.validate -> MainForm.ValidateRecord
            record.push -> MainForm.PushRecord
            record.close -> MainForm.RemoveSelectedOpenRecords
            record.close-all -> MainForm.RemoveAllOpenRecords
            record.delete -> MainForm.DeleteRecord
            view.zoom-in -> λ RecordGrid.ZoomIn
            view.zoom-out -> λ RecordGrid.ZoomOut
            view.zoom-reset -> λ RecordGrid.ZoomReset
            """,
            _s.Commands.Select(c => $"{c.Id} -> {c.Route}").ToList());
    }

    [Fact]
    public void Menus_render_every_entry_in_order_with_its_shortcut()
    {
        AssertLines("""
            &File
              &New Catalogue...
              &Open Catalogue... [Ctrl+O]
              ---
              &Import Records... [Ctrl+I]
              Set &BIB Output Folder...
              Set &Authority Output Folder...
              Set &Organization Code...
              Export &Base...
              Export &Selected...
              ---
              &Backup Server
                &Set Server...
                &Back Up to Server [Ctrl+U]
                &Restore from Server...
              ---
              E&xit [Alt+F4]
            &Base
              &BIB — Bibliographic (checked)
              &AUT — Authority
              ---
              &Switch Base (BIB ⇄ AUT) [Ctrl+B]
            &Record
              &New Record / Copy [Ctrl+N]
              &Save Draft [Ctrl+D]
              Save as &Template... [Ctrl+Shift+T]
              ---
              &Undo [Ctrl+Z]
              &Redo [Ctrl+Y]
              ---
              &Edit Field (cursor) [Insert]
              New &Field [F6]
              New Su&bfield [F7]
              &Delete Field [Ctrl+F5]
              Delete Se&lected Fields [Ctrl+Shift+F5]
              Delete Subf&ield [Ctrl+F7]
              &Copy Field [Ctrl+T]
              &Paste Field [Alt+T]
              Copy Subfield [Ctrl+S]
              Paste Subfield [Alt+S]
              ---
              Edit Fixed Field by &Position... [Ctrl+F3]
              Browse && Link &Heading [Ctrl+F4]
              Validate &Record [Ctrl+W]
              Validate && &Push [Ctrl+L]
              ---
              &Close Record
              Close &All Records [Ctrl+X]
              &Delete Record/Draft... [Ctrl+Delete]
            &Window
              Zoom &In [Ctrl++]
              Zoom &Out [Ctrl+-]
              &Reset Zoom [Ctrl+0]
            &Help
              &Field Help [F1]
              &Getting Started
              Bac&kup Times
              ---
              &About Apud
            """,
            _s.MenuLines);
    }

    /// <summary>No orphans either way: every leaf menu item is a registered command,
    /// and every command is on a menu or bound to a key. Only two are keyboard-only by
    /// design (F2 search, Enter order-fields).</summary>
    [Fact]
    public void Every_command_is_reachable_from_a_menu_or_a_key()
    {
        var names = _s.Commands.Select(c => c.Name).ToHashSet();
        Assert.All(_s.MenuLeafTexts, text => Assert.Contains(text, names));

        var keyboardOnly = _s.Commands.Where(c => !_s.MenuLeafTexts.Contains(c.Name)).ToList();
        Assert.Equal(new[] { "search.focus", "field.order" }, keyboardOnly.Select(c => c.Id));
        Assert.All(keyboardOnly, c => Assert.NotNull(c.Binding));
    }

    [Fact]
    public void Shipped_keymap_binds_only_registered_commands_without_conflicts()
    {
        Assert.Empty(_s.KeymapDiagnostics);
    }

    private static void AssertLines(string expected, IReadOnlyList<string> actual)
    {
        var want = expected.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.True(want.SequenceEqual(actual),
            "expected:\n" + string.Join("\n", want) + "\n\nactual:\n" + string.Join("\n", actual));
    }

    public sealed record CommandInfo(
        string Id, string Name, CommandContext Context, string? DefaultKey, string? Binding, string Route);

    /// <summary>Builds the form once on an STA thread (WinForms' home) and keeps only
    /// plain data, so the tests never touch a live control.</summary>
    public sealed class Snapshot
    {
        public IReadOnlyList<CommandInfo> Commands { get; }
        public IReadOnlyList<string> MenuLines { get; }
        public IReadOnlySet<string> MenuLeafTexts { get; }
        public IReadOnlyList<string> KeymapDiagnostics { get; }

        public Snapshot()
        {
            List<CommandInfo>? commands = null;
            var menuLines = new List<string>();
            var leaves = new HashSet<string>();
            List<string>? diagnostics = null;
            Exception? failure = null;

            var thread = new Thread(() =>
            {
                try
                {
                    using var form = new MainForm();
                    var keymap = Keymap.LoadFile(form.CommandTable,
                        Path.Combine(AppContext.BaseDirectory, Keymap.FileName));
                    commands = form.CommandTable.Commands
                        .Select(c => new CommandInfo(c.Id, c.Name, c.Context, c.DefaultKey,
                            keymap.BindingFor(c.Id), Route(c.Execute)))
                        .ToList();
                    diagnostics = keymap.Diagnostics.ToList();
                    Render(form.MainMenuStrip!.Items, 0, menuLines, leaves);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    // Put back the static tables the constructor loaded from the shipped files.
                    TagNames.ApplyJson("{}");
                    FieldHelp.ApplyJson("{}");
                    foreach (var b in new[] { "BIB", "AUT" })
                        ValidationProfileConfig.LoadFile(Path.Combine(Path.GetTempPath(), "apud-no-such-profile.json"), b);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure is not null) throw new InvalidOperationException("MainForm failed to construct.", failure);

            Commands = commands!;
            MenuLines = menuLines;
            MenuLeafTexts = leaves;
            KeymapDiagnostics = diagnostics!;
        }

        private static void Render(ToolStripItemCollection items, int depth, List<string> lines, HashSet<string> leaves)
        {
            string indent = new(' ', depth * 2);
            foreach (ToolStripItem item in items)
            {
                if (item is ToolStripSeparator) { lines.Add(indent + "---"); continue; }
                var menuItem = (ToolStripMenuItem)item;
                string line = indent + menuItem.Text;
                if (!string.IsNullOrEmpty(menuItem.ShortcutKeyDisplayString)) line += $" [{menuItem.ShortcutKeyDisplayString}]";
                if (menuItem.Checked) line += " (checked)";
                lines.Add(line);
                if (menuItem.HasDropDownItems) Render(menuItem.DropDownItems, depth + 1, lines, leaves);
                else leaves.Add(menuItem.Text!);
            }
        }

        private static string Route(Action execute)
        {
            MethodInfo method = execute.Method;
            if (!method.Name.StartsWith('<')) return $"{method.DeclaringType!.Name}.{method.Name}";
            return "λ " + string.Join(" ", CallsAndStrings(method));
        }

        private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (OpCode)f.GetValue(null)!)
            .ToDictionary(o => o.Value);

        /// <summary>Walks a lambda's IL and yields, in order, each string literal it
        /// loads and each method it calls.</summary>
        private static IEnumerable<string> CallsAndStrings(MethodInfo method)
        {
            byte[] il = method.GetMethodBody()!.GetILAsByteArray()!;
            for (int i = 0; i < il.Length;)
            {
                short value = il[i] == 0xFE ? (short)(0xFE00 | il[i + 1]) : il[i];
                OpCode op = OpCodesByValue[value];
                i += op.Size;
                switch (op.OperandType)
                {
                    case OperandType.InlineMethod:
                        var callee = method.Module.ResolveMethod(BitConverter.ToInt32(il, i))!;
                        yield return $"{callee.DeclaringType!.Name}.{callee.Name}";
                        break;
                    case OperandType.InlineString:
                        yield return $"\"{method.Module.ResolveString(BitConverter.ToInt32(il, i))}\"";
                        break;
                }
                i += op.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                    _ => 4,
                };
            }
        }
    }
}
