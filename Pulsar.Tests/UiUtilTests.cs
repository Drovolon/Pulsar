using System;
using Pulsar.Windows;
using Xunit;

namespace Pulsar.Tests;

public class UiUtilTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(59, "0:59")]
    [InlineData(65, "1:05")]
    [InlineData(3661, "61:01")]   // minutes never roll into hours - by design
    [InlineData(-65, "-1:05")]    // sign once, components absolute (honest + debuggable)
    [InlineData(-5, "-0:05")]
    public void Formats_minutes_and_seconds(int seconds, string expected)
        => Assert.Equal(expected, UiUtil.FormatTime(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Volume_taper_round_trips_exactly_at_the_endpoints()
    {
        Assert.Equal(0f, UiUtil.SliderToAmplitude(0f));
        Assert.Equal(1f, UiUtil.SliderToAmplitude(1f));
        Assert.Equal(0f, UiUtil.AmplitudeToSlider(0f));
        Assert.Equal(1f, UiUtil.AmplitudeToSlider(1f));
    }

    [Fact]
    public void Volume_taper_round_trips_across_the_travel()
    {
        // The Fader control displays AmplitudeToSlider(value) and stores
        // SliderToAmplitude(pos): the pair must be exact inverses or the knob drifts.
        for (var pos = 0f; pos <= 1f; pos += 0.01f)
            Assert.Equal(pos, UiUtil.AmplitudeToSlider(UiUtil.SliderToAmplitude(pos)), 3);
    }
}
