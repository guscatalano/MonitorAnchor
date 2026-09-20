using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MonitorAnchor;

/// <summary>Machine facts for the log and diagnostics: what you would ask for first when comparing two machines.</summary>
public static class SystemInfo
{
    public static string MachineName => Environment.MachineName;

    public static string Cpu()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "unknown CPU";
        }
        catch { return "unknown CPU"; }
    }

    public static string Memory()
    {
        try
        {
            var info = GC.GetGCMemoryInfo();
            return $"{info.TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024):F0} GB RAM";
        }
        catch { return "RAM unknown"; }
    }

    public static string Uptime()
    {
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var booted = DateTime.Now - up;
        return $"up {Presence.Describe(up)} (booted {booted:g})";
    }

    /// <summary>Display-off and sleep timeouts of the active power plan, on AC and battery, from powercfg.</summary>
    public static string PowerTimeouts()
    {
        try
        {
            string display = Timeout("SUB_VIDEO", "VIDEOIDLE");
            string sleep = Timeout("SUB_SLEEP", "STANDBYIDLE");
            return $"power plan: display off after {display}; sleep after {sleep}";
        }
        catch (Exception ex) { return "power plan: unavailable (" + ex.Message + ")"; }
    }

    private static string Timeout(string sub, string setting)
    {
        var psi = new ProcessStartInfo("powercfg", $"/query SCHEME_CURRENT {sub} {setting}") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);
        string ac = Regex.Match(output, @"AC Power Setting Index:\s*0x([0-9a-fA-F]+)").Groups[1].Value;
        string dc = Regex.Match(output, @"DC Power Setting Index:\s*0x([0-9a-fA-F]+)").Groups[1].Value;
        string Fmt(string hex) => hex.Length == 0 ? "?" : Convert.ToInt64(hex, 16) is var s && s == 0 ? "never" : Presence.Describe(TimeSpan.FromSeconds(s));
        return $"{Fmt(ac)} on AC, {Fmt(dc)} on battery";
    }

    /// <summary>Everything, one line per fact, indented for the log and diagnostics.</summary>
    public static string Describe()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("  ").Append(MachineName).Append(", ").Append(Cpu()).Append(", ").AppendLine(Memory());
        sb.Append("  ").AppendLine(LinkInfo.OsBuild());
        sb.Append("  ").AppendLine(Uptime());
        sb.Append("  ").AppendLine(PowerTimeouts());
        foreach (var a in LinkInfo.Adapters()) sb.Append("  ").AppendLine(a.ToString());
        foreach (var l in LinkInfo.Query()) sb.Append("  ").AppendLine(l.ToString());
        return sb.ToString().TrimEnd();
    }
}
