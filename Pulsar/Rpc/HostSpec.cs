using System.Collections.Generic;

namespace Pulsar.Rpc;

/// <summary>
/// Everything needed to spawn and reach one host subprocess.
/// </summary>
/// <param name="ExePath">Full path to the host executable in the plugin directory.</param>
/// <param name="PipeName">NamedPipe this instance uses; passed as --pipe.</param>
/// <param name="LogDirectory">Where to write log files; passed as --log-dir.</param>
/// <param name="LogTag">Log file prefix, for multi-instance hosts; passed as --log-tag.</param>
/// <param name="MixerName">
/// Human-readable name for this subprocess's stream in the volume mixer; passed as --mixer-name.
/// </param>
/// <param name="Environment">Extra environment variables for the subprocess.</param>
public sealed record HostSpec(
    string ExePath,
    string PipeName,
    string LogDirectory,
    string LogTag,
    string? MixerName = null,
    IReadOnlyDictionary<string, string>? Environment = null);
