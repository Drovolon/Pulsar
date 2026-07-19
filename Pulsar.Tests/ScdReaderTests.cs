using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Lumina;
using Lumina.Data.Parsing.Scd;
using Pulsar.Broadcast.Prepare;
using Pulsar.Common.Api;
using Pulsar.Playback;
using Pulsar.Tests.Fakes;
using Xunit;

namespace Pulsar.Tests;

/// <summary>
/// IsScd gates the whole SCD pipeline (RemotePrepare and RemoteEngineLoad both
/// dispatch on it).
/// </summary>
public class ScdReaderTests : System.IDisposable
{
    private readonly DirectoryInfo dir = Directory.CreateTempSubdirectory("pulsar-scd-test-");
    public void Dispose() => dir.Delete(recursive: true);

    private string Write(string name, byte[] bytes)
    {
        var p = Path.Combine(dir.FullName, name);
        File.WriteAllBytes(p, bytes);
        return p;
    }

    /// <summary>Writes the smallest SCD Lumina needs to parse one unencrypted Vorbis entry.</summary>
    private string WriteVorbisScd(string name, byte[] oggHeader, byte[] audioData)
    {
        var path = Path.Combine(dir.FullName, name);
        var totalSize = 0x60 + 32 + 32 + oggHeader.Length + audioData.Length;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        // BinaryHeader (0x00-0x2f).
        writer.Write("SEDB"u8);
        writer.Write("SSCF"u8);
        writer.Write(3u);
        writer.Write((byte)0);       // little-endian
        writer.Write((byte)4);       // alignment bits
        writer.Write((ushort)0x30);  // header size
        writer.Write((ulong)totalSize);
        writer.Write(0UL);           // timestamp
        writer.Write(new byte[16]);  // reserved

        // ScdHeader (0x30-0x4f): no sound/track entries, one audio offset at 0x50.
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)0);
        writer.Write(0x50u); // empty track table
        writer.Write(0x50u); // one-entry audio table
        writer.Write(0u);    // layout
        writer.Write(0u);    // routing
        writer.Write(0u);    // attributes
        writer.Write((ushort)0);
        writer.Write((ushort)0);

        writer.Write(0x60u); // audio[0] offset
        writer.Write(new byte[0x0c]);

        // AudioBasicDesc (0x60-0x7f).
        writer.Write((uint)audioData.Length);
        writer.Write(1u);
        writer.Write(48_000u);
        writer.Write((int)AudioFormat.OggVorbis);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(32u); // OggVorbisSeekTableHeader, no seek table
        writer.Write(0u);

        // OggVorbisSeekTableHeader (0x80-0x9f), version 0 = no XOR.
        writer.Write((byte)0);
        writer.Write((byte)32);
        writer.Write((byte)0);
        writer.Write(new byte[9]);
        writer.Write(0f);
        writer.Write(0u);
        writer.Write((uint)oggHeader.Length);
        writer.Write(0u);
        writer.Write(0u);

        writer.Write(oggHeader);
        writer.Write(audioData);
        return path;
    }

    private sealed class RecordingPrepareService : IPrepareService
    {
        public string? Operation { get; private set; }
        public byte[]? Bytes { get; private set; }

        public Task<PreparedTrack> PrepareAsync(string originalPath, string transcodeOutPath, CancellationToken ct)
        {
            Operation = "Path";
            return Task.FromResult(new PreparedTrack(originalPath, "blake3", "sha1", 0));
        }

        public Task<PreparedTrack> PrepareBytesAsync(string originalPath, byte[] audioData,
            string transcodeOutPath, CancellationToken ct)
        {
            Operation = "Bytes";
            Bytes = audioData;
            return Task.FromResult(new PreparedTrack(originalPath, "blake3", "sha1", 0));
        }
    }

    /// <summary>
    /// GetFileFromDisk only needs GameData.Options; the public GameData constructor also
    /// initializes Excel and therefore requires a full game sqpack. Build the narrow
    /// disk-loader state Lumina itself uses so this fixture stays game-install independent.
    /// </summary>
    private static GameData CreateDiskOnlyGameData()
    {
        var gameData = (GameData)RuntimeHelpers.GetUninitializedObject(typeof(GameData));
        typeof(GameData).GetProperty(nameof(GameData.Options), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(gameData, new LuminaOptions());
        return gameData;
    }

    [Fact]
    public void Recognizes_the_scd_magic()
        => Assert.True(ScdReader.IsScd(Write("real.scd", [.."SEDBSSCF"u8, 1, 2, 3, 4])));

    [Fact]
    public void A_magic_only_file_is_still_scd()
        => Assert.True(ScdReader.IsScd(Write("bare.scd", [.."SEDBSSCF"u8])));

    [Fact]
    public void A_file_shorter_than_the_magic_is_not_scd()
        => Assert.False(ScdReader.IsScd(Write("short.scd", [1, 2, 3])));

    [Fact]
    public void Wrong_magic_is_not_scd()
        => Assert.False(ScdReader.IsScd(Write("song.mp3", new byte[16])));

    [Fact]
    public void A_missing_file_reads_as_not_scd()
        => Assert.False(ScdReader.IsScd(Path.Combine(dir.FullName, "nope.scd")));

    [Fact]
    public void ExtractAudio_parses_a_real_lumina_scd_and_returns_the_vorbis_stream()
    {
        var path = WriteVorbisScd("audio.scd", "OggS"u8.ToArray(), [1, 2, 3, 4]);
        using var gameData = CreateDiskOnlyGameData();

        Assert.Equal([.."OggS"u8, 1, 2, 3, 4], ScdReader.ExtractAudio(path, gameData));
    }

    [Fact]
    public async Task Scd_dispatch_sends_extracted_bytes_to_both_hosts()
    {
        var path = Write("wire.scd", [.."SEDBSSCF"u8]);
        byte[] extracted = [10, 20, 30];
        var prep = new RecordingPrepareService();
        var engine = new FakeRemoteEngine();

        await prep.PrepareFileAsync(path, "out", _ => extracted, CancellationToken.None);
        await engine.LoadFileAsync(path, TimeSpan.FromSeconds(3), true, _ => extracted, CancellationToken.None);

        Assert.Equal("Bytes", prep.Operation);
        Assert.Same(extracted, prep.Bytes);
        var load = Assert.Single(engine.Calls, c => c.Op == "LoadBytes");
        var args = Assert.IsType<ValueTuple<string, TimeSpan, bool>>(load.Arg);
        Assert.Equal(path, args.Item1);
        Assert.Same(extracted, engine.LastLoadedBytes);
        Assert.Equal(TimeSpan.FromSeconds(3), args.Item2);
        Assert.True(args.Item3);
    }

    [Fact]
    public async Task Extraction_failure_falls_back_to_path_dispatch_for_both_hosts()
    {
        var path = Write("broken.scd", [.."SEDBSSCF"u8]);
        var prep = new RecordingPrepareService();
        var engine = new FakeRemoteEngine();
        static byte[] Fail(string _) => throw new InvalidDataException("bad SCD");

        await prep.PrepareFileAsync(path, "out", Fail, CancellationToken.None);
        await engine.LoadFileAsync(path, TimeSpan.Zero, true, Fail, CancellationToken.None);

        Assert.Equal("Path", prep.Operation);
        Assert.Contains(engine.Calls, c => c.Op == "Load");
        Assert.DoesNotContain(engine.Calls, c => c.Op == "LoadBytes");
    }
}
