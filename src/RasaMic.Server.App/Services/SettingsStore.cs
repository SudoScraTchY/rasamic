using System.Text.Json;
using System.Text.Json.Serialization;

namespace RasaMic.Server.App.Services;

public sealed record AppSettings(string MuteHotkey);

[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;

public static class SettingsStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RasaMic", "server-app.json");

    public static AppSettings Load(string? path = null)
    {
        try
        {
            string file = path ?? DefaultPath;
            if (!File.Exists(file))
                return new AppSettings(Hotkey.Default.ToString());
            return JsonSerializer.Deserialize(File.ReadAllText(file), AppSettingsJsonContext.Default.AppSettings)
                ?? new AppSettings(Hotkey.Default.ToString());
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings(Hotkey.Default.ToString());
        }
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        string file = path ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings));
    }
}