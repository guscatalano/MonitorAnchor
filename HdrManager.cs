using System.Runtime.InteropServices;

namespace MonitorAnchor;

public sealed record HdrInfo(bool Supported, bool Enabled, string Raw = "");

/// <summary>Reads and sets per-monitor HDR state through the DisplayConfig (CCD) API.</summary>
public static class HdrManager
{
    /// <summary>A DisplayConfig path: the target (monitor) and the source (GDI display) driving it.</summary>
    internal readonly record struct Target(Native.LUID AdapterId, uint TargetId, Native.LUID SourceAdapterId, uint SourceId);

    /// <summary>Maps each active GDI device name (\\.\DISPLAYn) to its DisplayConfig target.</summary>
    internal static Dictionary<string, Target> MapActiveTargets()
    {
        var map = new Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out uint numPaths, out uint numModes) != 0) return map;
            var paths = new Native.DISPLAYCONFIG_PATH_INFO[numPaths];
            var modes = new Native.DISPLAYCONFIG_MODE_INFO[numModes];
            if (Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero) != 0) return map;

            for (int i = 0; i < numPaths; i++)
            {
                var src = new Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new Native.DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = paths[i].sourceInfo.adapterId,
                        id = paths[i].sourceInfo.id,
                    },
                    viewGdiDeviceName = string.Empty,
                };
                if (Native.DisplayConfigGetDeviceInfo(ref src) != 0) continue;
                map[src.viewGdiDeviceName] = new Target(paths[i].targetInfo.adapterId, paths[i].targetInfo.id, paths[i].sourceInfo.adapterId, paths[i].sourceInfo.id);
            }
        }
        catch (Exception ex)
        {
            Log.Write("QueryDisplayConfig failed: " + ex.Message);
        }
        return map;
    }

    /// <summary>Returns HDR support/enabled for every active adapter output, keyed by \\.\DISPLAYn.</summary>
    public static Dictionary<string, HdrInfo> QueryAll()
    {
        var result = new Dictionary<string, HdrInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (gdiName, target) in MapActiveTargets())
        {
            var info = Query(target);
            if (info != null) result[gdiName] = info;
        }
        return result;
    }

    private static HdrInfo? Query(Target t)
    {
        // Windows 11 24H2+ separates HDR from wide-colour; prefer it when available.
        var v2 = new Native.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
        {
            header = Header(Native.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2, Marshal.SizeOf<Native.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2>(), t),
        };
        if (Native.DisplayConfigGetDeviceInfo(ref v2) == 0)
            return new HdrInfo(Supported: (v2.value & (1u << 4)) != 0, Enabled: (v2.value & (1u << 5)) != 0,
                               Raw: $"v2 value=0x{v2.value:X} mode={v2.activeColorMode} bpc={v2.bitsPerColorChannel}");

        var v1 = new Native.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            header = Header(Native.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO, Marshal.SizeOf<Native.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(), t),
        };
        if (Native.DisplayConfigGetDeviceInfo(ref v1) == 0)
            return new HdrInfo(Supported: (v1.value & 1u) != 0, Enabled: (v1.value & 2u) != 0, Raw: $"v1 value=0x{v1.value:X}");

        return null;
    }

    /// <summary>Turns HDR on or off for the monitor on the given adapter output. Returns a status string.</summary>
    public static string Set(string gdiName, bool enable)
    {
        if (!MapActiveTargets().TryGetValue(gdiName, out var t)) return "no active DisplayConfig target";

        var hdr = new Native.DISPLAYCONFIG_SET_HDR_STATE
        {
            header = Header(Native.DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE, Marshal.SizeOf<Native.DISPLAYCONFIG_SET_HDR_STATE>(), t),
            value = enable ? 1u : 0u,
        };
        int rc = Native.DisplayConfigSetDeviceInfo(ref hdr);
        if (rc == 0) return "success";

        var legacy = new Native.DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
        {
            header = Header(Native.DISPLAYCONFIG_DEVICE_INFO_SET_ADVANCED_COLOR_STATE, Marshal.SizeOf<Native.DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE>(), t),
            value = enable ? 1u : 0u,
        };
        rc = Native.DisplayConfigSetDeviceInfo(ref legacy);
        return rc == 0 ? "success (legacy API)" : $"error {rc}";
    }

    private static Native.DISPLAYCONFIG_DEVICE_INFO_HEADER Header(uint type, int size, Target t) => new()
    {
        type = type, size = (uint)size, adapterId = t.AdapterId, id = t.TargetId,
    };
}
