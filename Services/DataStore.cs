using System.IO;
using System.Text.Json;
using EveCorporationDashboard.Models;

namespace EveCorporationDashboard.Services;

public static class DataStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    static DataStore()
    {
        // One-time migration from the app's pre-release name, so login and data carry over.
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string oldDir = Path.Combine(appData, "EveMemberTracker");
            if (System.IO.Directory.Exists(oldDir) && !System.IO.Directory.Exists(Directory))
                System.IO.Directory.Move(oldDir, Directory);
        }
        catch { /* fall back to a fresh data folder */ }
    }

    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EveCorporationDashboard");

    private static string SettingsPath => Path.Combine(Directory, "settings.json");
    private static string DataPath => Path.Combine(Directory, "data.json");

    public static AppSettings LoadSettings() => Load<AppSettings>(SettingsPath) ?? new AppSettings();
    public static AppData LoadData() => Load<AppData>(DataPath) ?? new AppData();

    public static void SaveSettings(AppSettings settings) => Save(SettingsPath, settings);
    public static void SaveData(AppData data) => Save(DataPath, data);

    private static T? Load<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private static void Save<T>(string path, T value)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOpts));
    }
}
