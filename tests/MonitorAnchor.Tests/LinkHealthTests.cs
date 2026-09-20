using MonitorAnchor;
using Xunit;

namespace MonitorAnchor.Tests;

public class LinkHealthTests
{
    private static LinkInfo.Link Hdmi(string name, string colour, int bpc, double hz, double clock) =>
        new(name, "HDMI", colour, bpc, hz, clock, 1920, 1080, bpc > 8);

    [Theory]
    [InlineData(248.8, "RGB", 8, 248.8)]
    [InlineData(296.7, "YCbCr 4:4:4", 10, 370.875)]
    [InlineData(296.7, "YCbCr 4:2:2", 12, 296.7)]
    [InlineData(594.0, "YCbCr 4:2:0", 8, 297.0)]
    public void Effective_rate_scales_with_colour_format_and_depth(double clock, string colour, int bpc, double expected)
    {
        Assert.Equal(expected, LinkInfo.EffectiveTmdsMHz(clock, colour, bpc), 3);
    }

    [Fact]
    public void Laptop_style_link_raises_no_warning()
    {
        var w = LinkInfo.Warnings(new[] { Hdmi(@"\\.\DISPLAY10", "RGB", 8, 120.000, 248.8) },
                                  new[] { new LinkInfo.Adapter("AMD Radeon(TM) 780M", "32.0", new DateTime(2026, 8, 17)) }, new DateTime(2026, 9, 20));
        Assert.Empty(w);
    }

    [Fact]
    public void Ten_bit_hdr_at_120hz_over_hdmi_is_flagged_as_high_speed()
    {
        var w = LinkInfo.Warnings(new[] { Hdmi(@"\\.\DISPLAY2", "YCbCr 4:4:4", 10, 119.880, 296.7) }, Array.Empty<LinkInfo.Adapter>(), new DateTime(2026, 9, 20));
        Assert.Contains(w, s => s.Contains("371 MHz") && s.Contains("high-speed") && s.Contains("HDR off"));
        Assert.Contains(w, s => s.Contains("fractional refresh timing (119.880 Hz)"));
    }

    [Fact]
    public void Eight_bit_at_the_same_clock_is_not_flagged_for_speed()
    {
        var w = LinkInfo.Warnings(new[] { Hdmi(@"\\.\DISPLAY1", "YCbCr 4:4:4", 8, 120.000, 296.7) }, Array.Empty<LinkInfo.Adapter>(), new DateTime(2026, 9, 20));
        Assert.DoesNotContain(w, s => s.Contains("high-speed"));
    }

    [Fact]
    public void Beyond_hdmi_20_is_its_own_warning()
    {
        var w = LinkInfo.Warnings(new[] { Hdmi(@"\\.\DISPLAY1", "RGB", 10, 144, 533.0) }, Array.Empty<LinkInfo.Adapter>(), new DateTime(2026, 9, 20));
        Assert.Contains(w, s => s.Contains("exceeds HDMI 2.0"));
    }

    [Fact]
    public void Old_gpu_driver_is_flagged_but_virtual_adapters_are_not()
    {
        var w = LinkInfo.Warnings(Array.Empty<LinkInfo.Link>(), new[]
        {
            new LinkInfo.Adapter("AMD Radeon(TM) Graphics", "31.0.12027.9001", new DateTime(2023, 3, 30)),
            new LinkInfo.Adapter("Parsec Virtual Display Adapter", "0.41", new DateTime(2022, 9, 14)),
        }, new DateTime(2026, 9, 20));
        Assert.Single(w);
        Assert.Contains("AMD Radeon(TM) Graphics", w[0]);
        Assert.Contains("months old", w[0]);
    }

    [Fact]
    public void DisplayPort_links_are_not_judged_by_hdmi_rules()
    {
        var w = LinkInfo.Warnings(new[] { new LinkInfo.Link(@"\\.\DISPLAY1", "DisplayPort", "RGB", 10, 144, 800, 2560, 1440, true) },
                                  Array.Empty<LinkInfo.Adapter>(), new DateTime(2026, 9, 20));
        Assert.DoesNotContain(w, s => s.Contains("HDMI"));
    }
}
