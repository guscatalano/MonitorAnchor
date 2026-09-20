using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MonitorAnchor;

/// <summary>
/// The facts that decide whether a display link is comfortable: connector type, colour format and bit depth,
/// the exact refresh timing and the pixel clock, plus the GPU, driver and OS build. Two machines' dumps can
/// then be compared side by side instead of guessed at.
/// </summary>
public static class LinkInfo
{
    public sealed record Link(string GdiName, string Connector, string ColourFormat, int BitsPerChannel, double RefreshHz, double PixelClockMHz, int Width, int Height, bool HdrOffered)
    {
        public override string ToString() =>
            $"{GdiName}: {Connector}, {ColourFormat} {(BitsPerChannel > 0 ? BitsPerChannel + " bpc" : "")}, {Width}x{Height} @ {RefreshHz:F3} Hz, pixel clock {PixelClockMHz:F1} MHz{(HdrOffered ? ", HDR offered" : "")}";
    }

    /// <summary>One entry per active adapter output.</summary>
    public static List<Link> Query()
    {
        var result = new List<Link>();
        try
        {
            if (Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out uint numPaths, out uint numModes) != 0) return result;
            var paths = new Native.DISPLAYCONFIG_PATH_INFO[numPaths];
            var modes = new Native.DISPLAYCONFIG_MODE_INFO[numModes];
            if (Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero) != 0) return result;

            for (int i = 0; i < numPaths; i++)
            {
                var p = paths[i];
                var src = new Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new Native.DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = p.sourceInfo.adapterId, id = p.sourceInfo.id,
                    },
                    viewGdiDeviceName = string.Empty,
                };
                string gdi = Native.DisplayConfigGetDeviceInfo(ref src) == 0 ? src.viewGdiDeviceName : $"path {i}";

                double hz = 0, mhz = 0; int w = 0, h = 0;
                uint mi = p.targetInfo.modeInfoIdx;
                if (mi < numModes && modes[mi].infoType == Native.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET)
                {
                    var m = modes[mi];
                    if (m.vSyncFreq.Denominator != 0) hz = (double)m.vSyncFreq.Numerator / m.vSyncFreq.Denominator;
                    mhz = m.pixelRate / 1_000_000.0;
                    w = (int)m.activeCx; h = (int)m.activeCy;
                }
                else if (p.targetInfo.refreshRate.Denominator != 0)
                {
                    hz = (double)p.targetInfo.refreshRate.Numerator / p.targetInfo.refreshRate.Denominator;
                }

                string colour = "unknown"; int bpc = 0; bool hdr = false;
                var ci = new Native.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
                {
                    header = new Native.DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO,
                        size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(),
                        adapterId = p.targetInfo.adapterId, id = p.targetInfo.id,
                    },
                };
                if (Native.DisplayConfigGetDeviceInfo(ref ci) == 0)
                {
                    colour = ci.colorEncoding switch { 0 => "RGB", 1 => "YCbCr 4:4:4", 2 => "YCbCr 4:2:2", 3 => "YCbCr 4:2:0", 4 => "intensity", _ => $"encoding {ci.colorEncoding}" };
                    bpc = (int)ci.bitsPerColorChannel;
                    hdr = (ci.value & 1u) != 0;
                }

                result.Add(new Link(gdi, Connector(p.targetInfo.outputTechnology), colour, bpc, hz, mhz, w, h, hdr));
            }
        }
        catch (Exception ex)
        {
            Log.Write("Link query failed: " + ex.Message);
        }
        return result;
    }

    private static string Connector(uint tech) => tech switch
    {
        0 => "VGA", 1 => "S-Video", 2 => "composite", 3 => "component", 4 => "DVI", 5 => "HDMI", 6 => "LVDS", 8 => "D-terminal", 9 => "SDI",
        10 => "DisplayPort", 11 => "embedded DisplayPort", 12 => "UDI", 13 => "embedded UDI", 14 => "SDTV dongle", 15 => "Miracast",
        16 => "indirect (wired)", 17 => "indirect (virtual)", 18 => "DisplayPort over USB", 0x80000000 => "internal", _ => $"connector {tech}",
    };

    /// <summary>GPU adapters with driver versions, from the display class registry key (no WMI needed).</summary>
    public static List<string> Adapters()
    {
        var list = new List<string>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return list;
            foreach (var name in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                using var k = cls.OpenSubKey(name);
                if (k?.GetValue("DriverDesc") is not string desc) continue;
                string ver = k.GetValue("DriverVersion") as string ?? "?";
                string date = k.GetValue("DriverDate") as string ?? "";
                list.Add($"{desc}, driver {ver}{(date.Length > 0 ? $" ({date})" : "")}");
            }
        }
        catch (Exception ex) { list.Add("adapter query failed: " + ex.Message); }
        return list;
    }

    public static string OsBuild()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string product = k?.GetValue("ProductName") as string ?? "Windows";
            string display = k?.GetValue("DisplayVersion") as string ?? "";
            string build = k?.GetValue("CurrentBuildNumber") as string ?? Environment.OSVersion.Version.Build.ToString();
            int ubr = k?.GetValue("UBR") is int u ? u : 0;
            return $"{product} {display} build {build}.{ubr}".Replace("  ", " ");
        }
        catch { return Environment.OSVersion.VersionString; }
    }

    /// <summary>One block for the startup log and diagnostics.</summary>
    public static string Describe()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("  ").AppendLine(OsBuild());
        foreach (var a in Adapters()) sb.Append("  ").AppendLine(a);
        foreach (var l in Query()) sb.Append("  ").AppendLine(l.ToString());
        return sb.ToString().TrimEnd();
    }
}
