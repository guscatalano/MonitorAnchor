using Microsoft.Win32;

namespace MonitorAnchor;

/// <summary>Registers/unregisters the app in HKCU\...\Run so it comes back after a reboot or sign-in.</summary>
public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MonitorAnchor";

    private static string CommandLine => $"\"{Environment.ProcessPath}\"";

    /// <summary>Inside an MSIX package the Run key is virtualised; startup is the manifest's StartupTask, managed by Windows.</summary>
    public static bool ManagedByWindows => Packaged.IsPackaged;

    public static bool IsEnabled()
    {
        if (ManagedByWindows) return true; // the manifest enables the StartupTask; the user can turn it off in Settings
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>Opens Settings &gt; Apps &gt; Startup, where a packaged app's startup task is controlled.</summary>
    public static void OpenWindowsStartupSettings()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("Could not open startup settings: " + ex.Message); }
    }

    /// <summary>True when the Run entry exists but points at a different exe (e.g. the app was moved).</summary>
    public static bool IsStale()
    {
        if (ManagedByWindows) return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string s && !string.Equals(s, CommandLine, StringComparison.OrdinalIgnoreCase);
    }

    public static void Enable() => EnableFor(Environment.ProcessPath ?? "");

    /// <summary>Registers a specific exe path (used when installing, before the installed copy runs).</summary>
    public static void EnableFor(string exePath)
    {
        if (ManagedByWindows) return;
        string command = $"\"{exePath}\"";
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.SetValue(ValueName, command, RegistryValueKind.String);
        Log.Write($"Startup enabled: {command}");
    }

    public static void Disable()
    {
        if (ManagedByWindows) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
        Log.Write("Startup disabled");
    }
}
