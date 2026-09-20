using MonitorAnchor;
using Xunit;

namespace MonitorAnchor.Tests;

public class LinkRetrainTests
{
    private const string GpuAudio = @"\\?\FUNC_01&VEN_1002&DEV_AA01&SUBSYS_00AA0100&REV_1007#5&79adabd&0&0001#{guid}";
    private const string Endpoint = @"\\?\MMDEVAPI#{0.0.0.00000000}.{caac5442-985e-4323-99ab-98a3826d5838}#{guid}";

    [Fact]
    public void Repeated_display_changes_with_unchanged_monitors_are_a_link_retrain()
    {
        using var tmp = new TempDataFolder();
        var state = new AppState();
        var d = new KvmDetector(state);
        d.FeedRetrain(5);
        for (int i = 0; i < 6; i++) { d.FeedDevice(false, GpuAudio); d.FeedDevice(true, GpuAudio); }
        d.FeedDevice(false, Endpoint);
        d.Evaluate();

        Assert.Equal("Display link retrained", d.LastKind);
        Assert.Equal(5, state.LinkRetrains);
        Assert.NotNull(state.LastLinkRetrain);
    }

    [Fact]
    public void A_single_mode_change_is_not_reported()
    {
        using var tmp = new TempDataFolder();
        var state = new AppState();
        var d = new KvmDetector(state);
        d.FeedRetrain(1);
        d.Evaluate();
        Assert.Null(d.LastKind);
        Assert.Equal(0, state.LinkRetrains);
    }

    [Fact]
    public void Audio_churn_does_not_count_toward_the_dock_threshold()
    {
        using var tmp = new TempDataFolder();
        var state = new AppState();
        var d = new KvmDetector(state);
        d.FeedMonitors(removed: 1, added: 0);
        d.FeedDevice(false, @"\\?\HID#VID_1038&PID_231A&MI_00#c&1#{guid}");
        for (int i = 0; i < 10; i++) d.FeedDevice(false, GpuAudio);
        d.Evaluate();
        Assert.Equal("KVM switch away", d.LastKind); // not "Undocked"
    }

    [Theory]
    [InlineData(@"\\?\HID#VID_1038&PID_231A&MI_00#c&1#{g}", "hid")]
    [InlineData(@"\\?\MMDEVAPI#{0.0.0.00000000}.{caac5442}#{g}", "audio")]
    [InlineData(@"\\?\FUNC_01&VEN_1002&DEV_AA01#5&1#{g}", "audio")]
    [InlineData(@"\\?\HDAUDIO#FUNC_01&VEN_10EC&DEV_0256#4&1#{g}", "audio")]
    [InlineData(@"\\?\USB#VID_05E3&PID_0610#9&1#{g}", "other")]
    public void Classifies_device_paths(string path, string expected)
    {
        Assert.Equal(expected, KvmDetector.Classify(path));
    }
}
