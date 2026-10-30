using System.Text.Json;
using System.IO;

namespace LaurusDownloader;

public sealed class UserSettings
{
    public string OutputDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "SpotiFetch Downloads");
    public string Bitrate { get; set; } = "320";
    public int MaxWorkers { get; set; } = 4;
    public bool SkipExisting { get; set; } = true;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpotiFetch", "settings.json");
    private static string LegacyFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LaurusDownloader", "settings.json");

    public static UserSettings Load()
    {
        try
        {
            var source = File.Exists(FilePath) ? FilePath : LegacyFilePath;
            if (File.Exists(source))
                return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(source)) ?? new();
        }
        catch (Exception) { }
        return new UserSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
