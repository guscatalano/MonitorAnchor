using System.Text.Json;

namespace MonitorAnchor;

/// <summary>User preferences, stored next to the profile in settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>When true, the saved layout is re-applied automatically on every display change.</summary>
    public bool Enforce { get; set; } = true;

    /// <summary>When true, the display and system idle timers are held so the screens never sleep.</summary>
    public bool KeepAwake { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    public static string Path => System.IO.Path.Combine(DisplayProfile.ConfigDir, "settings.json");

    public static bool Exists => File.Exists(Path);

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), JsonOpts) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Write($"Failed to load settings: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DisplayProfile.ConfigDir);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, JsonOpts));
    }
}
