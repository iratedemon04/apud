namespace Apud.App;

/// <summary>Where a command is live: Global works everywhere; Search only while
/// the search screen shows; Editor only while the record screen shows.</summary>
public enum CommandContext
{
    Global,
    Search,
    Editor,
}

/// <summary>
/// One application action (Module 6 step 1). Menus render from these and
/// keymap.json binds keys to their ids — text, handler and shortcut always
/// agree because there is exactly one place they are defined.
/// </summary>
public sealed class Command
{
    /// <summary>Stable id keymap.json binds against, e.g. "catalogue.new".</summary>
    public required string Id { get; init; }

    /// <summary>Menu text; '&' mnemonics allowed.</summary>
    public required string Name { get; init; }

    public CommandContext Context { get; init; } = CommandContext.Global;

    /// <summary>Built-in key chord ("Ctrl+L"), or null for unbound/menu-only.</summary>
    public string? DefaultKey { get; init; }

    public required Action Execute { get; init; }
}

/// <summary>The command table: every action the app can perform, in menu order.</summary>
public sealed class CommandRegistry
{
    private readonly List<Command> _commands = new();
    private readonly Dictionary<string, Command> _byId = new();

    public IReadOnlyList<Command> Commands => _commands;

    public void Add(Command cmd)
    {
        if (!_byId.TryAdd(cmd.Id, cmd))
            throw new InvalidOperationException($"Duplicate command id: {cmd.Id}");
        _commands.Add(cmd);
    }

    public Command? Find(string id) => _byId.GetValueOrDefault(id);
}
