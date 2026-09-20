using System.Text;

namespace MonitorAnchor;

/// <summary>Builds the diagnostic report shown by "Show diagnostics..." and written by --dump.</summary>
public static class Diagnostics
{
    public static string DumpPath => Path.Combine(DisplayProfile.ConfigDir, "dump.txt");

    /// <summary>Extra lines the running tray app contributes (window memory status etc.); null from the command line.</summary>
    public static Func<string>? LiveStatus;

    public static string Build(LayoutStore store, Func<string>? kvmVerdict = null)
    {
        var saved = store.SelectForCurrentMonitors();
        var text = new StringBuilder();
        text.AppendLine($"Monitor Anchor {Updater.Current} ({Updater.AssetName})  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"Data folder: {DisplayProfile.ConfigDir}");
        text.AppendLine($"Running from: {Environment.ProcessPath}{(Packaged.IsPackaged ? "  (MSIX package)" : Installer.IsInstalled ? "  (installed)" : "")}");
        text.AppendLine();

        text.AppendLine($"SAVED LAYOUTS ({store.Layouts.Count})");
        if (store.Layouts.Count == 0) text.AppendLine("  (none)");
        foreach (var l in store.Layouts)
        {
            text.AppendLine($"  {l.Name}{(ReferenceEquals(l, saved) ? "   <- active for the connected monitors" : "")}   (captured {l.CapturedAt:g})");
            text.AppendLine(Indent(Indent(l.ToString())));
        }
        if (saved == null) text.AppendLine("  no saved layout matches the monitors connected right now");
        text.AppendLine();

        var live = DisplayManager.CaptureAll();
        text.AppendLine("LIVE LAYOUT");
        text.AppendLine(Indent(live.ToString()));
        text.AppendLine();

        text.AppendLine("MONITOR IDS");
        foreach (var m in live.Monitors) text.AppendLine($"  {m.MonitorName,-20} {m.MonitorId}");
        text.AppendLine();

        text.AppendLine("SCALING (per adapter output)");
        var scaling = DpiScaling.QueryAll();
        if (scaling.Count == 0) text.AppendLine("  not readable");
        foreach (var (name, d) in scaling)
            text.AppendLine(d.Percent > 0 ? $"  {name}: {d.Percent}% (recommended {d.Recommended}%, up to {d.Max}%) [{d.Raw}]" : $"  {name}: not readable [{d.Raw}]");
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
        var edids = new List<Edid.Info>();
        foreach (var t in connected)
        {
            var info = Edid.ForMonitor(t.MonitorId);
            if (info != null) edids.Add(info);
            text.AppendLine($"  {t.FriendlyName,-20} {(info == null ? "not available" : Edid.Describe(info))}");
        }
        bool duplicateEdid = edids.GroupBy(e => (e.Manufacturer, e.ProductCode, e.Serial, e.SerialText)).Any(g => g.Count() > 1);
        text.AppendLine(duplicateEdid
            ? "  note: two monitors report identical EDIDs, which suggests a KVM or splitter emulating EDID"
            : "  monitors report distinct EDIDs: pass-through (no EDID emulation detected)");
        text.AppendLine();

        text.AppendLine("KVM / DOCK");
        string verdict;
        try { verdict = kvmVerdict?.Invoke() ?? new KvmDetector(AppState.Load()).Verdict(); }
        catch (Exception ex) { verdict = "unavailable: " + ex.Message; }
        text.AppendLine("  " + verdict);
        text.AppendLine("  (inferred: a KVM takes monitors and USB input devices away together; a dock also takes hubs, network and storage)");
        text.AppendLine();

        text.AppendLine("VIRTUAL DISPLAY DRIVER");
        text.AppendLine($"  Parsec VDD installed: {ParsecVdd.IsDriverPresent()}");
        text.AppendLine();

        text.AppendLine("INPUT");
        text.AppendLine("  " + InputTracker.Status);
        text.AppendLine();

        if (LiveStatus != null)
        {
            text.AppendLine("WINDOW MEMORY / PRESENCE");
            try { text.AppendLine("  " + LiveStatus()); } catch (Exception ex) { text.AppendLine("  unavailable: " + ex.Message); }
            text.AppendLine();
        }

        text.AppendLine("REMOTE DESKTOP WINDOWS");
        var rdp = RemoteDesktop.FindSessions();
        if (rdp.Count == 0) text.AppendLine("  none");
        foreach (var s in rdp) text.AppendLine($"  {(s.FullScreen ? "full-screen" : "windowed  ")} {s.Screen,-14} {s.Process}: {s.Title}");
        text.AppendLine();

        text.AppendLine(WindowSnapshot.Describe("now"));
        return text.ToString();
    }

    /// <summary>Writes the report to dump.txt and returns it.</summary>
    public static string WriteDump(LayoutStore store, Func<string>? kvmVerdict = null)
    {
        string report = Build(store, kvmVerdict);
        Directory.CreateDirectory(DisplayProfile.ConfigDir);
        File.WriteAllText(DumpPath, report);
        return report;
    }

    private static string Indent(string block) =>
        string.Join(Environment.NewLine, block.Split('\n').Select(l => "  " + l.TrimEnd('\r')));
}
