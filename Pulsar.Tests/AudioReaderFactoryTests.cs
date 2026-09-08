using System;
using System.IO;
using NAudio.SoundFile;
using NAudio.Wave;
using Pulsar.Common;
using Xunit;

namespace Pulsar.Tests;

public sealed class AudioReaderFactoryTests : IDisposable
{
    private readonly DirectoryInfo dir = Directory.CreateTempSubdirectory("pulsar-reader-test-");
    public void Dispose() => dir.Delete(true);

    [Fact]
    public void Unicode_path_decodes_and_seeks_through_libsndfile()
    {
        var wav = TestAudio.WriteWav(Path.Combine(dir.FullName, "source.wav"), 44100, 2, 1,
                                     TestAudio.Sine(44100, 0.5));
        var ogg = Path.Combine(dir.FullName, "source.ogg");
        using (var source = new SoundFileReader(wav))
            SoundFileWriter.CreateSoundFile(ogg, source, SoundFileMajorFormat.OggVorbis, new SoundFileWriterOptions());

        var folder = Directory.CreateDirectory(Path.Combine(dir.FullName, "音楽"));
        var path = Path.Combine(folder.FullName, "bradeazy, Öwnboss - Louboutin.ogg");
        File.Move(ogg, path);
        using (var reader = AudioReaderFactory.Open(path))
        {
            // Media Foundation fallback can hide a broken libsndfile path on some machines.
            var samples = Assert.IsAssignableFrom<ISampleProvider>(reader);
            Assert.Equal(44100, reader.WaveFormat.SampleRate);
            Assert.Equal(2, reader.WaveFormat.Channels);
            Assert.Equal(TimeSpan.FromSeconds(1), reader.TotalTime);

            var first = new float[2048];
            Assert.Equal(first.Length, samples.Read(first));
            Assert.Contains(first, value => Math.Abs(value) > 0.1);
            var remaining = new byte[8192];
            while (reader.Read(remaining.AsSpan()) > 0) { }

            reader.Position = 0;
            var replay = new float[first.Length];
            Assert.Equal(replay.Length, samples.Read(replay));
            Assert.Equal(first, replay);
        }

        // An exclusive open must succeed immediately; disposal must close the backing FileStream.
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void Failed_decoder_open_releases_the_file()
    {
        var path = Path.Combine(dir.FullName, "invalid.ogg");
        File.WriteAllText(path, "not audio");

        Assert.ThrowsAny<Exception>(() =>
        {
            using var reader = AudioReaderFactory.Open(path);
        });

        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
}
