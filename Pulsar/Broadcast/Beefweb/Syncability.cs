using System.IO;

namespace Pulsar.Broadcast.Beefweb;

public enum UnsyncableReason { NotLocalFile, InternetRadio, CdAudio, Archive, FileDoesNotExist }

// LocalPath is the translated, openable path (what we broadcast) when Syncable; null otherwise.
public readonly record struct SyncVerdict(bool Syncable, string? LocalPath, UnsyncableReason Reason);

/// <summary>
/// Decides whether a beefweb %path% is a syncable local file.
/// </summary>
public static class Syncability
{
    public static SyncVerdict Check(string? path, bool isWine)
    {
        if (string.IsNullOrEmpty(path))
            return new SyncVerdict(false, null, UnsyncableReason.NotLocalFile);

        if (path.StartsWith("http://") || path.StartsWith("https://"))
            return new SyncVerdict(false, null, UnsyncableReason.InternetRadio);
        if (path.StartsWith("cdda://"))
            return new SyncVerdict(false, null, UnsyncableReason.CdAudio);
        if (path.StartsWith("unpack://") || path.Contains('|'))
            return new SyncVerdict(false, null, UnsyncableReason.Archive);

        var local = Translate(path, isWine);
        Plugin.Log.Debug($"Beefweb: checking if {local} exists");
        return File.Exists(local)
            ? new SyncVerdict(true, local, default)
            // this can happen in Linux, if the path contains special characters
            // and Dalamud chokes when handling them
            : new SyncVerdict(false, null, UnsyncableReason.FileDoesNotExist);
    }

    // Support Wine by translating paths using the Z: drive, since beefweb will
    // return a path like /home/user/Music/my_file.mp3.
    private static string Translate(string path, bool isWine)
        => isWine && path.StartsWith('/') ? "Z:" + path.Replace('/', '\\') : path;
}
