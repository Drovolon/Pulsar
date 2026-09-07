using System;
using System.IO;
using System.Threading;

namespace Pulsar.Playback;

internal sealed record ScdConversionProgress(int Converted = 0, int Skipped = 0, int Failed = 0,
                                            string? LastError = null);

internal static class ScdFolderConverter
{
    internal static void Convert(string inputFolder, string outputFolder, Func<string, byte[]> extractAudio,
                                 Action<ScdConversionProgress> report, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        var input = Path.GetFullPath(inputFolder);
        var output = Path.GetFullPath(outputFolder);
        if (!Directory.Exists(input)) throw new DirectoryNotFoundException("The input folder does not exist.");

        var progress = new ScdConversionProgress();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var path in Directory.EnumerateFiles(input, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (!Path.GetExtension(path).Equals(".scd", StringComparison.OrdinalIgnoreCase)) continue;

            var relativePath = Path.GetRelativePath(input, path);
            var destination = Path.Combine(output, Path.ChangeExtension(relativePath, ".ogg"));
            try
            {
                if (File.Exists(destination))
                {
                    progress = progress with { Skipped = progress.Skipped + 1 };
                }
                else
                {
                    var bytes = extractAudio(path);
                    ct.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.WriteAllBytes(temporary, bytes);
                        ct.ThrowIfCancellationRequested();
                        File.Move(temporary, destination);
                    }
                    finally
                    {
                        File.Delete(temporary);
                    }
                    progress = progress with { Converted = progress.Converted + 1 };
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                progress = progress with { Failed = progress.Failed + 1, LastError = $"{relativePath}: {ex.Message}" };
            }
            report(progress);
        }
        ct.ThrowIfCancellationRequested();
    }
}
