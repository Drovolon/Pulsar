using System;
using System.IO;
using Pulsar.Broadcast.Beefweb;
using Xunit;

namespace Pulsar.Tests;

/// <summary>
/// Path-classification scenarios for Syncability: which beefweb %path% values can be
/// broadcast to listeners. The invariant: only paths we can actually open locally are
/// syncable; everything else gets a reason the DJ can act on.
/// </summary>
public class SyncabilityTests : IDisposable
{
    private readonly DirectoryInfo dir = Directory.CreateTempSubdirectory("pulsar-sync-test-");

    public void Dispose() => dir.Delete(true);

    [Fact]
    public void An_existing_local_file_is_syncable_and_its_path_is_broadcast_verbatim()
    {
        var track = TestData.CreateTrack(dir, "song.flac");

        var verdict = Syncability.Check(track, false);

        Assert.True(verdict.Syncable);
        Assert.Equal(track, verdict.LocalPath);
    }

    [Theory]
    [InlineData("http://radio.example/stream")]
    [InlineData("https://radio.example/stream.mp3")]
    public void Web_streams_are_internet_radio(string url)
    {
        var verdict = Syncability.Check(url, false);

        Assert.False(verdict.Syncable);
        Assert.Equal(UnsyncableReason.InternetRadio, verdict.Reason);
        Assert.Null(verdict.LocalPath);
    }

    [Fact]
    public void Cd_audio_is_unsyncable()
    {
        var verdict = Syncability.Check("cdda://drive/track01", false);

        Assert.False(verdict.Syncable);
        Assert.Equal(UnsyncableReason.CdAudio, verdict.Reason);
    }

    // foobar2000 exposes archive members either via unpack:// or a pipe-separated
    // "archive|member" path; neither is a file we can hand to the transcoder.
    [Theory]
    [InlineData("unpack://zip|file://C:/music/album.zip|track.flac")]
    [InlineData(@"C:\music\album.zip|3")]
    public void Archive_members_are_unsyncable(string path)
    {
        var verdict = Syncability.Check(path, false);

        Assert.False(verdict.Syncable);
        Assert.Equal(UnsyncableReason.Archive, verdict.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_missing_path_is_not_a_local_file(string? path)
    {
        var verdict = Syncability.Check(path, false);

        Assert.False(verdict.Syncable);
        Assert.Equal(UnsyncableReason.NotLocalFile, verdict.Reason);
    }

    [Fact]
    public void A_file_that_does_not_exist_fails_the_existence_check()
    {
        var verdict = Syncability.Check(Path.Combine(dir.FullName, "deleted.flac"), false);

        Assert.False(verdict.Syncable);
        Assert.Equal(UnsyncableReason.FileDoesNotExist, verdict.Reason);
    }

    [Fact]
    public void Under_wine_unix_paths_are_reached_through_the_z_drive()
    {
        // Under Wine, beefweb reports unix paths but Pulsar (a Windows process) must
        // open them as Z:\... - prove the translation by planting a file whose literal
        // Linux name IS the translated form. (Backslashes are legal in Linux names, so
        // "Z:\tmp\...\a.flac" is a single relative filename in the test's cwd.)
        var unixStyle = Path.Combine(dir.FullName, "song.flac");
        var translated = "Z:" + unixStyle.Replace('/', '\\');
        File.WriteAllBytes(translated, new byte[16]);
        try
        {
            var verdict = Syncability.Check(unixStyle, true);

            Assert.True(verdict.Syncable);
            Assert.Equal(translated, verdict.LocalPath);
        } finally
        {
            File.Delete(translated);
        }
    }

    [Fact]
    public void Under_wine_an_unreachable_unix_path_fails_the_existence_check()
    {
        // The real-world Linux failure mode: the file exists at the unix path, but the
        // translated Z: path can't be opened. Must be reported (the Watcher turns this
        // into the "enable the locale hack" chat warning), not silently synced.
        var track = TestData.CreateTrack(dir, "song.flac");

        var verdict = Syncability.Check(track, true);

        Assert.False(verdict.Syncable);
        Assert.Equal(UnsyncableReason.FileDoesNotExist, verdict.Reason);
    }
}
