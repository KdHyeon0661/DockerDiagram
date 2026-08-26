using System;
using System.IO;
using System.Text.Json;

namespace DockerDiagram.Properties;

/// <summary>
/// Persists the small set of user preferences used by the application.
/// This replaces the missing Visual Studio-generated Settings files so
/// command-line builds and packaged builds behave consistently.
/// </summary>
internal sealed class Settings
{
    private static readonly Lazy<Settings> LazyDefault = new(Load);
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DockerDiagram");
    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public static Settings Default => LazyDefault.Value;

    public string LastFilePath { get; set; } = string.Empty;

    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this));
    }

    private static Settings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new Settings();
            }

            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath))
                   ?? new Settings();
        }
        catch
        {
            return new Settings();
        }
    }
}
