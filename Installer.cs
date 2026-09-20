using System.Diagnostics;

namespace MonitorAnchor;

/// <summary>
/// Puts the exe in a permanent place (%LOCALAPPDATA%\Programs\MonitorAnchor), adds a Start Menu shortcut and
/// registers startup, so a build downloaded to a temporary folder does not keep running from there.
/// No administrator rights needed; everything stays in the user's profile.
/// </summary>
public static class Installer
{
    public static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MonitorAnchor");
    public static string InstalledExe => Path.Combine(InstallDir, "MonitorAnchor.exe");
    public static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Monitor Anchor.lnk");

    /// <summary>True when the running exe is the installed copy.</summary>
    public static bool IsInstalled =>
        string.Equals(Environment.ProcessPath, InstalledExe, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when running from a build tree or a temporary/download location rather than somewhere chosen on purpose.</summary>
    public static bool LooksTemporary
    {
        get
        {
            string p = Environment.ProcessPath ?? "";
            if (IsInstalled || p.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) || p.Contains(@"\publish\", StringComparison.OrdinalIgnoreCase)) return false;
            string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            return p.StartsWith(downloads, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Copies this exe into the install folder, creates the shortcut, registers startup and starts the installed copy.</summary>
    public static string Install()
    {
        string source = Environment.ProcessPath ?? throw new InvalidOperationException("cannot determine own path");
        Directory.CreateDirectory(InstallDir);
        if (!IsInstalled)
        {
            // Retire a previously installed copy that may still be running.
            string parked = InstalledExe + ".old";
            if (File.Exists(InstalledExe))
            {
                if (File.Exists(parked)) File.Delete(parked);
                File.Move(InstalledExe, parked);
            }
            File.Copy(source, InstalledExe, overwrite: true);
        }
        CreateShortcut();
        Startup.EnableFor(InstalledExe);
        Log.Write($"Installed to {InstalledExe}; shortcut at {ShortcutPath}");
        return InstalledExe;
    }

    /// <summary>Removes the shortcut and startup entry; optionally the installed exe and the data folder.</summary>
    public static void Uninstall(bool removeData)
    {
        try { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); } catch { /* ignore */ }
        Startup.Disable();
        Log.Write("Uninstalled: shortcut and startup entry removed" + (removeData ? "; data folder scheduled for removal" : ""));
        // The exe and data folder cannot be deleted while this process runs; a detached shell command does it after exit.
        string script = $"/c timeout /t 3 /nobreak >nul & rmdir /s /q \"{InstallDir}\"" + (removeData ? $" & rmdir /s /q \"{DisplayProfile.ConfigDir}\"" : "");
        Process.Start(new ProcessStartInfo("cmd.exe", script) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
    }

    private static void CreateShortcut()
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell unavailable");
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(ShortcutPath);
            link.TargetPath = InstalledExe;
            link.WorkingDirectory = InstallDir;
            link.Description = "Monitor Anchor: keeps your monitor layout the way you set it";
            link.IconLocation = InstalledExe + ",0";
            link.Save();
        }
        catch (Exception ex)
        {
            Log.Write("Shortcut creation failed: " + ex.Message);
        }
    }
}
