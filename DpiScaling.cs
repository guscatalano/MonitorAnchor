using System.Runtime.InteropServices;

namespace MonitorAnchor;

/// <summary>
/// Per-monitor display scaling (the "Scale" percentage in Settings). There is no public API; this uses the
/// DisplayConfig device-info calls the Settings page itself makes. Values are expressed as an index into the
/// fixed table of Windows scale steps, relative to the monitor's recommended step.
/// </summary>
public static class DpiScaling
{
    private static readonly int[] Steps = { 100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500 };

    public sealed record Info(int Percent, int Recommended, int Min, int Max, string Raw = "");

    /// <summary>Current, recommended, minimum and maximum scale for every active adapter output, keyed by \\.\DISPLAYn.</summary>
    public static Dictionary<string, Info> QueryAll()
    {
        var result = new Dictionary<string, Info>(StringComparer.OrdinalIgnoreCase);
        foreach (var (gdiName, t) in HdrManager.MapActiveTargets())
        {
            var info = Query(t);
            if (info != null) result[gdiName] = info;
        }
        return result;
    }

    private static Info? Query(HdrManager.Target t)
    {
        var get = new Native.DISPLAYCONFIG_SOURCE_DPI_SCALE_GET
        {
            header = new Native.DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_DPI_SCALE,
                size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_SOURCE_DPI_SCALE_GET>(),
                adapterId = t.SourceAdapterId,
                id = t.SourceId,
            },
        };
        int rc = Native.DisplayConfigGetDeviceInfo(ref get);
        string raw = $"rc={rc} min={get.minScaleRel} cur={get.curScaleRel} max={get.maxScaleRel} src={t.SourceId}";
        if (rc != 0) return new Info(0, 0, 0, 0, raw);
        return Decode(get.minScaleRel, get.curScaleRel, get.maxScaleRel, raw);
    }

    /// <summary>
    /// Turns the driver's relative indices into percentages. minScaleRel is how many steps below the recommended
    /// one are allowed, so its magnitude is the recommended index. Windows sometimes reports a current value
    /// outside [min, max] (seen on a primary display); it is clamped like the Settings page does.
    /// Percent is 0 when the values make no sense.
    /// </summary>
    internal static Info Decode(int minRel, int curRel, int maxRel, string raw = "")
    {
        int cur = Math.Clamp(curRel, minRel, maxRel);
        int recommendedIdx = Math.Abs(minRel);
        int curIdx = recommendedIdx + cur;
        int maxIdx = recommendedIdx + maxRel;
        if (curIdx < 0 || curIdx >= Steps.Length || recommendedIdx >= Steps.Length) return new Info(0, 0, 0, 0, raw);
        return new Info(Steps[curIdx], Steps[recommendedIdx], Steps[0], Steps[Math.Clamp(maxIdx, 0, Steps.Length - 1)], raw);
    }

    /// <summary>The relative index to send for <paramref name="percent"/> given the recommended percentage, or null if not a step.</summary>
    internal static int? EncodeRel(int percent, int recommended)
    {
        int targetIdx = Array.IndexOf(Steps, percent), recIdx = Array.IndexOf(Steps, recommended);
        return targetIdx < 0 || recIdx < 0 ? null : targetIdx - recIdx;
    }

    /// <summary>Sets the scale for an adapter output. Returns a status string starting with "success" when it worked.</summary>
    public static string Set(string gdiName, int percent)
    {
        if (!HdrManager.MapActiveTargets().TryGetValue(gdiName, out var t)) return "no active DisplayConfig source";
        var current = Query(t);
        if (current == null) return "scale not readable";

        int targetIdx = Array.IndexOf(Steps, percent);
        if (targetIdx < 0) return $"{percent}% is not a Windows scale step";
        int recommendedIdx = Array.IndexOf(Steps, current.Recommended);
        int maxIdx = Array.IndexOf(Steps, current.Max);
        if (targetIdx > maxIdx) return $"{percent}% exceeds this display's maximum of {current.Max}%";

        var set = new Native.DISPLAYCONFIG_SOURCE_DPI_SCALE_SET
        {
            header = new Native.DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = Native.DISPLAYCONFIG_DEVICE_INFO_SET_DPI_SCALE,
                size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_SOURCE_DPI_SCALE_SET>(),
                adapterId = t.SourceAdapterId,
                id = t.SourceId,
            },
            scaleRel = targetIdx - recommendedIdx,
        };
        int rc = Native.DisplayConfigSetDeviceInfo(ref set);
        return rc == 0 ? "success" : $"error {rc}";
    }
}
