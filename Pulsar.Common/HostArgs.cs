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

        for (var i = 0; i + 1 < args.Length; i++)
        {
            switch (args[i])
            {
                case "--log-dir":
                    logDirectory = args[++i];
                    break;
                case "--parent":
                    if (int.TryParse(args[i + 1], out var pid))
                        parentProcessId = pid;
                    i++;
                    break;
                case "--pipe":
                    pipeName = args[++i];
                    break;
                case "--log-tag":
                    logTag = args[++i];
                    break;
                case "--mixer-name":
                    mixerName = args[++i];
                    break;
            }
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
