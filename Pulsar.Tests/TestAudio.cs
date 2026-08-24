using System;
using System.IO;
using NAudio.SoundFile;

namespace Pulsar.Tests;

/// <summary>
/// Generates small audio fixtures on the fly - no binaries checked in. Requires
/// system libsndfile (already a test dependency via the FilePlayer suites) for FLAC.
/// </summary>
public static class TestAudio
{
    /// <summary>Writes a 16-bit PCM WAV; sample(i) returns [-1,1] for frame i,
    /// duplicated across channels.</summary>
    public static string WriteWav(string path, int sampleRate, int channels, double seconds, Func<int, double> sample)
    {
        var frames = (int)(sampleRate * seconds);
        var dataBytes = frames * channels * 2;
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * 2);
        w.Write((short)(channels * 2));
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(dataBytes);
        for (var i = 0; i < frames; i++)
        {
            var v = (short)(Math.Clamp(sample(i), -1, 1) * short.MaxValue);
            for (var c = 0; c < channels; c++) w.Write(v);
        }

        return path;
    }

    /// <summary>A 997 Hz sine at the given linear amplitude. A full-scale sine measures
    /// ≈ -3.01 LUFS (K-weighting is ~0 dB at 1 kHz), so expected LUFS ≈ 20·log10(amplitude) - 3.01.</summary>
    public static Func<int, double> Sine(int sampleRate, double amplitude, double hz = 997) =>
        i => amplitude * Math.Sin(2 * Math.PI * hz * i / sampleRate);

    public static Func<int, double> Silence { get; } = _ => 0;

    /// <summary>FLAC-encodes a generated stream: a real audio file whose effective bitrate
    /// sits far below the transcode threshold, for the passthrough decision.</summary>
    public static string WriteQuietFlac(string path, int sampleRate, int channels, double seconds, double amplitude = 0)
    {
        var wav = WriteWav(path + ".src.wav", sampleRate, channels, seconds,
                           amplitude == 0 ? Silence : Sine(sampleRate, amplitude));
        using (var reader = new SoundFileReader(wav))
        {
            SoundFileWriter.CreateSoundFile(path, reader, SoundFileMajorFormat.Flac, new SoundFileWriterOptions());
        }

        File.Delete(wav);
        return path;
    }
}
