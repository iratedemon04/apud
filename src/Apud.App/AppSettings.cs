using System.Text.Json;

namespace Apud.App;

/// <summary>
/// Application-level settings (NOT catalogue settings — those live in the
/// catalogue's own setting table). Stored as JSON in %AppData%\Apud so the app
/// can find the last catalogue before any catalogue is open.
/// </summary>
public sealed class AppSettings
{
    public string? LastCatalogPath { get; set; }

    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Apud", "settings.json");

    /// <summary>The default home for catalogue files: Documents\Apud.</summary>
    public static string DefaultCatalogFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Apud");

    /// <summary>Loads settings, or returns defaults if the file is missing or unreadable.</summary>
    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            // Corrupt or locked settings never stop the app from starting.
        }
        return new AppSettings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
