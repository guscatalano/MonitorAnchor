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
            try
            {
                var previous = Process.GetProcessById(pid);
                if (!previous.WaitForExit(15_000) && previous.ProcessName.Equals("MonitorAnchor", StringComparison.OrdinalIgnoreCase))
                {
                    // Our predecessor is stuck (seen when the install offer ran before the message loop); take over.
                    Log.Write($"Previous instance {pid} did not exit within 15 s; ending it");
                    previous.Kill();
                    previous.WaitForExit(5_000);
                }
            }
            catch { /* already gone */ }
        }
        Updater.CleanupOld();
        Log.Trim();

        // Diagnostic mode: write the current layout to a file and exit. Handy for bug reports.
        if (args.Any(a => string.Equals(a, "--dump", StringComparison.OrdinalIgnoreCase)))
        {
            Diagnostics.WriteDump(LayoutStore.Load());
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

        // Render README screenshots of the menu and diagnostics window: --screenshots <dir>
        int shotIdx = Array.FindIndex(args, a => string.Equals(a, "--screenshots", StringComparison.OrdinalIgnoreCase));
        if (shotIdx >= 0 && shotIdx + 1 < args.Length)
        {
            TrayApp.ScreenshotMode = true;
            ApplicationConfiguration.Initialize();
            var app = new TrayApp();
            app.SaveScreenshots(args[shotIdx + 1]);
            app.ExitThread();
            return;
        }

        // --install copies this exe to %LOCALAPPDATA%\Programs\MonitorAnchor, adds a Start Menu shortcut and startup entry, and starts it.
        if (args.Any(a => string.Equals(a, "--install", StringComparison.OrdinalIgnoreCase)))
        {
            string exe = Installer.Install();
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            return;
        }
        if (args.Any(a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            Installer.Uninstall(removeData: args.Any(a => string.Equals(a, "--purge", StringComparison.OrdinalIgnoreCase)));
            return;
        }

        // Elevated helper: --pause-updates <days> (0 = resume). Launched by the tray app with a UAC prompt.
        int pauseIdx = Array.FindIndex(args, a => string.Equals(a, "--pause-updates", StringComparison.OrdinalIgnoreCase));
        if (pauseIdx >= 0 && pauseIdx + 1 < args.Length && int.TryParse(args[pauseIdx + 1], out int pauseDays))
        {
            Environment.Exit(WindowsUpdate.ApplyElevated(pauseDays));
            return;
        }

        // Diagnostic: run one mouse tour now and log which windows were reached.
        if (args.Any(a => string.Equals(a, "--tour", StringComparison.OrdinalIgnoreCase)))
        {
            var r = MouseTour.Run();
            Log.Write($"Mouse tour (command line): hovered over {r.Visited} window(s), {r.Hidden} fully covered: {string.Join(", ", r.Names)}");
            return;
        }

        // Diagnostic: list child window classes of windows whose title contains the given text: --classes <text>
        int clsIdx = Array.FindIndex(args, a => string.Equals(a, "--classes", StringComparison.OrdinalIgnoreCase));
        if (clsIdx >= 0 && clsIdx + 1 < args.Length)
        {
            Log.Write("Window classes for \"" + args[clsIdx + 1] + "\":" + Environment.NewLine + RemoteDesktop.DescribeWindowClasses(args[clsIdx + 1]));
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
            var captured = LayoutStore.Load().Upsert(DisplayManager.CaptureForProfile());
            Log.Write($"Persisted layout \"{captured.Name}\" (command line):" + Environment.NewLine + captured);
            return;
        }
        if (args.Any(a => string.Equals(a, "--apply", StringComparison.OrdinalIgnoreCase)))
        {
            var saved = LayoutStore.Load().SelectForCurrentMonitors();
            if (saved == null) { Log.Write("--apply: no saved layout matches the connected monitors"); return; }
            var result = DisplayManager.Apply(saved);
            Log.Write("Apply (command line): " + result.Summary);
            return;
        }

        using var mutex = new Mutex(initiallyOwned: true, @"Local\MonitorAnchor.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Log.Write($"Not starting: another instance is already running (this one: {Environment.ProcessPath})");
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write("Unhandled UI exception: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Unhandled exception: " + e.ExceptionObject);

        Application.Run(new TrayApp());
    }
}
