using MonitorAnchor;
using Xunit;

namespace MonitorAnchor.Tests;

public class DifferencesTests
{
    private static MonitorSettings Mon(string id, int hz = 120, bool hdr = false, int dpi = 100) => new()
    {
        MonitorId = $@"\\?\DISPLAY#{id}#{{g}}", MonitorName = "VG259QM", AdapterName = @"\\.\DISPLAY2",
        Width = 1920, Height = 1080, RefreshRate = hz, HdrSupported = true, HdrEnabled = hdr, DpiScale = dpi, IsActive = true,
    };

    [Fact]
    public void No_differences_when_live_matches_saved()
    {
        var saved = new DisplayProfile { Monitors = { Mon("A") } };
        var live = new DisplayProfile { Monitors = { Mon("A") } };
        Assert.Empty(saved.DifferencesFrom(live));
    }

    [Fact]
    public void Describes_each_changed_field()
    {
        var saved = new DisplayProfile { Monitors = { Mon("A", hz: 120, hdr: true, dpi: 100) } };
        var live = new DisplayProfile { Monitors = { Mon("A", hz: 60, hdr: false, dpi: 125) } };
        var d = saved.DifferencesFrom(live);
        Assert.Single(d);
        Assert.Contains("120 Hz -> 60 Hz", d[0]);
        Assert.Contains("HDR on -> off", d[0]);
        Assert.Contains("scale 100% -> 125%", d[0]);
    }

    [Fact]
    public void Unknown_monitors_are_ignored()
    {
        var saved = new DisplayProfile { Monitors = { Mon("A") } };
        var live = new DisplayProfile { Monitors = { Mon("Z", hz: 60) } };
        Assert.Empty(saved.DifferencesFrom(live));
    }
}
