using System.Text.Json;

namespace Apud.App;

/// <summary>
/// A tiny per-user UI state file (<c>%APPDATA%\Apud\ui.json</c>). This is the ONE
/// deliberate, user-approved exception to the no-remembered-state rule (user,
/// 2026-08-01, explicit — "just this once you are allowed smart behaviour"): it
/// remembers only the last folder a File dialog used, so Open / New / Import /
/// Export reopen where the cataloguer last was. It never reconnects a catalogue,
/// never restores window or session state, and holds nothing that would open the
/// wrong base out of habit. A missing or corrupt file simply means no memory —
/// loading and saving never throw (a convenience is not worth an error dialog).
/// </summary>
public sealed class AppState
{
    private static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Apud", "ui.json");

    /// <summary>The last folder a File dialog used, or null when unknown.</summary>
    public string? LastFolder { get; set; }

    /// <summary>True once the first-run Setup wizard has been shown, so it stops
    /// appearing on its own at launch (Module 10). This is one-time onboarding state,
    /// not session memory — it never reconnects a catalogue or restores anything, and
    /// the wizard stays reachable any time via Help → Setup. A missing/corrupt file
    /// reads false, so a fresh install shows the wizard exactly once.</summary>
    public bool FirstRunDone { get; set; }

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
