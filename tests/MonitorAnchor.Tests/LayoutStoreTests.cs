using MonitorAnchor;
using Xunit;

namespace MonitorAnchor.Tests;

public class LayoutStoreTests
{
    private static MonitorSettings Mon(string id, string name, bool primary = false) => new()
    {
        MonitorId = $@"\\?\DISPLAY#{id}#{{guid}}", MonitorName = name, Width = 1920, Height = 1080, RefreshRate = 60, IsPrimary = primary,
    };

    private static DisplayProfile Layout(params MonitorSettings[] monitors) => new()
    {
        CapturedAt = DateTime.Now, Monitors = monitors.ToList(),
    };

    [Fact]
    public void Selects_the_layout_whose_monitors_are_all_connected()
    {
        using var tmp = new TempDataFolder();
        var store = new LayoutStore();
        var desk = store.Upsert(Layout(Mon("A", "VG259QM"), Mon("B", "VG259QM", true)));
        var laptop = store.Upsert(Layout(Mon("L", "NE13NY1", true)));

        Assert.Same(desk, store.Select(new[] { desk.Monitors[0].MonitorId, desk.Monitors[1].MonitorId, laptop.Monitors[0].MonitorId }));
        Assert.Same(laptop, store.Select(new[] { laptop.Monitors[0].MonitorId }));
        Assert.Null(store.Select(new[] { desk.Monitors[0].MonitorId })); // only one of the desk monitors present
        Assert.Null(store.Select(Array.Empty<string>()));
    }

    [Fact]
    public void Prefers_the_layout_covering_more_monitors()
    {
        using var tmp = new TempDataFolder();
        var store = new LayoutStore();
        var single = store.Upsert(Layout(Mon("A", "X", true)));
        var pair = store.Upsert(Layout(Mon("A", "X", true), Mon("B", "Y")));
        Assert.Same(pair, store.Select(new[] { single.Monitors[0].MonitorId, pair.Monitors[1].MonitorId }));
        Assert.Same(single, store.Select(new[] { single.Monitors[0].MonitorId }));
    }

    [Fact]
    public void Upsert_replaces_the_layout_for_the_same_monitors_and_keeps_a_chosen_name()
    {
        using var tmp = new TempDataFolder();
        var store = new LayoutStore();
        var first = store.Upsert(Layout(Mon("A", "X", true), Mon("B", "X")));
        Assert.Equal("X ×2", first.Name);

        store.Rename(first, "Desk");
        var second = Layout(Mon("B", "X"), Mon("A", "X", true)); // same monitors, different order
        second.Monitors[0].RefreshRate = 144;
        var stored = store.Upsert(second);

        Assert.Single(store.Layouts);
        Assert.Equal("Desk", stored.Name);
        Assert.Equal(144, store.Layouts[0].Monitors.First(m => m.MonitorId.Contains("#B#")).RefreshRate);
    }

    [Fact]
    public void Generated_names_are_refreshed_and_kept_unique()
    {
        using var tmp = new TempDataFolder();
        var store = new LayoutStore();
        var l1 = store.Upsert(Layout(Mon("A", "Generic PnP Monitor", true)));
        Assert.Equal("Generic PnP Monitor", l1.Name);
        var again = store.Upsert(Layout(Mon("A", "VG259QM", true)));
        Assert.Equal("VG259QM", again.Name); // auto name refreshed from better monitor names

        var other = store.Upsert(Layout(Mon("Z", "VG259QM", true)));
        Assert.Equal("VG259QM (2)", other.Name);
    }

    [Fact]
    public void Round_trips_through_json()
    {
        using var tmp = new TempDataFolder();
        var store = new LayoutStore();
        store.Upsert(Layout(Mon("A", "X", true)));
        store.Layouts[0].Monitors[0].DpiScale = 125;
        store.Layouts[0].Monitors[0].HdrSupported = true;
        store.Save();

        var loaded = LayoutStore.Load();
        Assert.Single(loaded.Layouts);
        Assert.Equal(125, loaded.Layouts[0].Monitors[0].DpiScale);
        Assert.True(loaded.Layouts[0].Monitors[0].HdrSupported);
        Assert.Equal(LayoutStore.Signature(store.Layouts[0]), LayoutStore.Signature(loaded.Layouts[0]));
    }

    [Fact]
    public void Imports_a_legacy_single_profile()
    {
        using var tmp = new TempDataFolder();
        var legacy = Layout(Mon("A", "X", true));
        legacy.Save(); // writes profile.json

        var store = LayoutStore.Load();
        Assert.Single(store.Layouts);
        Assert.Equal("X", store.Layouts[0].Name);
        Assert.True(File.Exists(LayoutStore.Path));
        Assert.False(File.Exists(DisplayProfile.ProfilePath));
    }
}
