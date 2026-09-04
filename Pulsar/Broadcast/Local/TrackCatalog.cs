using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Pulsar.Playback;

namespace Pulsar.Broadcast.Local;

/// <summary>A local audio file together with catalog-only presentation data.</summary>
public sealed record LocalTrack(string FilePath, string RelativePath, string DisplayName, string? Origin = null);

/// <summary>A playable subset of a local catalog.</summary>
public sealed record TrackGroup(string Id, string Name, IReadOnlyList<LocalTrack> Tracks);

/// <summary>
/// A track catalog is a set of groups, with the first group always being "All Files".
/// For the mod catalog, other groups are built from Penumbra mod groups. For the regular
/// folder catalog, it only ever uses the single "all files" group. For now, anyway?
/// </summary>
public sealed record TrackCatalog(IReadOnlyList<TrackGroup> Groups)
{
    public const string AllFilesId = "";
    public const string AllFilesName = "All Files";

    public TrackGroup AllFiles => Groups[0];

    public TrackGroup FindGroup(string? id) =>
        id is null ? AllFiles : Groups.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.Ordinal)) ?? AllFiles;

    internal static LocalTrack[] SortTracksByFileName(IEnumerable<LocalTrack> tracks) =>
        tracks.OrderBy(track => Path.GetFileName(track.FilePath), NaturalPathComparer.Instance)
              .ThenBy(track => track.RelativePath, NaturalPathComparer.Instance)
              .ToArray();
}

public interface ITrackCatalogLoader
{
    string RootDirectory { get; }
    Task<TrackCatalog> LoadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Builds the ordinary recursive folder catalog.</summary>
public sealed class FolderTrackCatalogLoader(string rootDirectory) : ITrackCatalogLoader
{
    private static readonly string[] Extensions =
    [
        "*.aac", "*.aiff", "*.flac", "*.m4a",
        "*.mp3", "*.ogg", "*.opus", "*.wav",
        "*.wma", "*.wv", "*.scd",
    ];

    public string RootDirectory { get; } = Path.GetFullPath(rootDirectory);

    public Task<TrackCatalog> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(cancellationToken), cancellationToken);

    internal TrackCatalog Scan(CancellationToken cancellationToken)
    {
        var tracks = TrackCatalog.SortTracksByFileName(Extensions
                                                       .SelectMany(pattern => Directory.EnumerateFiles(
                                                                       RootDirectory, pattern, SearchOption.AllDirectories))
                                                       .Select(path =>
                                                       {
                                                           cancellationToken.ThrowIfCancellationRequested();
                                                           var full = Path.GetFullPath(path);
                                                           var relative = Path.GetRelativePath(RootDirectory, full)
                                                                              .Replace('\\', '/');
                                                           return new LocalTrack(
                                                               full, relative, Path.GetFileName(full),
                                                               Path.GetFileName(
                                                                   RootDirectory.TrimEnd(
                                                                       Path.DirectorySeparatorChar,
                                                                       Path.AltDirectorySeparatorChar)));
                                                       }));

        return new TrackCatalog([new TrackGroup(TrackCatalog.AllFilesId, TrackCatalog.AllFilesName, tracks)]);
    }
}

/// <summary>
/// Builds a mod catalog that tries to infer "playlists" using Penumbra groups.
/// Based on hand-inspecting a handful of friends' Thunderdome.exe mods they had loaded music into via Soundy.
///
/// This is all extremely best-effort. The Penumbra IPC doesn't appear to offer the data needed to actually
/// do this via IPC. So, we directly parse `group_*.json` files, looking for groups that redirect a single
/// .scd file to multiple different backing files. In the case of Thunderdome, that's dam.scd.
///
/// This function is conservative and basically just gives up if it runs into something weird.
///
/// Note, "All Files" is always an option still. The inferred groups are hopefully useful. If not, oh well.
/// </summary>
public sealed class ModTrackCatalogLoader(string rootDirectory) : ITrackCatalogLoader
{
    public string RootDirectory { get; } = Path.GetFullPath(rootDirectory);

