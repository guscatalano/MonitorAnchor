using MonitorAnchor;
using Xunit;

namespace MonitorAnchor.Tests;

public class DpiScalingTests
{
    [Theory]
    [InlineData(0, 0, 3, 100, 100, 175)]      // recommended 100, at 100, up to 175
    [InlineData(0, 1, 3, 125, 100, 175)]      // one step up
    [InlineData(-2, 0, 2, 150, 150, 200)]     // recommended 150 (index 2), at recommended, up to index 4 = 200
    [InlineData(-2, -2, 2, 100, 150, 200)]    // two steps below recommended
    [InlineData(0, -2, 3, 100, 100, 175)]     // out-of-range current value is clamped (seen on a primary display)
    [InlineData(0, 9, 3, 175, 100, 175)]      // above max is clamped to max
    public void Decodes_relative_indices_into_percentages(int min, int cur, int max, int percent, int recommended, int maximum)
    {
        var info = DpiScaling.Decode(min, cur, max);
        Assert.Equal(percent, info.Percent);
        Assert.Equal(recommended, info.Recommended);
        Assert.Equal(maximum, info.Max);
    }

    [Fact]
    public void Nonsense_values_give_zero_percent()
    {
        Assert.Equal(0, DpiScaling.Decode(-20, 0, 0).Percent);
    }

    [Theory]
    [InlineData(125, 100, 1)]
    [InlineData(100, 150, -2)]
    [InlineData(150, 150, 0)]
    public void Encodes_a_percentage_as_a_relative_index(int percent, int recommended, int expected)
    {
        Assert.Equal(expected, DpiScaling.EncodeRel(percent, recommended));
    }

    [Fact]
    public void Rejects_values_that_are_not_windows_steps()
    {
        Assert.Null(DpiScaling.EncodeRel(110, 100));
    }
}
