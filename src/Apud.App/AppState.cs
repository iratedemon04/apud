using System.Text.Json;

namespace Apud.App;

/// <summary>
/// A tiny per-user UI state file (<c>%APPDATA%\Apud\ui.json</c>). It remembers the
/// last folder a File dialog used (so Open / New / Import / Export reopen where the
/// cataloguer last was) and the last catalogue opened (so Apud reopens it on launch
/// — user, 2026-08-08, explicitly reversing the earlier "no remembered session
/// state" stance). It restores nothing else — no window geometry, no base, no open
/// records beyond the catalogue's own drafts. A missing or corrupt file simply means
/// no memory — loading and saving never throw (a convenience is not worth an error
/// dialog), and a remembered catalogue that has since moved is silently skipped.
/// </summary>
public sealed class AppState
{
    private static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Apud", "ui.json");

    /// <summary>The last folder a File dialog used, or null when unknown.</summary>
    public string? LastFolder { get; set; }

    /// <summary>The full path of the last catalogue opened, reopened on launch when
    /// it still exists. Null when no catalogue has been opened yet.</summary>
    public string? LastCatalogue { get; set; }

    /// <summary>True once the first-run Setup wizard has been shown, so it stops
    /// appearing on its own at launch (Module 10). This is one-time onboarding state,
    /// not session memory — it never reconnects a catalogue or restores anything, and
    /// the wizard stays reachable any time via Help → Setup. A missing/corrupt file
    /// reads false, so a fresh install shows the wizard exactly once.</summary>
    public bool FirstRunDone { get; set; }

    /// <summary>The record editor's zoom factor (Ctrl++/Ctrl+-), persisted so the
    /// chosen text size survives a restart. Defaults to 1.0 (100%); an older ui.json
    /// without the key keeps that default, and the grid clamps any stray value to its
    /// own range on apply.</summary>
    public float FontScale { get; set; } = 1f;

    /// <summary>Whether Import normalizes coded fixed fields (leader + 006/007/008),
    /// rewriting blank placeholders ('\' and '^' from LC and other exports) as real
    /// spaces. The Import dialog reflects and updates this; it persists so the choice
    /// stays put across runs. Defaults to on — a stray '\'/'^' in a coded position is
    /// wrong once the record becomes binary MARC (user, 2026-08-17).</summary>
    public bool NormalizeFixedFieldsOnImport { get; set; } = true;

    public static AppState Load() => LoadFrom(DefaultPath);
    public void Save() => SaveTo(DefaultPath);

    internal static AppState LoadFrom(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<AppState>(File.ReadAllText(path)) is AppState loaded)
                return loaded;
        }
        catch { /* corrupt or unreadable — start blank, never crash */ }
        return new AppState();
    }

    internal void SaveTo(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
        catch { /* best-effort: remembering a folder must never break a real action */ }
    }
}
