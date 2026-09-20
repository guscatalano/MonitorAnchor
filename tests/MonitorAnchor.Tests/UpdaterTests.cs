using MonitorAnchor;
using Xunit;

namespace MonitorAnchor.Tests;

public class UpdaterTests
{
    [Fact]
    public void Current_version_has_three_components()
    {
        var v = Updater.Current;
        Assert.True(v.Major >= 1);
        Assert.True(v.Build >= 0);
        Assert.Equal(-1, v.Revision);
    }

    [Fact]
    public void Asset_name_matches_the_build_flavour_when_not_packaged()
    {
        Assert.False(Packaged.IsPackaged);
        Assert.Contains("MonitorAnchor", Updater.AssetName);
        Assert.EndsWith(".exe", Updater.AssetName);
    }
}
