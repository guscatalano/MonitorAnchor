using System.Text.Json;

namespace MonitorAnchor;

/// <summary>
/// All saved layouts, one per set of monitors. The active layout is whichever saved layout's monitors are all
/// connected right now; with several candidates the one covering the most monitors wins, then the newest.
/// Stored in layouts.json; a legacy single profile.json is imported on first load.
/// </summary>
public sealed class LayoutStore
{
    public List<DisplayProfile> Layouts { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    public static string Path => System.IO.Path.Combine(DisplayProfile.ConfigDir, "layouts.json");

    public static LayoutStore Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<LayoutStore>(File.ReadAllText(Path), JsonOpts) ?? new LayoutStore();

            // First run with this version: bring the single profile across.
            var legacy = DisplayProfile.Load();
            var store = new LayoutStore();
            if (legacy != null && legacy.Monitors.Count > 0)
            {
                legacy.Name = string.IsNullOrWhiteSpace(legacy.Name) ? AutoName(legacy) : legacy.Name;
                store.Layouts.Add(legacy);
                store.Save();
                try { File.Move(DisplayProfile.ProfilePath, DisplayProfile.ProfilePath + ".imported", overwrite: true); } catch { /* keep both */ }
                Log.Write($"Imported the existing profile as layout \"{legacy.Name}\"");
            }
            return store;
        }
        catch (Exception ex)
        {
            Log.Write("Failed to load layouts: " + ex.Message);
            return new LayoutStore();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(DisplayProfile.ConfigDir);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, JsonOpts));
    }

    /// <summary>Identifies a set of monitors regardless of order.</summary>
    public static string Signature(DisplayProfile p) =>
        string.Join("|", p.Monitors.Select(m => m.MonitorId.ToUpperInvariant()).OrderBy(s => s, StringComparer.Ordinal));

    /// <summary>"VG259QM ×2", "VG259QM + NE13NY1", ...</summary>
    public static string AutoName(DisplayProfile p)
    {
        var groups = p.Monitors.GroupBy(m => m.MonitorName).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key);
        string name = string.Join(" + ", groups);
        return string.IsNullOrWhiteSpace(name) ? "Layout" : name;
    }

    /// <summary>Adds the captured layout, or replaces the saved layout for the same set of monitors (keeping its name).</summary>
    public DisplayProfile Upsert(DisplayProfile captured)
    {
        string sig = Signature(captured);
        int idx = Layouts.FindIndex(l => Signature(l) == sig);
        if (idx >= 0)
        {
            // Keep a name the user chose; refresh one we generated (monitor names may have improved).
            var old = Layouts[idx];
            captured.Name = old.Name == AutoName(old) ? Unique(AutoName(captured), old) : old.Name;
            Layouts[idx] = captured;
        }
        else
        {
            captured.Name = Unique(AutoName(captured));
            Layouts.Add(captured);
        }
        Save();
        return captured;
    }

    public void Remove(DisplayProfile layout)
    {
        Layouts.Remove(layout);
        Save();
    }

    public void Rename(DisplayProfile layout, string name)
    {
        layout.Name = Unique(name.Trim(), layout);
        Save();
    }

    private string Unique(string name, DisplayProfile? self = null)
    {
        string candidate = name;
        for (int n = 2; Layouts.Any(l => l != self && string.Equals(l.Name, candidate, StringComparison.OrdinalIgnoreCase)); n++)
            candidate = $"{name} ({n})";
        return candidate;
    }

    /// <summary>The layout that fits the monitors connected right now, or null when none does.</summary>
    public DisplayProfile? Select(IEnumerable<string> connectedMonitorIds)
    {
        var connected = connectedMonitorIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Layouts
            .Where(l => l.Monitors.Count > 0 && l.Monitors.All(m => connected.Contains(m.MonitorId)))
            .OrderByDescending(l => l.Monitors.Count)
            .ThenByDescending(l => l.CapturedAt)
            .FirstOrDefault();
    }

    /// <summary>Convenience: the layout for the monitors physically connected now.</summary>
    public DisplayProfile? SelectForCurrentMonitors() =>
        Select(Topology.ConnectedMonitors().Select(t => t.MonitorId));
}
