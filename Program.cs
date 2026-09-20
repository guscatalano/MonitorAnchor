using System.Diagnostics;

namespace MonitorAnchor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Started by the updater: let the previous instance finish exiting so the single-instance mutex is free.
        int waitIdx = Array.FindIndex(args, a => string.Equals(a, "--wait-for", StringComparison.OrdinalIgnoreCase));
        if (waitIdx >= 0 && waitIdx + 1 < args.Length && int.TryParse(args[waitIdx + 1], out int pid))
        {
            try { Process.GetProcessById(pid).WaitForExit(15_000); } catch { /* already gone */ }
        }
        Updater.CleanupOld();

        // Diagnostic mode: write the current layout to a file and exit. Handy for bug reports.
        if (args.Any(a => string.Equals(a, "--dump", StringComparison.OrdinalIgnoreCase)))
        {
            var snapshot = DisplayManager.CaptureAll();
            Directory.CreateDirectory(DisplayProfile.ConfigDir);
            string dumpPath = Path.Combine(DisplayProfile.ConfigDir, "dump.txt");
            var text = new System.Text.StringBuilder();
            text.AppendLine(snapshot.ToString()).AppendLine();
            foreach (var m in snapshot.Monitors) text.AppendLine(m.MonitorId);
            text.AppendLine().AppendLine("HDR (per adapter output):");
            foreach (var (name, h) in HdrManager.QueryAll()) text.AppendLine($"  {name}: supported={h.Supported} enabled={h.Enabled} ({h.Raw})");
            text.AppendLine().AppendLine("Connected targets (DisplayConfig):");
            foreach (var t in Topology.ConnectedMonitors()) text.AppendLine($"  {(t.IsActive ? "active  " : "inactive")} {t.FriendlyName} {t.MonitorId}");
            text.AppendLine().AppendLine("EDID (as cached by Windows; a KVM with EDID emulation shows its own values here):");
            foreach (var t in Topology.ConnectedMonitors())
            {
                var info = Edid.ForMonitor(t.MonitorId);
                text.AppendLine($"  {t.FriendlyName}: {(info == null ? "not available" : Edid.Describe(info))}");
            }
            text.AppendLine().AppendLine($"Version {Updater.Current} ({Updater.AssetName})");
            File.WriteAllText(dumpPath, text.ToString());
            return;
        }

        // Diagnostic: check (without applying) whether disabled-but-connected monitors could be re-enabled in a targeted way.
        if (args.Any(a => string.Equals(a, "--test-enable", StringComparison.OrdinalIgnoreCase)))
        {
            var inactive = Topology.ConnectedMonitors().Where(t => !t.IsActive).Select(t => t.MonitorId).ToList();
            string result = inactive.Count == 0 ? "no disabled monitors connected" : Topology.Enable(inactive, validateOnly: true);
            Log.Write($"--test-enable: {result}");
            return;
        }

        // Diagnostic: log where every window is right now.
        if (args.Any(a => string.Equals(a, "--windows", StringComparison.OrdinalIgnoreCase)))
        {
            WindowSnapshot.LogNow("command line");
            return;
        }

        // Scriptable equivalents of the tray menu items.
        if (args.Any(a => string.Equals(a, "--persist", StringComparison.OrdinalIgnoreCase)))
        {
            var captured = DisplayManager.CaptureForProfile();
            captured.Save();
            Log.Write("Persisted layout (command line):" + Environment.NewLine + captured);
            return;
        }
        if (args.Any(a => string.Equals(a, "--apply", StringComparison.OrdinalIgnoreCase)))
        {
            var saved = DisplayProfile.Load();
            if (saved == null) { Log.Write("--apply: no saved profile"); return; }
            var result = DisplayManager.Apply(saved);
            Log.Write("Apply (command line): " + result.Summary);
            return;
        }

        using var mutex = new Mutex(initiallyOwned: true, @"Local\MonitorAnchor.SingleInstance", out bool createdNew);
        if (!createdNew) return; // already running in this session

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write("Unhandled UI exception: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Unhandled exception: " + e.ExceptionObject);

        Application.Run(new TrayApp());
    }
}
