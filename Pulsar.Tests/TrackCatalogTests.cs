using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Pulsar.Broadcast;
using Pulsar.Broadcast.Local;
using Pulsar.Playback;
using Xunit;

namespace Pulsar.Tests;

public sealed class TrackCatalogTests : IDisposable
{
    private readonly DirectoryInfo dir = Directory.CreateTempSubdirectory("pulsar-catalog-test-");

    public void Dispose() => dir.Delete(true);

    private void Track(string relativePath)
    {
        var full = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var parent = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(parent);
        File.WriteAllBytes(full, [0]);
    }

    private void Group(string fileName, string json, string? subdirectory = null)
    {
        var parent = subdirectory is null ? dir.FullName : Path.Combine(dir.FullName, subdirectory);
        Directory.CreateDirectory(parent);
        File.WriteAllText(Path.Combine(parent, fileName), json);
    }

    [Fact]
    public async Task Folder_catalog_sorts_naturally_by_filename_before_directory()
    {
        Track("a/10 - Outro.scd");
        Track("z/2 - Verse.scd");
        Track("m/1 - Intro.scd");

        var catalog = await new FolderTrackCatalogLoader(dir.FullName).LoadAsync();

        Assert.Equal(["1 - Intro.scd", "2 - Verse.scd", "10 - Outro.scd"],
                     catalog.AllFiles.Tracks.Select(track => Path.GetFileName(track.FilePath)));
    }

    [Fact]
    public async Task Mod_catalog_always_has_all_files_and_infers_a_shared_scd_redirect_group()
    {
        Track("soundy/songs/392_Spiritbox.scd");
        Track("soundy/songs/5431_Left Behind.scd");
        Track("sound/Mylist/unreferenced.scd");
        Group("group_004_music.json", """
                                      {
                                        "Name": "MUSIC // ROCK",
                                        "Type": "Single",
                                        "Options": [
                                          { "Name": "Off", "Files": {} },
                                          { "Name": "Spiritbox - Circle With Me", "Files": {
                                              "sound/dam.scd": "SOUNDY\\SONGS\\392_spiritbox.scd" } },
                                          { "Name": "The Plot In You - Left Behind", "Files": {
                                              "sound/dam.scd": "soundy/songs/5431_left behind.scd" } }
                                        ]
                                      }
                                      """);

        var catalog = await new ModTrackCatalogLoader(dir.FullName).LoadAsync();

        Assert.Equal(2, catalog.Groups.Count);
        Assert.Equal(3, catalog.AllFiles.Tracks.Count); // fallback includes even unreferenced files
        var rock = catalog.Groups[1];
        Assert.Equal("group_004_music.json", rock.Id);
        Assert.Equal("MUSIC // ROCK", rock.Name);
        Assert.Equal(["Spiritbox - Circle With Me", "The Plot In You - Left Behind"],
                     rock.Tracks.Select(track => track.DisplayName));
        Assert.All(rock.Tracks, track => Assert.True(File.Exists(track.FilePath)));
    }

    [Fact]
    public async Task Inferred_mod_groups_preserve_penumbra_option_order()
    {
        Track("soundy/songs/100_Hundred.scd");
        Track("soundy/songs/20_Twenty.scd");
        Group("group_004_music.json", """
                                      {
                                        "Name": "MUSIC",
                                        "Type": "Single",
                                        "Options": [
                                          { "Name": "Hundred", "Files": {
                                              "sound/dam.scd": "soundy/songs/100_Hundred.scd" } },
                                          { "Name": "Twenty", "Files": {
                                              "sound/dam.scd": "soundy/songs/20_Twenty.scd" } }
                                        ]
                                      }
                                      """);

        var catalog = await new ModTrackCatalogLoader(dir.FullName).LoadAsync();

        Assert.Equal(["Hundred", "Twenty"], catalog.Groups[1].Tracks.Select(track => track.DisplayName));
    }

