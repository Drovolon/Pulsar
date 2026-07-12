using System;
using System.IO;
using Pulsar.Common;
using Xunit;

namespace Pulsar.Tests;

/// <summary>
/// HostArgs.Parse is deliberately lenient (a host must start even with odd argv);
/// these tests define exactly what lenient means.
/// </summary>
public class HostArgsTests
{
    [Fact]
    public void Parses_all_five_flags()
    {
        var a = HostArgs.Parse(["--log-dir", "/tmp/logs", "--parent", "1234",
                                "--pipe", "p", "--log-tag", "t", "--mixer-name", "m"]);
        Assert.Equal("/tmp/logs", a.LogDirectory);
        Assert.Equal(1234, a.ParentProcessId);
        Assert.Equal("p", a.PipeName);
        Assert.Equal("t", a.LogTag);
        Assert.Equal("m", a.MixerName);
    }

    [Fact]
    public void Log_directory_defaults_beside_the_host()
    {
        var a = HostArgs.Parse([]);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "logs"), a.LogDirectory);
        Assert.Null(a.ParentProcessId);
        Assert.Null(a.PipeName);
    }

    [Fact]
    public void Last_occurrence_of_a_repeated_flag_wins()
        => Assert.Equal("b", HostArgs.Parse(["--pipe", "a", "--pipe", "b"]).PipeName);

    [Fact]
    public void Non_integer_parent_is_ignored_but_parsing_continues()
    {
        var a = HostArgs.Parse(["--parent", "not-a-pid", "--pipe", "p"]);
        Assert.Null(a.ParentProcessId);
        Assert.Equal("p", a.PipeName);
    }

    [Fact]
    public void A_trailing_flag_with_no_value_is_ignored()
    {
        var a = HostArgs.Parse(["--log-tag", "t", "--pipe"]);
        Assert.Null(a.PipeName);
        Assert.Equal("t", a.LogTag);
    }

    [Fact]
    public void Unknown_flags_are_ignored()
        => Assert.Equal("p", HostArgs.Parse(["--wat", "x", "--pipe", "p"]).PipeName);
}
