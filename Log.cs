namespace MonitorAnchor;

/// <summary>Tiny append-only log in %LOCALAPPDATA%\MonitorAnchor\log.txt, trimmed when it grows past ~512 KB.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 512 * 1024;

    public static string Path => System.IO.Path.Combine(DisplayProfile.ConfigDir, "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DisplayProfile.ConfigDir);
                var fi = new FileInfo(Path);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    // Keep the tail so recent history survives the trim.
                    var lines = File.ReadAllLines(Path);
                    File.WriteAllLines(Path, lines.Skip(lines.Length / 2));
                }
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
