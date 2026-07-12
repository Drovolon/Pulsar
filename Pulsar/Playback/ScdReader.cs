using System;
using System.IO;
using Lumina;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Scd;

namespace Pulsar.Playback;

/// <summary>
/// As implemented, we rely on Dalamud's instance of Lumina to parse .scd and extract
/// the raw Vorbis data. So, we need to do .scd parsing/loading in the plugin, not in
/// the audio or transcode host, for now, unfortunately.
///
/// Lumina appears to require instantiating game data to function as a library, so
/// it's not super easy to ship our own either, even if the .scd code *itself* doesn't
/// *really* need game data. At best, we could copy/paste their .scd code - but... meh.
/// </summary>
internal static class ScdReader
{
    public static byte[] ExtractAudio(string path) => ExtractAudio(path, Plugin.DataManager.GameData);

    // Used for unit tests, to avoid writing a fake IDataManager from Dalamud
    internal static byte[] ExtractAudio(string path, GameData gameData)
    {
        var scd = gameData.GetFileFromDisk<ScdFile>(path);
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
