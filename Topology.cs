using System.Runtime.InteropServices;

namespace MonitorAnchor;

public sealed record ConnectedTarget(string MonitorId, string FriendlyName, bool IsActive);

/// <summary>Physical-connection view of the display topology, via the DisplayConfig (CCD) API.</summary>
public static class Topology
{
    private sealed class PathSet
    {
        public Native.DISPLAYCONFIG_PATH_INFO[] Paths = Array.Empty<Native.DISPLAYCONFIG_PATH_INFO>();
        public uint Count;
    }

    private static PathSet? QueryAllPaths()
    {
        if (Native.GetDisplayConfigBufferSizes(Native.QDC_ALL_PATHS, out uint numPaths, out uint numModes) != 0) return null;
        var set = new PathSet { Paths = new Native.DISPLAYCONFIG_PATH_INFO[numPaths] };
        var modes = new Native.DISPLAYCONFIG_MODE_INFO[numModes];
        if (Native.QueryDisplayConfig(Native.QDC_ALL_PATHS, ref numPaths, set.Paths, ref numModes, modes, IntPtr.Zero) != 0) return null;
        set.Count = numPaths;
        return set;
    }

    private static string? TargetPath(in Native.DISPLAYCONFIG_PATH_TARGET_INFO t, out string friendly)
    {
        friendly = "Monitor";
        var name = new Native.DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new Native.DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                adapterId = t.adapterId,
                id = t.id,
            },
            monitorFriendlyDeviceName = string.Empty,
            monitorDevicePath = string.Empty,
        };
        if (Native.DisplayConfigGetDeviceInfo(ref name) != 0 || string.IsNullOrEmpty(name.monitorDevicePath)) return null;
        if (!string.IsNullOrWhiteSpace(name.monitorFriendlyDeviceName)) friendly = name.monitorFriendlyDeviceName;
        return name.monitorDevicePath;
    }

    /// <summary>
    /// Every monitor that is physically connected right now, with whether it is part of the desktop.
    /// Unlike EnumDisplayDevices on inactive outputs, this does not report stale registry entries.
    /// </summary>
    public static List<ConnectedTarget> ConnectedMonitors()
    {
        var result = new List<ConnectedTarget>();
        try
        {
            var set = QueryAllPaths();
            if (set == null) return result;

            // QDC_ALL_PATHS lists every source/target combination; collapse to one entry per target.
            var seen = new Dictionary<(uint, int, uint), ConnectedTarget>();
            for (int i = 0; i < set.Count; i++)
            {
                var t = set.Paths[i].targetInfo;
                if (t.targetAvailable == 0) continue;
                bool active = (set.Paths[i].flags & Native.DISPLAYCONFIG_PATH_ACTIVE) != 0;
                var key = (t.adapterId.LowPart, t.adapterId.HighPart, t.id);
                if (seen.TryGetValue(key, out var existing) && (existing.IsActive || !active)) continue;

                var path = TargetPath(in t, out string friendly);
                if (path == null) continue;
                seen[key] = new ConnectedTarget(path, friendly, active);
            }
            result.AddRange(seen.Values);
        }
        catch (Exception ex)
        {
            Log.Write("Topology query failed: " + ex.Message);
        }
        return result;
    }

    /// <summary>
    /// Adds the given monitors (by device path) to the desktop, leaving every other monitor exactly as it is.
    /// Supplies the current active paths plus one free path per wanted target and lets Windows pick modes
    /// from its database; the caller then enforces the saved modes. Falls back to "extend to all" if the
    /// targeted request is rejected. Returns a status string starting with "success" when it worked.
    /// </summary>
    public static string Enable(IReadOnlyCollection<string> monitorIds, bool validateOnly = false)
    {
        string Fallback(string why) => validateOnly ? $"targeted enable failed ({why}); would extend" : ExtendFallback(why);
        try
        {
            var set = QueryAllPaths();
            if (set == null) return Fallback("QueryDisplayConfig failed");

            var chosen = new List<Native.DISPLAYCONFIG_PATH_INFO>();
            var usedSources = new HashSet<(uint, int, uint)>();
            var satisfied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < set.Count; i++)
            {
                if ((set.Paths[i].flags & Native.DISPLAYCONFIG_PATH_ACTIVE) == 0) continue;
                chosen.Add(set.Paths[i]);
                var s = set.Paths[i].sourceInfo;
                usedSources.Add((s.adapterId.LowPart, s.adapterId.HighPart, s.id));
            }

            for (int i = 0; i < set.Count && satisfied.Count < monitorIds.Count; i++)
            {
                var p = set.Paths[i];
                if ((p.flags & Native.DISPLAYCONFIG_PATH_ACTIVE) != 0 || p.targetInfo.targetAvailable == 0) continue;
                var srcKey = (p.sourceInfo.adapterId.LowPart, p.sourceInfo.adapterId.HighPart, p.sourceInfo.id);
                if (usedSources.Contains(srcKey)) continue;

                var path = TargetPath(in p.targetInfo, out _);
                if (path == null || satisfied.Contains(path)) continue;
                if (!monitorIds.Any(id => string.Equals(id, path, StringComparison.OrdinalIgnoreCase))) continue;

                // Inactive paths come back with unspecified target fields; give them the values SetDisplayConfig accepts.
                p.flags = Native.DISPLAYCONFIG_PATH_ACTIVE;
                p.sourceInfo.statusFlags = 0;
                p.targetInfo.statusFlags = 0;
                if (p.targetInfo.rotation == 0) p.targetInfo.rotation = Native.DISPLAYCONFIG_ROTATION_IDENTITY;
                if (p.targetInfo.scaling == 0) p.targetInfo.scaling = Native.DISPLAYCONFIG_SCALING_PREFERRED;
                p.targetInfo.refreshRate = default;
                p.targetInfo.scanLineOrdering = 0;
                chosen.Add(p);
                usedSources.Add(srcKey);
                satisfied.Add(path);
            }

            if (satisfied.Count < monitorIds.Count)
                return Fallback($"no free path for {monitorIds.Count - satisfied.Count} monitor(s)");

            // SDC_TOPOLOGY_SUPPLIED requires mode indices to be unspecified; Windows fills modes from its database.
            var arr = chosen.ToArray();
            for (int i = 0; i < arr.Length; i++)
            {
                arr[i].sourceInfo.modeInfoIdx = Native.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                arr[i].targetInfo.modeInfoIdx = Native.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            }

            // Flag combinations differ in what Windows accepts alongside SDC_TOPOLOGY_SUPPLIED; try the strictest first.
            uint[] ladder =
            {
                Native.SDC_TOPOLOGY_SUPPLIED | Native.SDC_ALLOW_PATH_ORDER_CHANGES, // known good on Windows 11
                Native.SDC_TOPOLOGY_SUPPLIED,
                Native.SDC_TOPOLOGY_SUPPLIED | Native.SDC_ALLOW_PATH_ORDER_CHANGES | Native.SDC_SAVE_TO_DATABASE,
            };
            uint mode = validateOnly ? Native.SDC_VALIDATE : Native.SDC_APPLY;
            int rc = -1;
            foreach (uint f in ladder)
            {
                rc = Native.SetDisplayConfig((uint)arr.Length, arr, 0, IntPtr.Zero, f | mode);
                Log.Write($"SetDisplayConfig({(validateOnly ? "validate" : "apply")} supplied, {arr.Length} path(s), flags 0x{f:X}): {(rc == 0 ? "success" : $"error {rc}")}");
                if (rc == 0) break;
            }
            return rc == 0 ? "success (targeted)" : Fallback($"targeted enable error {rc}");
        }
        catch (Exception ex)
        {
            return Fallback(ex.Message);
        }
    }

    private static string ExtendFallback(string why)
    {
        Log.Write($"Targeted enable not possible ({why}); extending desktop to all connected monitors");
        int rc = Native.SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, Native.SDC_APPLY | Native.SDC_TOPOLOGY_EXTEND);
        Log.Write($"SetDisplayConfig(extend): {(rc == 0 ? "success" : $"error {rc}")}");
        return rc == 0 ? "success (extend)" : $"error {rc}";
    }
}
