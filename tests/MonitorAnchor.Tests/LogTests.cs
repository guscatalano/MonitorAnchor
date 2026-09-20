using MonitorAnchor;
using Xunit;

namespace MonitorAnchor.Tests;

public class LogTests
{
    [Fact]
    public void Trim_drops_entries_older_than_two_days_and_keeps_continuation_lines_with_their_entry()
    {
        using var tmp = new TempDataFolder();
        string Stamp(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss");
        var old = DateTime.Now.AddDays(-3);
        var recent = DateTime.Now.AddHours(-1);
        File.WriteAllLines(Log.Path, new[]
        {
            $"{Stamp(old)} old entry",
            "    continuation of the old entry",
            $"{Stamp(recent)} recent entry",
            "    continuation of the recent entry",
        });

        Log.Trim();

        var lines = File.ReadAllLines(Log.Path);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("recent entry", lines[0]);
        Assert.Equal("    continuation of the recent entry", lines[1]);
    }

    [Fact]
    public void Write_appends_a_timestamped_line()
    {
        using var tmp = new TempDataFolder();
        Log.Write("hello");
        var line = File.ReadAllLines(Log.Path).Single();
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} hello$", line);
    }
}
