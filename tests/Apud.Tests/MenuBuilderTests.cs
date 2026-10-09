using Apud.App;

namespace Apud.Tests;

/// <summary>
/// MenuBuilder (MainForm refactor step 4) against a stub command table. MainFormWiringTests
/// pins the real menu tree; these click every item to prove each one runs its own command,
/// and check the keymap and the unregistered-command guard.
/// </summary>
public class MenuBuilderTests
{
    /// <summary>Every command the layout puts on a menu, in menu order.</summary>
    private static readonly string[] MenuIds =
    {
        "catalogue.new", "catalogue.open", "catalogue.import", "catalogue.marc-out",
        "catalogue.marc-out-aut", "catalogue.org-code", "catalogue.export-base",
        "catalogue.export-selected", "sync.configure", "sync.upload", "sync.restore", "app.exit",
        "base.bib", "base.aut", "base.toggle",
        "record.new", "record.save-draft", "record.save-template", "record.undo", "record.redo",
        "field.edit", "field.new", "subfield.new", "field.delete", "field.delete-selected",
        "subfield.delete", "field.copy", "field.paste", "subfield.copy", "subfield.paste",
        "field.fixed-edit", "field.validate", "record.validate", "record.push",
        "record.close", "record.close-all", "record.delete",
        "view.zoom-in", "view.zoom-out", "view.zoom-reset",
        "help.field", "help.intro", "help.backup-time", "help.about",
    };

    /// <summary>A stub table: each command is named by its own id and records its id when run.</summary>
    private static CommandRegistry Registry(List<string> ran, IEnumerable<string> ids, Func<string, string?>? key = null)
    {
        var registry = new CommandRegistry();
        foreach (var id in ids)
            registry.Add(new Command { Id = id, Name = id, DefaultKey = key?.Invoke(id), Execute = () => ran.Add(id) });
        return registry;
    }

    private static IEnumerable<ToolStripMenuItem> Leaves(ToolStripItemCollection items)
    {
        foreach (var item in items.OfType<ToolStripMenuItem>())
        {
            if (item.HasDropDownItems)
                foreach (var leaf in Leaves(item.DropDownItems)) yield return leaf;
            else
                yield return item;
        }
    }

    /// <summary>WinForms controls are built on an STA thread, their home.</summary>
    private static void OnSta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { test(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void Each_menu_item_runs_its_own_command_and_every_command_appears_once() => OnSta(() =>
    {
        var ran = new List<string>();
        var registry = Registry(ran, MenuIds);
        var (menu, _, _) = MenuBuilder.Build(registry, Keymap.Build(registry, json: null));

        var leaves = Leaves(menu.Items).ToList();
        Assert.Equal(MenuIds, leaves.Select(l => l.Text));
        foreach (var leaf in leaves)
        {
            ran.Clear();
            leaf.PerformClick();
            Assert.Equal(new[] { leaf.Text }, ran);
        }
    });

    [Fact]
    public void Shortcut_text_follows_the_keymap_including_a_users_rebind() => OnSta(() =>
    {
        var registry = Registry(new List<string>(), MenuIds,
            id => id switch { "record.push" => "Ctrl+L", "field.new" => "F6", "record.undo" => "Ctrl+Z", _ => null });
        var keymap = Keymap.Build(registry, """{ "record.push": "Ctrl+P", "field.new": "" }""");

        var (menu, _, _) = MenuBuilder.Build(registry, keymap);
        var byId = Leaves(menu.Items).ToDictionary(l => l.Text!);

        Assert.Equal("Ctrl+P", byId["record.push"].ShortcutKeyDisplayString);      // rebound in keymap.json
        Assert.True(string.IsNullOrEmpty(byId["field.new"].ShortcutKeyDisplayString)); // unbound there
        Assert.Equal("Ctrl+Z", byId["record.undo"].ShortcutKeyDisplayString);      // built-in default
        Assert.True(string.IsNullOrEmpty(byId["catalogue.new"].ShortcutKeyDisplayString)); // menu-only
    });

    [Fact]
    public void Returns_the_Base_menus_BIB_and_AUT_items_with_BIB_checked() => OnSta(() =>
    {
        var registry = Registry(new List<string>(), MenuIds);
        var (menu, bib, aut) = MenuBuilder.Build(registry, Keymap.Build(registry, json: null));

        Assert.Equal("base.bib", bib.Text);
        Assert.True(bib.Checked);
        Assert.Equal("base.aut", aut.Text);
        Assert.False(aut.Checked);
        var baseMenu = menu.Items.OfType<ToolStripMenuItem>().Single(m => m.Text == "&Base");
        Assert.Contains(bib, baseMenu.DropDownItems.OfType<ToolStripMenuItem>());
        Assert.Contains(aut, baseMenu.DropDownItems.OfType<ToolStripMenuItem>());
    });

    [Fact]
    public void A_menu_entry_whose_command_is_not_registered_fails_at_build() => OnSta(() =>
    {
        var registry = Registry(new List<string>(), MenuIds.Where(id => id != "help.about"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => MenuBuilder.Build(registry, Keymap.Build(registry, json: null)));
        Assert.Contains("help.about", ex.Message);
    });
}
