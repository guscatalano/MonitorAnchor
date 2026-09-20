using System.Text.Json;

namespace MonitorAnchor;

/// <summary>User preferences, stored next to the profile in settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>When true, the saved layout is re-applied automatically on every display change.</summary>
    public bool Enforce { get; set; } = true;

    /// <summary>When true, the display and system idle timers are held so the screens never sleep.</summary>
    public bool KeepAwake { get; set; } = true;

    /// <summary>
    /// When true, a virtual monitor (Parsec Virtual Display Driver) stands in for each saved monitor that is
    /// unplugged, so windows keep their places. Off by default because it needs the driver installed.
    /// </summary>
    public bool VirtualStandIns { get; set; } = false;

    /// <summary>How long a saved monitor must be missing before a fake one takes its place (0 = immediately).</summary>
    public int StandInDelaySeconds { get; set; } = 10;

    /// <summary>Nudge the mouse one pixel when there has been no input for JiggleIdleSeconds, to look active.</summary>
    public bool JiggleWhenIdle { get; set; } = false;
    public int JiggleIdleSeconds { get; set; } = 60;

    /// <summary>When idle, bring each full-screen Remote Desktop window to the front and press F15 so the remote session stays active.</summary>
    public bool KeepRdpAlive { get; set; } = false;

    /// <summary>Check GitHub for a newer release at startup and daily, and install it automatically.</summary>
    public bool AutoUpdate { get; set; } = true;

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
