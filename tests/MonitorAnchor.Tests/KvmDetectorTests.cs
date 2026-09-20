using MonitorAnchor;
using Xunit;

namespace MonitorAnchor.Tests;

public class KvmDetectorTests
{
    private const string Kbd0 = @"\\?\HID#VID_1038&PID_231A&MI_00#c&1b3c1196&0&0000#{guid}";
    private const string Kbd1 = @"\\?\HID#VID_1038&PID_231A&MI_01#c&3788551&0&0000#{guid}";
    private const string Hub1 = @"\\?\USB#VID_05E3&PID_0610#9&26204ef6&0&4#{guid}";
    private const string Hub2 = @"\\?\USB#VID_05E3&PID_0610#a&12199534&0&4#{guid}";

    private static KvmDetector NewDetector(out AppState state)
    {
        state = new AppState();
        return new KvmDetector(state);
    }

    [Fact]
    public void Monitors_and_input_devices_dropping_together_is_a_kvm_switch()
    {
        using var tmp = new TempDataFolder();
        var d = NewDetector(out var state);
        d.FeedMonitors(removed: 2, added: 0);
        d.FeedDevice(false, Kbd0);
        d.FeedDevice(false, Kbd1);
        d.FeedDevice(false, Hub1);
        d.Evaluate();

        Assert.Equal("KVM switch away", d.LastKind);
        Assert.Equal(1, state.KvmSwitchesAway);
        Assert.Equal(0, state.KvmUsbOnly);
    }

    [Fact]
    public void Input_devices_and_a_hub_without_monitors_is_a_usb_only_switch()
    {
        using var tmp = new TempDataFolder();
        var d = NewDetector(out var state);
        d.FeedDevice(false, Kbd0);
        d.FeedDevice(false, Kbd1);
        d.FeedDevice(false, Hub1);
        d.FeedDevice(false, Hub2);
        d.Evaluate();
        Assert.Equal("KVM switch away (USB only)", d.LastKind);

        d.FeedDevice(true, Hub1);
        d.FeedDevice(true, Kbd0);
        d.FeedDevice(true, Kbd1);
        d.FeedDevice(true, Hub2);
        d.Evaluate();
        Assert.Equal("KVM switch back (USB only)", d.LastKind);
        Assert.Equal(2, state.KvmUsbOnly);
        Assert.Equal(1, state.KvmSwitchesBack);
    }

    [Fact]
    public void A_flapping_hub_counts_once()
    {
        using var tmp = new TempDataFolder();
        var d = NewDetector(out var state);
        d.FeedDevice(true, Kbd0);
        d.FeedDevice(true, Hub1);
        d.FeedDevice(false, Hub1); // flap
        d.FeedDevice(true, Hub1);
        d.FeedDevice(true, Hub1);
        d.FeedDevice(true, Hub2);
        d.Evaluate();
        // Distinct "other" arrivals = 2 hubs, well under the dock threshold, so this is a KVM, not a dock.
        Assert.Equal("KVM switch back (USB only)", d.LastKind);
        Assert.Equal(0, state.DockEvents);
    }

    [Fact]
    public void Many_other_devices_make_it_a_dock_event()
    {
        using var tmp = new TempDataFolder();
        var d = NewDetector(out var state);
        d.FeedMonitors(removed: 1, added: 0);
        d.FeedDevice(false, Kbd0);
        for (int i = 0; i < 5; i++) d.FeedDevice(false, $@"\\?\USB#VID_0000&PID_{i:X4}#1&2&3#{{guid}}");
        d.Evaluate();
        Assert.Equal("Undocked", d.LastKind);
        Assert.Equal(1, state.DockEvents);
        Assert.Equal(0, state.KvmSwitchesAway);
    }

    [Fact]
    public void A_monitor_alone_is_a_plain_unplug()
    {
        using var tmp = new TempDataFolder();
        var d = NewDetector(out var state);
        d.FeedMonitors(removed: 1, added: 0);
        d.Evaluate();
        Assert.Null(d.LastKind);
        Assert.Equal(0, state.KvmSwitchesAway + state.KvmSwitchesBack + state.DockEvents);
    }

    [Fact]
    public void Verdict_reads_naturally()
    {
        using var tmp = new TempDataFolder();
        var d = NewDetector(out var state);
        Assert.StartsWith("no KVM pattern seen yet", d.Verdict());
        state.KvmSwitchesAway = 2; state.KvmSwitchesBack = 2; state.KvmUsbOnly = 4; state.LastKvmSwitch = DateTime.Now;
        Assert.Contains("KVM suspected: 2 switch(es) away, 2 back", d.Verdict());
        Assert.Contains("USB only", d.Verdict());
    }
}
