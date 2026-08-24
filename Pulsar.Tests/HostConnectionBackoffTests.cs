using System;
using Pulsar.Rpc;
using Xunit;

namespace Pulsar.Tests;

public class HostConnectionBackoffTests
{
    private static readonly TimeSpan Unit = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Cap = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]    // 32s hits the 30s cap
    [InlineData(1000, 30)] // a long outage must not overflow TimeSpan arithmetic
    public void Doubles_from_the_unit_and_caps(int attempt, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), HostConnection<object>.Backoff(attempt, Unit, Cap));

    [Fact]
    public void Scales_with_the_unit() =>
        Assert.Equal(TimeSpan.FromMilliseconds(100),
                     HostConnection<object>.Backoff(3, TimeSpan.FromMilliseconds(25), TimeSpan.FromSeconds(1)));
}
