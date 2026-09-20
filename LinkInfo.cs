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
            $"{GdiName}: {Connector}, {ColourFormat} {(BitsPerChannel > 0 ? BitsPerChannel + " bpc" : "")}, {Width}x{Height} @ {RefreshHz:F3} Hz, pixel clock {PixelClockMHz:F1} MHz" +
            (Connector.StartsWith("HDMI") && PixelClockMHz > 0 ? $" (effective {EffectiveTmdsMHz(PixelClockMHz, ColourFormat, BitsPerChannel):F0} MHz)" : "") +
            (HdrOffered ? ", HDR offered" : "");
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

    public sealed record Adapter(string Description, string Version, DateTime? Date)
    {
        public override string ToString() => $"{Description}, driver {Version}{(Date is { } d ? $" ({d:yyyy-MM-dd})" : "")}";
    }

    /// <summary>GPU adapters with driver versions, from the display class registry key (no WMI needed).</summary>
    public static List<Adapter> Adapters()
    {
        var list = new List<Adapter>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return list;
            foreach (var name in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                using var k = cls.OpenSubKey(name);
                if (k?.GetValue("DriverDesc") is not string desc) continue;
                string ver = k.GetValue("DriverVersion") as string ?? "?";
                DateTime? date = DateTime.TryParseExact(k.GetValue("DriverDate") as string ?? "", "M-d-yyyy",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) ? d : null;
                list.Add(new Adapter(desc, ver, date));
            }
        }
        catch (Exception ex) { list.Add(new Adapter("adapter query failed: " + ex.Message, "?", null)); }
        return list;
    }

    // ---- Link health ---------------------------------------------------------------------------------

    /// <summary>HDMI TMDS character rate above this needs the high-speed (scrambled) mode: fine on a good cable, marginal through switches.</summary>
    public const double HdmiHighSpeedMHz = 340;
    /// <summary>Above this HDMI 2.0 is exhausted and the link must use HDMI 2.1 fixed-rate signalling.</summary>
    public const double Hdmi20LimitMHz = 600;
    public static readonly TimeSpan OldDriverAge = TimeSpan.FromDays(548); // 18 months

    /// <summary>Effective TMDS rate in MHz: pixel clock scaled by bit depth for RGB/4:4:4, unscaled for 4:2:2, halved for 4:2:0.</summary>
    internal static double EffectiveTmdsMHz(double pixelClockMHz, string colourFormat, int bpc)
    {
        double factor = colourFormat.Contains("4:2:0") ? 0.5
                      : colourFormat.Contains("4:2:2") ? 1.0
                      : Math.Max(8, bpc) / 8.0;
        return pixelClockMHz * factor;
    }

    /// <summary>Plain-language warnings about links that are likely to flicker, and drivers likely to pick poor timings.</summary>
    internal static List<string> Warnings(IEnumerable<Link> links, IEnumerable<Adapter> adapters, DateTime? now = null)
    {
        var w = new List<string>();
        var today = now ?? DateTime.Now;

        foreach (var l in links)
        {
            if (l.PixelClockMHz <= 0) continue;
            bool hdmi = l.Connector.StartsWith("HDMI", StringComparison.OrdinalIgnoreCase);
            if (!hdmi) continue;
            double tmds = EffectiveTmdsMHz(l.PixelClockMHz, l.ColourFormat, l.BitsPerChannel);
            if (tmds > Hdmi20LimitMHz)
                w.Add($"{l.GdiName}: {tmds:F0} MHz effective HDMI rate exceeds HDMI 2.0 ({Hdmi20LimitMHz:F0} MHz); the link must use HDMI 2.1 signalling, " +
                      "which many KVMs and cables do not carry reliably. Lower the refresh rate, use 8-bit colour, or turn HDR off.");
            else if (tmds > HdmiHighSpeedMHz)
            {
                string why = l.BitsPerChannel > 8 && !l.ColourFormat.Contains("4:2:2")
                    ? $"{l.BitsPerChannel}-bit colour multiplies the {l.PixelClockMHz:F0} MHz pixel clock by {Math.Max(8, l.BitsPerChannel) / 8.0:F2}"
                    : $"the {l.PixelClockMHz:F0} MHz pixel clock is above the threshold on its own";
                w.Add($"{l.GdiName}: {tmds:F0} MHz effective HDMI rate is above {HdmiHighSpeedMHz:F0} MHz, the point where HDMI switches to its high-speed mode " +
                      $"({why}). That mode is marginal through KVMs and long or cheap cables and shows up as blanking or retraining. " +
                      (l.BitsPerChannel > 8 ? "Turning HDR off (8-bit colour) brings it back under the line. " : "") +
                      "A reduced-blanking timing (often what a newer driver picks for 120 Hz) also lowers the pixel clock.");
            }
            if (Math.Abs(l.RefreshHz - Math.Round(l.RefreshHz)) > 0.01 && l.RefreshHz > 0)
                w.Add($"{l.GdiName}: fractional refresh timing ({l.RefreshHz:F3} Hz). Harmless by itself, but if the display or GPU makes noise at this timing, the integer {Math.Round(l.RefreshHz):F0} Hz mode is worth trying.");
        }

        foreach (var a in adapters)
        {
            if (a.Date is { } d && today - d > OldDriverAge && !a.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) && !a.Description.Contains("DisplayLink", StringComparison.OrdinalIgnoreCase))
                w.Add($"{a.Description}: driver dated {d:yyyy-MM-dd} is {(int)((today - d).TotalDays / 30)} months old. Newer drivers fix link handling and often choose lower-clock timings.");
        }
        return w;
    }

    public static List<string> CurrentWarnings() => Warnings(Query(), Adapters());

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
        foreach (var a in Adapters()) sb.Append("  ").AppendLine(a.ToString());
        foreach (var l in Query()) sb.Append("  ").AppendLine(l.ToString());
        return sb.ToString().TrimEnd();
    }
}
