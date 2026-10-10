using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace screen_to_search;

internal sealed class AppSettings
{
    public string Language { get; set; } = "en";
    public string SearchEngine { get; set; } = "google";
    public string OcrLanguage { get; set; } = "auto";
    public ShortcutBinding Shortcut { get; set; } = new(0x14, ShortcutModifiers.None);
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HoldKey? ShortcutKey { get; set; } // Migrates settings written by earlier releases.
    public int HoldMilliseconds { get; set; } = 350;
    public bool LaunchAtSignIn { get; set; }

    private static string PathName => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CircleToSearch", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathName)) ?? new();
            if (!NeuralOcrService.SupportsLanguage(settings.OcrLanguage))
                settings.OcrLanguage = "auto";
            if (settings.ShortcutKey is { } legacy && Enum.IsDefined(legacy))
                settings.Shortcut = new(legacy == HoldKey.CapsLock ? 0x14u :
                    legacy == HoldKey.F8 ? 0x77u : (uint)legacy, ShortcutModifiers.None);
            settings.ShortcutKey = null;
            if (!settings.Shortcut.IsValid)
                settings.Shortcut = ShortcutBinding.Disabled;
            return settings;
        }
        catch (IOException) { return new(); }
        catch (JsonException) { return new(); }
        catch (UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            File.WriteAllText(PathName, JsonSerializer.Serialize(this));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
