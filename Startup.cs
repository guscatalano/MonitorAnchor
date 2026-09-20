using Microsoft.Win32;

namespace MonitorAnchor;

/// <summary>Registers/unregisters the app in HKCU\...\Run so it comes back after a reboot or sign-in.</summary>
public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MonitorAnchor";

    private static string CommandLine => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>True when the Run entry exists but points at a different exe (e.g. the app was moved).</summary>
    public static bool IsStale()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string s && !string.Equals(s, CommandLine, StringComparison.OrdinalIgnoreCase);
    }

    public static void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.SetValue(ValueName, CommandLine, RegistryValueKind.String);
        Log.Write($"Startup enabled: {CommandLine}");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
        Log.Write("Startup disabled");
    }
}
