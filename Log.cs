using System.Globalization;

namespace MonitorAnchor;

/// <summary>
/// Append-only log in %LOCALAPPDATA%\MonitorAnchor\log.txt. Entries older than <see cref="MaxAge"/> are dropped
/// (checked at startup and hourly), and the file is halved if it ever passes <see cref="MaxBytes"/>.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 2 * 1024 * 1024;
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(2);
    private static readonly TimeSpan TrimInterval = TimeSpan.FromHours(1);
    private static DateTime _lastTrim = DateTime.MinValue;

    private const string Stamp = "yyyy-MM-dd HH:mm:ss";

    public static string Path => System.IO.Path.Combine(DisplayProfile.ConfigDir, "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DisplayProfile.ConfigDir);
                if (DateTime.Now - _lastTrim > TrimInterval) TrimLocked();
                File.AppendAllText(Path, $"{DateTime.Now.ToString(Stamp, CultureInfo.InvariantCulture)} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    /// <summary>Drops entries older than MaxAge and enforces the size cap. Safe to call any time.</summary>
    public static void Trim()
    {
        try { lock (Gate) TrimLocked(); } catch { /* best effort */ }
    }

    private static void TrimLocked()
    {
        _lastTrim = DateTime.Now;
        var fi = new FileInfo(Path);
        if (!fi.Exists || fi.Length == 0) return;

        var lines = File.ReadAllLines(Path);
        var cutoff = DateTime.Now - MaxAge;

        // Find the first entry that is new enough. Lines without a timestamp (indented continuation lines,
        // bullet lists) belong to the entry above them and travel with it.
        int keepFrom = lines.Length;
        for (int i = 0; i < lines.Length; i++)
        {
            var stamp = ParseStamp(lines[i]);
            if (stamp != null && stamp >= cutoff) { keepFrom = i; break; }
        }

        if (keepFrom > 0 || fi.Length > MaxBytes)
        {
            var kept = lines.Skip(keepFrom).ToList();
            // Still too big (a very chatty two days): keep the newer half.
            while (kept.Sum(l => l.Length + 2) > MaxBytes && kept.Count > 1)
            {
                int half = kept.Count / 2;
                while (half < kept.Count && ParseStamp(kept[half]) == null) half++; // do not split an entry
                kept = kept.Skip(half).ToList();
            }
            File.WriteAllLines(Path, kept);
        }
    }

    private static DateTime? ParseStamp(string line) =>
        line.Length >= Stamp.Length &&
        DateTime.TryParseExact(line.AsSpan(0, Stamp.Length), Stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt)
            ? dt : null;
}
