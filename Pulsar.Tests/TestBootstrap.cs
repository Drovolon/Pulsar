using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Serilog;
using Serilog.Events;

namespace Pulsar.Tests;

internal static class TestBootstrap
{
    /// <summary>Everything the code under test prints to chat lands here.</summary>
    internal static readonly Fakes.RecordingChatGui Chat = new();

    // Plugin.Log/Framework/Chat are normally injected by Dalamud. Several code paths
    // under test use them unconditionally, so install stand-ins before anything runs.
    // (Their setters are internal for exactly this; InternalsVisibleTo covers us.)
    [ModuleInitializer]
    internal static void Init()
    {
        Plugin.Log = new NullPluginLog();
        Plugin.Framework = new Fakes.FakeFramework();
        Plugin.Chat = Chat;
    }
}

internal sealed class NullPluginLog : IPluginLog
{
    public ILogger Logger { get; } = new LoggerConfiguration().CreateLogger();
    public LogEventLevel MinimumLogLevel { get; set; } = LogEventLevel.Fatal;

    public void Fatal(string messageTemplate, params object[] values) { }
    public void Fatal(Exception? exception, string messageTemplate, params object[] values) { }
    public void Error(string messageTemplate, params object[] values) { }
    public void Error(Exception? exception, string messageTemplate, params object[] values) { }
    public void Warning(string messageTemplate, params object[] values) { }
    public void Warning(Exception? exception, string messageTemplate, params object[] values) { }
    public void Information(string messageTemplate, params object[] values) { }
    public void Information(Exception? exception, string messageTemplate, params object[] values) { }
    public void Info(string messageTemplate, params object[] values) { }
    public void Info(Exception? exception, string messageTemplate, params object[] values) { }
    public void Debug(string messageTemplate, params object[] values) { }
    public void Debug(Exception? exception, string messageTemplate, params object[] values) { }
    public void Verbose(string messageTemplate, params object[] values) { }
    public void Verbose(Exception? exception, string messageTemplate, params object[] values) { }
    public void Write(LogEventLevel level, Exception? exception, string messageTemplate, params object[] values) { }
}

/// <summary>Polling helpers for async convergence, shared by all suites.</summary>
public static class TestWait
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task<bool> Until(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? Timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }

        return condition();
    }

    /// <summary>Asserts the condition becomes true; xunit-friendly message on timeout.</summary>
    public static async Task Assert(Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        if (!await Until(condition, timeout))
            throw new Xunit.Sdk.XunitException($"Timed out waiting for: {because}");
    }

    /// <summary>Awaits a task with the shared test timeout; xunit-friendly message on timeout.</summary>
    public static async Task<T> Within<T>(Task<T> task, string because, TimeSpan? timeout = null)
    {
        try
        {
            return await task.WaitAsync(timeout ?? Timeout);
        }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException($"Timed out waiting for: {because}");
        }
    }

    /// <summary>Same, for tasks with no result.</summary>
    public static async Task Within(Task task, string because, TimeSpan? timeout = null)
    {
        try
        {
            await task.WaitAsync(timeout ?? Timeout);
        }
        catch (TimeoutException)
        {
            throw new Xunit.Sdk.XunitException($"Timed out waiting for: {because}");
        }
    }
}

/// <summary>Builders shared across suites so "a playing track" means one thing everywhere.</summary>
public static class TestData
{
    /// <summary>A plugin configuration that will not write to the process-global fake chat.</summary>
    public static Configuration QuietConfiguration() =>
        new()
        {
            NotifyNearbyBroadcaster = false,
            NotifyNearbyBroadcasterAutoPlayOff = false,
            NotifyNearbyBroadcasterWhileBroadcasting = false,
            NotifyMutedPlayback = false,
            NotifyListeningTrackChanged = false,
            NotifyUnsyncableBroadcast = false,
        };

    /// <summary>Writes a small fake audio file and returns its path.</summary>
    public static string CreateTrack(System.IO.DirectoryInfo dir, string name, int bytes = 16)
    {
        var path = System.IO.Path.Combine(dir.FullName, name);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    /// <summary>A live source snapshot: 5s into a one-minute track.</summary>
    public static Broadcast.SourceSnapshot Snap(string file, bool playing = true, string? next = null) =>
        new(file, next, playing, TimeSpan.FromSeconds(5), DateTimeOffset.UtcNow, new Listening.TrackMeta
        {
            OriginalFileName = System.IO.Path.GetFileName(file),
            DurationMs = 60_000,
        });

    /// <summary>Expected-value math for gain assertions, derived independently of production.</summary>
    public static float DbToLinear(double db) => (float)Math.Pow(10.0, db / 20.0);
}
