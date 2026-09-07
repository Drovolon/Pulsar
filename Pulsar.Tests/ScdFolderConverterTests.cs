using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Pulsar.Playback;
using Xunit;

namespace Pulsar.Tests;

public sealed class ScdFolderConverterTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("pulsar-scd-folder-");
    private string Input => Path.Combine(root.FullName, "input");
    private string Output => Path.Combine(root.FullName, "output");

    public void Dispose() => root.Delete(true);

    private string AddFile(string relativePath)
    {
        var path = Path.Combine(Input, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fixture");
        return path;
    }

    [Fact]
    public void Mirrors_subfolders_matches_extensions_case_insensitively_and_preserves_bytes()
    {
        var first = AddFile("music/theme.scd");
        var second = AddFile("music/deeper/theme.SCD");
        AddFile("notes.txt");
        AddFile("music/not.scd.backup");
        byte[] audio = [.. "OggS"u8, 0, 1, 255];
        var extracted = new List<string>();
        ScdConversionProgress? result = null;

        ScdFolderConverter.Convert(Input, Output, path =>
        {
            extracted.Add(path);
            return audio;
        }, value => result = value, CancellationToken.None);

        Assert.Equal(2, extracted.Count);
        Assert.Contains(first, extracted);
        Assert.Contains(second, extracted);
        Assert.Equal(audio, File.ReadAllBytes(Path.Combine(Output, "music/theme.ogg")));
        Assert.Equal(audio, File.ReadAllBytes(Path.Combine(Output, "music/deeper/theme.ogg")));
        Assert.Equal(2, Directory.GetFiles(Output, "*", SearchOption.AllDirectories).Length);
        Assert.Equal(new ScdConversionProgress(Converted: 2), result);
    }

    [Fact]
    public void Existing_outputs_are_preserved_without_extracting_again()
    {
        AddFile("theme.scd");
        Directory.CreateDirectory(Output);
        var destination = Path.Combine(Output, "theme.ogg");
        File.WriteAllText(destination, "existing audio");
        ScdConversionProgress? result = null;

        ScdFolderConverter.Convert(Input, Output, _ => throw new Exception("Must not extract"),
                                   value => result = value, CancellationToken.None);

        Assert.Equal("existing audio", File.ReadAllText(destination));
        Assert.Equal(new ScdConversionProgress(Skipped: 1), result);
    }

    [Fact]
    public void Bad_files_are_reported_without_stopping_other_files()
    {
        AddFile("broken.scd");
        AddFile("unsupported.scd");
        AddFile("valid.scd");
        ScdConversionProgress? result = null;

        ScdFolderConverter.Convert(Input, Output, path => Path.GetFileName(path) switch
        {
            "broken.scd" => throw new InvalidDataException("Invalid SCD"),
            "unsupported.scd" => throw new NotSupportedException("Unsupported codec"),
            _ => [1, 2, 3],
        }, value => result = value, CancellationToken.None);

        Assert.Equal(1, result!.Converted);
        Assert.Equal(2, result.Failed);
        Assert.NotNull(result.LastError);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(Output, "valid.ogg")));
        Assert.Single(Directory.GetFiles(Output));
    }

    [Fact]
    public void Cancellation_after_extraction_does_not_publish_an_output()
    {
        AddFile("theme.scd");
        using var cancellation = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() => ScdFolderConverter.Convert(Input, Output, _ =>
        {
            cancellation.Cancel();
            return [1, 2, 3];
        }, _ => { }, cancellation.Token));

        Assert.False(Directory.Exists(Output));
    }

    [Fact]
    public void Missing_input_fails_without_creating_output()
    {
        Assert.Throws<DirectoryNotFoundException>(() => ScdFolderConverter.Convert(Input, Output,
            _ => [], _ => { }, CancellationToken.None));
        Assert.False(Directory.Exists(Output));
    }

    [Fact]
    public void Output_can_be_inside_input_without_reconverting_generated_files()
    {
        AddFile("theme.scd");
        var output = Path.Combine(Input, "converted");
        ScdConversionProgress? result = null;

        ScdFolderConverter.Convert(Input, output, _ => [1, 2, 3], value => result = value, CancellationToken.None);

        Assert.Equal(new ScdConversionProgress(Converted: 1), result);
        Assert.Single(Directory.GetFiles(output, "*", SearchOption.AllDirectories));
    }
}