    [Fact]
    public async Task Malformed_root_groups_and_valid_backup_groups_leave_only_all_files()
    {
        Track("song.scd");
        Group("group_001_broken.json", "{ definitely not json");
        Group("group_002_backup.json", """
                                       { "Name": "OLD", "Type": "Single", "Options": [
                                         { "Name": "One", "Files": { "sound/dam.scd": "song.scd" } },
                                         { "Name": "Two", "Files": { "sound/dam.scd": "song.scd" } }
                                       ] }
                                       """, "backup");

        var catalog = await new ModTrackCatalogLoader(dir.FullName).LoadAsync();

        Assert.Single(catalog.Groups);
        Assert.Equal(TrackCatalog.AllFilesId, catalog.AllFiles.Id);
    }

    [Fact]
    public async Task Unrelated_scd_redirects_do_not_form_a_group()
    {
        Track("songs/one.scd");
        Track("songs/two.scd");
        Group("group_001_sounds.json", """
                                       { "Name": "SOUNDS", "Type": "Single", "Options": [
                                         { "Name": "One", "Files": { "sound/one.scd": "songs/one.scd" } },
                                         { "Name": "Two", "Files": { "sound/two.scd": "songs/two.scd" } }
                                       ] }
                                       """);

        var catalog = await new ModTrackCatalogLoader(dir.FullName).LoadAsync();

        Assert.Single(catalog.Groups);
    }

    [Fact]
    public async Task Missing_backing_paths_are_ignored()
    {
        Track("songs/one.scd");
        Group("group_001_music.json", """
                                      { "Name": "MUSIC", "Type": "Single", "Options": [
                                        { "Name": "One", "Files": { "sound/dam.scd": "songs/one.scd" } },
                                        { "Name": "Missing", "Files": { "sound/dam.scd": "songs/missing.scd" } }
                                      ] }
                                      """);

        var catalog = await new ModTrackCatalogLoader(dir.FullName).LoadAsync();

        Assert.Single(catalog.Groups); // fewer than two valid distinct backing files
    }

    [Fact]
    public async Task Group_uses_the_game_path_redirected_by_the_most_options()
    {
        Track("songs/one.scd");
        Track("songs/two.scd");
        Track("songs/three.scd");
        Track("effects/one.scd");
        Track("effects/two.scd");
        Group("group_001_music.json", """
                                      { "Name": "MUSIC", "Type": "Single", "Options": [
                                        { "Name": "One", "Files": {
                                            "sound/dam.scd": "songs/one.scd",
                                            "sound/effect.scd": "effects/one.scd" } },
                                        { "Name": "Two", "Files": {
                                            "sound/dam.scd": "songs/two.scd",
                                            "sound/effect.scd": "effects/two.scd" } },
                                        { "Name": "Three", "Files": {
                                            "sound/dam.scd": "songs/three.scd" } }
                                      ] }
                                      """);

        var catalog = await new ModTrackCatalogLoader(dir.FullName).LoadAsync();

        Assert.Equal(["One", "Two", "Three"], catalog.Groups[1].Tracks.Select(track => track.DisplayName));
        Assert.All(catalog.Groups[1].Tracks, track => Assert.StartsWith("songs/", track.RelativePath));
    }

    [Fact]
    public async Task Group_skips_a_more_frequent_game_path_without_distinct_backing_tracks()
    {
        Track("songs/one.scd");
        Track("songs/two.scd");
        Track("effects/shared.scd");
        Group("group_001_music.json", """
                                      { "Name": "MUSIC", "Type": "Single", "Options": [
                                        { "Name": "One", "Files": {
                                            "sound/dam.scd": "songs/one.scd",
                                            "sound/effect.scd": "effects/shared.scd" } },
                                        { "Name": "Two", "Files": {
                                            "sound/dam.scd": "songs/two.scd",
                                            "sound/effect.scd": "effects/shared.scd" } },
                                        { "Name": "Also Shared", "Files": {
                                            "sound/effect.scd": "effects/shared.scd" } }
                                      ] }
                                      """);

        var catalog = await new ModTrackCatalogLoader(dir.FullName).LoadAsync();

        Assert.Equal(["One", "Two"], catalog.Groups[1].Tracks.Select(track => track.DisplayName));
        Assert.All(catalog.Groups[1].Tracks, track => Assert.StartsWith("songs/", track.RelativePath));
    }
}
