using System;
using System.IO;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Scd;

namespace Pulsar.Playback;

/// <summary>
/// Takes a .scd file path and returns (hopefully) raw playable audio bytes.
/// Needs to be executed plugin-side, since it uses Lumina. Audio host is sent raw Vorbis bytes.
/// </summary>
internal static class ScdReader
{
    public static byte[] ExtractAudio(string path)
    {
        var scd = Plugin.DataManager.GameData.GetFileFromDisk<ScdFile>(path);
        var audio = scd.GetAudio(0);

        if (audio.AudioBasicDesc.Format != AudioFormat.OggVorbis)
            throw new NotSupportedException(
                $"Only Vorbis SCD codec supported, not {audio.AudioBasicDesc.Format}: {Path.GetFileName(path)}");

        return audio.AudioData;
    }

    // SCD files magic bytes: "SEDBSSCF"
    public static bool IsScd(string path)
    {
        try
        {
            Span<byte> head = stackalloc byte[8];
            using var fs = File.OpenRead(path);
            return fs.Read(head) == head.Length && head.SequenceEqual("SEDBSSCF"u8);
        }
        catch
        {
            return false;
        }
    }
}
