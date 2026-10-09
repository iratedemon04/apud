namespace Apud.App;

/// <summary>
/// Builds the main menu bar from the command table (docs/MAINFORM-REFACTOR-PLAN.md step 4),
/// extracted from <see cref="MainForm"/>'s constructor. This file is only the menu LAYOUT:
/// which command sits under which menu, in what order, between which separators. Each
/// item's text, click handler and shortcut text come from its <see cref="Command"/> and the
/// <see cref="Keymap"/>, so the menus can never disagree with the keyboard. Putting a
/// command on a menu is one line here; the command itself is still registered in
/// MainForm's command table.
/// </summary>
public static class MenuBuilder
{
    /// <summary>Builds the menu bar. Also returns the Base menu's BIB and AUT items,
    /// because MainForm keeps their checkmarks in step with the active base. Throws if
    /// the layout names a command the table doesn't have — a wiring bug, caught at
    /// startup rather than shipped as a dead menu item.</summary>
    public static (MenuStrip Menu, ToolStripMenuItem BibItem, ToolStripMenuItem AutItem) Build(
        CommandRegistry commands, Keymap keymap)
    {
        var menu = new MenuStrip();

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(MenuItem("catalogue.new"));
        file.DropDownItems.Add(MenuItem("catalogue.open"));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(MenuItem("catalogue.import"));
        file.DropDownItems.Add(MenuItem("catalogue.marc-out"));
        file.DropDownItems.Add(MenuItem("catalogue.marc-out-aut"));
        file.DropDownItems.Add(MenuItem("catalogue.org-code"));
        file.DropDownItems.Add(MenuItem("catalogue.export-base"));
        file.DropDownItems.Add(MenuItem("catalogue.export-selected"));
        file.DropDownItems.Add(new ToolStripSeparator());
        var server = new ToolStripMenuItem("&Backup Server");
        server.DropDownItems.Add(MenuItem("sync.configure"));
        server.DropDownItems.Add(MenuItem("sync.upload"));
        server.DropDownItems.Add(MenuItem("sync.restore"));
        file.DropDownItems.Add(server);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(MenuItem("app.exit"));

        var @base = new ToolStripMenuItem("&Base");
        var bibItem = MenuItem("base.bib");
        bibItem.Checked = true;
        var autItem = MenuItem("base.aut");
        @base.DropDownItems.Add(bibItem);
        @base.DropDownItems.Add(autItem);
        @base.DropDownItems.Add(new ToolStripSeparator());
        @base.DropDownItems.Add(MenuItem("base.toggle"));

        var record = new ToolStripMenuItem("&Record");
        record.DropDownItems.Add(MenuItem("record.new"));
        record.DropDownItems.Add(MenuItem("record.save-draft"));
        record.DropDownItems.Add(MenuItem("record.save-template"));
        record.DropDownItems.Add(new ToolStripSeparator());
        record.DropDownItems.Add(MenuItem("record.undo"));
        record.DropDownItems.Add(MenuItem("record.redo"));
        record.DropDownItems.Add(new ToolStripSeparator());
        record.DropDownItems.Add(MenuItem("field.edit"));
        record.DropDownItems.Add(MenuItem("field.new"));
        record.DropDownItems.Add(MenuItem("subfield.new"));
        record.DropDownItems.Add(MenuItem("field.delete"));
        record.DropDownItems.Add(MenuItem("field.delete-selected"));
        record.DropDownItems.Add(MenuItem("subfield.delete"));
        record.DropDownItems.Add(MenuItem("field.copy"));
        record.DropDownItems.Add(MenuItem("field.paste"));
        record.DropDownItems.Add(MenuItem("subfield.copy"));
        record.DropDownItems.Add(MenuItem("subfield.paste"));
        record.DropDownItems.Add(new ToolStripSeparator());
        record.DropDownItems.Add(MenuItem("field.fixed-edit"));
        record.DropDownItems.Add(MenuItem("field.validate"));
        record.DropDownItems.Add(MenuItem("record.validate"));
        record.DropDownItems.Add(MenuItem("record.push"));
        record.DropDownItems.Add(new ToolStripSeparator());
        record.DropDownItems.Add(MenuItem("record.close"));
        record.DropDownItems.Add(MenuItem("record.close-all"));
        record.DropDownItems.Add(MenuItem("record.delete"));

        var window = new ToolStripMenuItem("&Window");
        window.DropDownItems.Add(MenuItem("view.zoom-in"));
        window.DropDownItems.Add(MenuItem("view.zoom-out"));
        window.DropDownItems.Add(MenuItem("view.zoom-reset"));

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(MenuItem("help.field"));
        help.DropDownItems.Add(MenuItem("help.intro"));
        help.DropDownItems.Add(MenuItem("help.backup-time"));
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(MenuItem("help.about"));

        menu.Items.Add(file);
        menu.Items.Add(@base);
        menu.Items.Add(record);
        menu.Items.Add(window);
        menu.Items.Add(help);

        return (menu, bibItem, autItem);

        // Menu item for a command: text, handler and shortcut display all
        // come from the one table entry, so they can never disagree.
        ToolStripMenuItem MenuItem(string commandId)
        {
            var cmd = commands.Find(commandId)
                ?? throw new InvalidOperationException($"Menu references unregistered command {commandId}.");
            var item = new ToolStripMenuItem(cmd.Name, null, (_, _) => cmd.Execute());
            if (keymap.BindingFor(commandId) is string chord)
                item.ShortcutKeyDisplayString = chord;
            return item;
        }
    }
}