    public Task<TrackCatalog> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Load(cancellationToken), cancellationToken);

    private TrackCatalog Load(CancellationToken cancellationToken)
    {
        var baseCatalog = new FolderTrackCatalogLoader(RootDirectory).Scan(cancellationToken);
        var byRelativePath = new Dictionary<string, LocalTrack>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in baseCatalog.AllFiles.Tracks) byRelativePath.TryAdd(track.RelativePath, track);
        var groups = new List<TrackGroup> { baseCatalog.AllFiles };

        // support new penumbra
        if (TryReadEmbeddedGroups(byRelativePath, groups, cancellationToken)) return new TrackCatalog(groups);

        foreach (var groupFile in Directory.EnumerateFiles(RootDirectory, "group_*.json", SearchOption.TopDirectoryOnly)
                                           .OrderBy(path => path, NaturalPathComparer.Instance))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var document = ParseJson(groupFile);
                if (TryReadGroup(document.RootElement, Path.GetFileName(groupFile),
                                 Path.GetFileNameWithoutExtension(groupFile), byRelativePath, out var group))
                    groups.Add(group);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                Plugin.Log.Warning(e, "Ignoring unreadable Penumbra group file {path}", groupFile);
            }
        }

        return new TrackCatalog(groups);
    }

    private bool TryReadEmbeddedGroups(
        IReadOnlyDictionary<string, LocalTrack> byRelativePath, ICollection<TrackGroup> groups,
        CancellationToken cancellationToken)
    {
        var metaFile = Path.Combine(RootDirectory, "meta.json");
        if (!File.Exists(metaFile)) return false;

        try
        {
            using var document = ParseJson(metaFile);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("Groups", out var embeddedGroups) ||
                embeddedGroups.ValueKind != JsonValueKind.Array)
                return false;

            var groupIndex = 0;
            foreach (var embeddedGroup in embeddedGroups.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = embeddedGroup.ValueKind == JsonValueKind.Object &&
                         embeddedGroup.TryGetProperty("Id", out var idElement) &&
                         idElement.ValueKind == JsonValueKind.String &&
                         !string.IsNullOrWhiteSpace(idElement.GetString())
                             ? idElement.GetString()!
                             : $"meta.json#group-{groupIndex + 1:D3}";
                if (TryReadGroup(embeddedGroup, id, $"Group {groupIndex + 1}", byRelativePath, out var group))
                    groups.Add(group);
                groupIndex++;
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Plugin.Log.Warning(e, "Ignoring unreadable Penumbra metadata file {path}", metaFile);
            return false;
        }
    }

    private static JsonDocument ParseJson(string path) =>
        JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 32,
        });

    private static bool TryReadGroup(
        JsonElement root, string id, string fallbackName,
        IReadOnlyDictionary<string, LocalTrack> byRelativePath, out TrackGroup group)
    {
        group = null!;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("Type", out var type) ||
            type.ValueKind != JsonValueKind.String ||
            !string.Equals(type.GetString(), "Single", StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("Options", out var options) ||
            options.ValueKind != JsonValueKind.Array)
            return false;

        var mappings = new List<OptionMapping>();
        var optionIndex = 0;
        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object)
            {
                optionIndex++;
                continue;
            }

            var optionName =
                option.TryGetProperty("Name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString() ?? ""
                    : "";
            if (!option.TryGetProperty("Files", out var files) || files.ValueKind != JsonValueKind.Object)
            {
                optionIndex++;
                continue;
            }

            foreach (var mapping in files.EnumerateObject())
            {
                if (!mapping.Name.EndsWith(".scd", StringComparison.OrdinalIgnoreCase) ||
                    mapping.Value.ValueKind != JsonValueKind.String ||
                    mapping.Value.GetString() is not { } backingPath ||
                    !backingPath.EndsWith(".scd", StringComparison.OrdinalIgnoreCase) ||
                    !byRelativePath.TryGetValue(backingPath.Replace('\\', '/'), out var physicalTrack))
                    continue;

                mappings.Add(new OptionMapping(optionIndex, optionName, mapping.Name.Replace('\\', '/'), physicalTrack));
            }

            optionIndex++;
        }

        // in a mapping like:
        //     {"dam.scd" => "song1.scd", "dam.scd" => "song2.scd", "dam.scd" => "song3.scd",
        //      "some_other_thing.scd" => "effect.scd", ... }
        // this groups by the key (e.g. dam.scd) and picks the most frequent option
        var mostFrequentGamePath = mappings.GroupBy(mapping => mapping.GamePath, StringComparer.OrdinalIgnoreCase)
                                           .Select(candidate => new
                                           {
                                               GamePath = candidate.Key,
                                               OptionCount = candidate.Select(mapping => mapping.OptionIndex)
                                                                      .Distinct()
                                                                      .Count(),
                                               TrackCount = candidate.Select(mapping => mapping.Track.FilePath)
                                                                     .Distinct(StringComparer.OrdinalIgnoreCase)
                                                                     .Count(),
                                           })
                                           .Where(candidate => candidate.OptionCount >= 2 && candidate.TrackCount >= 2)
                                           .OrderByDescending(candidate => candidate.OptionCount)
                                           .ThenBy(candidate => candidate.GamePath, StringComparer.OrdinalIgnoreCase)
                                           .FirstOrDefault();
        if (mostFrequentGamePath is null) return false;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tracks = mappings
                     .Where(mapping => string.Equals(mapping.GamePath, mostFrequentGamePath.GamePath,
                                                     StringComparison.OrdinalIgnoreCase))
                     .OrderBy(mapping => mapping.OptionIndex)
                     .Where(mapping => seen.Add(mapping.Track.FilePath))
                     .Select(mapping => mapping.Track with
                     {
                         DisplayName = string.IsNullOrWhiteSpace(mapping.OptionName)
                                           ? mapping.Track.DisplayName
                                           : mapping.OptionName,
                     })
                     .ToArray();
        if (tracks.Length < 2) return false;

        var name = root.TryGetProperty("Name", out var groupName) &&
                   groupName.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(groupName.GetString())
                       ? groupName.GetString()!
                       : fallbackName;
        group = new TrackGroup(id, name, tracks);
        return true;
    }

    private sealed record OptionMapping(int OptionIndex, string OptionName, string GamePath, LocalTrack Track);
}
