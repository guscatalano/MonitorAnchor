using System.Text;

namespace MonitorAnchor;

/// <summary>Builds the diagnostic report shown by "Show diagnostics..." and written by --dump.</summary>
public static class Diagnostics
{
    public static string DumpPath => Path.Combine(DisplayProfile.ConfigDir, "dump.txt");

    public static string Build(DisplayProfile? saved)
    {
        var text = new StringBuilder();
        text.AppendLine($"Monitor Anchor {Updater.Current} ({Updater.AssetName})  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"Data folder: {DisplayProfile.ConfigDir}");
        text.AppendLine();

        text.AppendLine("SAVED LAYOUT");
        text.AppendLine(saved == null ? "  (none)" : Indent(saved.ToString()));
        if (saved != null) text.AppendLine($"  captured {saved.CapturedAt:g}");
        text.AppendLine();

        var live = DisplayManager.CaptureAll();
        text.AppendLine("LIVE LAYOUT");
        text.AppendLine(Indent(live.ToString()));
        text.AppendLine();

        text.AppendLine("MONITOR IDS");
        foreach (var m in live.Monitors) text.AppendLine($"  {m.MonitorName,-20} {m.MonitorId}");
        text.AppendLine();

        text.AppendLine("HDR (per adapter output)");
        var hdr = HdrManager.QueryAll();
        if (hdr.Count == 0) text.AppendLine("  none reported");
        foreach (var (name, h) in hdr) text.AppendLine($"  {name}: supported={h.Supported} enabled={h.Enabled} ({h.Raw})");
        text.AppendLine();

        var connected = Topology.ConnectedMonitors();
        text.AppendLine("CONNECTED MONITORS (DisplayConfig)");
        foreach (var t in connected) text.AppendLine($"  {(t.IsActive ? "active  " : "inactive")} {t.FriendlyName,-20} {t.MonitorId}");
        text.AppendLine();

        text.AppendLine("EDID (as cached by Windows; a KVM with EDID emulation shows its own values here)");
        foreach (var t in connected)
        {
            var info = Edid.ForMonitor(t.MonitorId);
            text.AppendLine($"  {t.FriendlyName,-20} {(info == null ? "not available" : Edid.Describe(info))}");
        }
        text.AppendLine();

        text.AppendLine("VIRTUAL DISPLAY DRIVER");
        text.AppendLine($"  Parsec VDD installed: {ParsecVdd.IsDriverPresent()}");
        text.AppendLine();

        text.AppendLine("INPUT");
        text.AppendLine($"  idle for {Jiggler.IdleTime().TotalSeconds:F0} s");
        text.AppendLine();

        text.AppendLine(WindowSnapshot.Describe("now"));
        return text.ToString();
    }

    /// <summary>Writes the report to dump.txt and returns it.</summary>
    public static string WriteDump(DisplayProfile? saved)
    {
        string report = Build(saved);
        Directory.CreateDirectory(DisplayProfile.ConfigDir);
        File.WriteAllText(DumpPath, report);
        return report;
    }

    private static string Indent(string block) =>
        string.Join(Environment.NewLine, block.Split('\n').Select(l => "  " + l.TrimEnd('\r')));
}
