using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorAnchor;

/// <summary>The settings we persist for one physical monitor.</summary>
public sealed class MonitorSettings
{
    /// <summary>Device-interface path of the monitor (\\?\DISPLAY#VENDOR1234#...). Stable for a given monitor on a given port.</summary>
    public string MonitorId { get; set; } = string.Empty;
    /// <summary>Friendly name reported by the driver, for display only.</summary>
    public string MonitorName { get; set; } = string.Empty;
    /// <summary>Adapter output name at capture time (\\.\DISPLAY1). Informational; may differ at apply time.</summary>
    public string AdapterName { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public int RefreshRate { get; set; }
    public int BitsPerPixel { get; set; }
    public int PositionX { get; set; }
    public int PositionY { get; set; }
    /// <summary>0 = landscape, 1 = 90 deg, 2 = 180 deg, 3 = 270 deg (DMDO_*).</summary>
    public int Orientation { get; set; }
    public bool IsPrimary { get; set; }
    /// <summary>Whether the monitor reported HDR capability when captured.</summary>
    public bool HdrSupported { get; set; }
    /// <summary>Whether HDR was switched on when captured. Only enforced when HdrSupported is true.</summary>
    public bool HdrEnabled { get; set; }

    /// <summary>Display scaling percentage (100, 125, 150, ...) when captured; 0 = unknown, never enforced.</summary>
    public int DpiScale { get; set; }

    /// <summary>Runtime only: false when the monitor is connected but not part of the desktop (disabled).</summary>
    [JsonIgnore]
    public bool IsActive { get; set; } = true;

    [JsonIgnore]
    public string ModelKey => DisplayProfile.ModelKeyOf(MonitorId);

    /// <summary>Mode-only comparison (HDR is handled separately because it is applied through a different API).</summary>
    public bool SameModeAs(MonitorSettings other) =>
        other.IsActive && Width == other.Width && Height == other.Height && RefreshRate == other.RefreshRate &&
        BitsPerPixel == other.BitsPerPixel && PositionX == other.PositionX && PositionY == other.PositionY &&
        Orientation == other.Orientation && IsPrimary == other.IsPrimary;

    public override string ToString() => !IsActive
        ? $"{MonitorName} [{AdapterName}] connected but disabled"
        : $"{MonitorName} [{AdapterName}] {Width}x{Height} @ {RefreshRate}Hz, pos ({PositionX},{PositionY}), rot {Orientation * 90}\u00B0" +
          $"{(IsPrimary ? ", primary" : "")}{(DpiScale > 0 ? $", {DpiScale}%" : "")}{(HdrSupported ? (HdrEnabled ? ", HDR on" : ", HDR off") : "")}";
}

/// <summary>A snapshot of every active monitor, plus persistence helpers.</summary>
public sealed class DisplayProfile
{
    /// <summary>User-visible name; generated from the monitor names unless renamed.</summary>
    public string Name { get; set; } = string.Empty;
    public DateTime CapturedAt { get; set; }
    public List<MonitorSettings> Monitors { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>Tests point this at a temporary folder so nothing touches the real data folder.</summary>
    public static string? ConfigDirOverride;

    public static string ConfigDir =>
        ConfigDirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorAnchor");

    public static string ProfilePath => Path.Combine(ConfigDir, "profile.json");

    /// <summary>Reduces a device-interface path to vendor+product ("\\?\DISPLAY#GSM5B09") so a monitor moved to another port can still match.</summary>
    public static string ModelKeyOf(string monitorId)
    {
        // \\?\DISPLAY#GSM5B09#5&2e0e5d7d&0&UID4353#{e6f07b5f-...}
        var parts = monitorId.Split('#');
        return parts.Length >= 2 ? (parts[0] + "#" + parts[1]).ToUpperInvariant() : monitorId.ToUpperInvariant();
    }

    public static DisplayProfile? Load()
    {
        try
        {
            if (!File.Exists(ProfilePath)) return null;
            return JsonSerializer.Deserialize<DisplayProfile>(File.ReadAllText(ProfilePath), JsonOpts);
        }
        catch (Exception ex)
        {
            Log.Write($"Failed to load profile: {ex.Message}");
            return null;
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ProfilePath, JsonSerializer.Serialize(this, JsonOpts));
    }

    /// <summary>Finds the saved settings for a currently-connected monitor: exact device path first, then unique model match.</summary>
    public MonitorSettings? FindFor(MonitorSettings current)
    {
        var exact = Monitors.FirstOrDefault(m => string.Equals(m.MonitorId, current.MonitorId, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        var byModel = Monitors.Where(m => m.ModelKey == current.ModelKey).ToList();
        return byModel.Count == 1 ? byModel[0] : null;
    }

    public override string ToString() =>
        Monitors.Count == 0 ? "(no monitors)" : string.Join(Environment.NewLine, Monitors.Select(m => "\u2022 " + m));

    /// <summary>Human-readable differences between a live capture and this saved layout, per monitor. Empty when they match.</summary>
    public List<string> DifferencesFrom(DisplayProfile live)
    {
        var diffs = new List<string>();
        foreach (var l in live.Monitors.Where(m => m.IsActive))
        {
            var s = FindFor(l);
            if (s == null) continue;
            var parts = new List<string>();
            if (s.Width != l.Width || s.Height != l.Height) parts.Add($"{s.Width}x{s.Height} -> {l.Width}x{l.Height}");
            if (s.RefreshRate != l.RefreshRate) parts.Add($"{s.RefreshRate} Hz -> {l.RefreshRate} Hz");
            if (s.PositionX != l.PositionX || s.PositionY != l.PositionY) parts.Add($"position ({s.PositionX},{s.PositionY}) -> ({l.PositionX},{l.PositionY})");
            if (s.Orientation != l.Orientation) parts.Add($"rotation {s.Orientation * 90}\u00b0 -> {l.Orientation * 90}\u00b0");
            if (s.IsPrimary != l.IsPrimary) parts.Add(l.IsPrimary ? "now primary" : "no longer primary");
            if (s.HdrSupported && l.HdrSupported && s.HdrEnabled != l.HdrEnabled) parts.Add($"HDR {(s.HdrEnabled ? "on" : "off")} -> {(l.HdrEnabled ? "on" : "off")}");
            if (s.DpiScale > 0 && l.DpiScale > 0 && s.DpiScale != l.DpiScale) parts.Add($"scale {s.DpiScale}% -> {l.DpiScale}%");
            if (parts.Count > 0) diffs.Add($"{l.MonitorName} ({l.AdapterName}): {string.Join(", ", parts)}");
        }
        return diffs;
    }
}
