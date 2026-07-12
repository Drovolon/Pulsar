namespace Pulsar.Common;

/// <summary>
/// Holds arguments passed to subprocesses (the audio and transcode hosts).
/// </summary>
public sealed class HostArgs
{
    /// <summary>Where to write log files (--log-dir).</summary>
    public required string LogDirectory { get; init; }

    /// <summary>
    /// PID of the game process (--parent).
    /// </summary>
    public int? ParentProcessId { get; init; }

    /// <summary>
    /// The name of the NamedPipe to use for RPC. (--pipe).
    /// </summary>
    public string? PipeName { get; init; }

    /// <summary>
    /// Prefix for log files (--log-tag). Used to run multiple audio hosts (broadcast/listening).
    /// </summary>
    public string? LogTag { get; init; }

    /// <summary>
    /// Name for the audio session (--mixer-name). Doesn't seem to work on Linux/Wine, sadly.
    /// </summary>
    public string? MixerName { get; init; }

    public static HostArgs Parse(string[] args)
    {
        string? logDirectory = null;
        int? parentProcessId = null;
        string? pipeName = null;
        string? logTag = null;
        string? mixerName = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--log-dir":
                    logDirectory = Next() ?? logDirectory;
                    break;
                case "--parent":
                    if (Next() is { } pidStr && int.TryParse(pidStr, out var pid))
                        parentProcessId = pid;
                    break;
                case "--pipe":
                    pipeName = Next() ?? pipeName;
                    break;
                case "--log-tag":
                    logTag = Next() ?? logTag;
                    break;
                case "--mixer-name":
                    mixerName = Next() ?? mixerName;
                    break;
            }

            continue;

            string? Next() => i + 1 < args.Length ? args[++i] : null;
        }

        return new HostArgs
        {
            LogDirectory = logDirectory ?? Path.Combine(AppContext.BaseDirectory, "logs"),
            ParentProcessId = parentProcessId,
            PipeName = pipeName,
            LogTag = logTag,
            MixerName = mixerName,
        };
    }
}
