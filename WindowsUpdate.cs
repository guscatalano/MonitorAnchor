using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;

namespace MonitorAnchor;

/// <summary>
/// Pauses or resumes Windows Update the same way the Settings page does, by writing the pause window into
/// HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings. Writing needs administrator rights, so the app
/// relaunches itself elevated with --pause-updates for just that step. Reading the status does not.
/// </summary>
public static class WindowsUpdate
{
    private const string Key = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    private const string Iso = "yyyy-MM-ddTHH:mm:ssZ";

    /// <summary>When the current pause ends, or null when updates are not paused.</summary>
    public static DateTime? PausedUntil()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(Key);
            if (k?.GetValue("PauseUpdatesExpiryTime") is string s &&
                DateTime.TryParseExact(s, Iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var until) &&
                until > DateTime.UtcNow)
                return until.ToLocalTime();
        }
        catch { /* treat as not paused */ }
        return null;
    }

    /// <summary>Launches an elevated copy of this exe to pause for <paramref name="days"/> days (0 = resume). Returns its exit code, or -1 if the UAC prompt was declined.</summary>
    public static int RequestPause(int days)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--pause-updates {days}") { UseShellExecute = true, Verb = "runas" });
            if (p == null) return -1;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return -1; // user declined the elevation prompt
        }
    }

    /// <summary>Runs inside the elevated instance. Returns 0 on success.</summary>
    public static int ApplyElevated(int days)
    {
        try
        {
            using var k = Registry.LocalMachine.CreateSubKey(Key, writable: true);
            string[] names =
            {
                "PauseUpdatesStartTime", "PauseUpdatesExpiryTime",
                "PauseFeatureUpdatesStartTime", "PauseFeatureUpdatesEndTime",
                "PauseQualityUpdatesStartTime", "PauseQualityUpdatesEndTime",
            };
            if (days <= 0)
            {
                foreach (var n in names) k.DeleteValue(n, throwOnMissingValue: false);
                k.DeleteValue("FlightSettingsMaxPauseDays", throwOnMissingValue: false);
                Log.Write("Windows Update: resumed (pause cleared)");
                return 0;
            }

            var start = DateTime.UtcNow;
            var end = start.AddDays(days);
            string s = start.ToString(Iso, CultureInfo.InvariantCulture), e = end.ToString(Iso, CultureInfo.InvariantCulture);
            k.SetValue("PauseUpdatesStartTime", s);
            k.SetValue("PauseUpdatesExpiryTime", e);
            k.SetValue("PauseFeatureUpdatesStartTime", s);
            k.SetValue("PauseFeatureUpdatesEndTime", e);
            k.SetValue("PauseQualityUpdatesStartTime", s);
            k.SetValue("PauseQualityUpdatesEndTime", e);
            // Settings caps the UI at 35 days; this value raises the cap so longer windows are honoured.
            if (days > 35) k.SetValue("FlightSettingsMaxPauseDays", days, RegistryValueKind.DWord);
            Log.Write($"Windows Update: paused for {days} day(s), until {end.ToLocalTime():g}");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write("Windows Update pause failed: " + ex.Message);
            return 2;
        }
    }
}
