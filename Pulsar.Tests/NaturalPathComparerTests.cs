using System.Linq;
using Pulsar.Playback;
using Xunit;

namespace Pulsar.Tests;

/// <summary>
/// The number-aware ordering folder catalogs rely on for playlists. The comparer
/// exists precisely because ordinal ordering gets track numbers wrong.
/// </summary>
public class NaturalPathComparerTests
{
    private static int Cmp(string? x, string? y) => NaturalPathComparer.Instance.Compare(x, y);

    [Fact]
    public void Numeric_runs_compare_as_numbers_not_text()
    {
        Assert.True(Cmp("track2.mp3", "track10.mp3") < 0); // ordinal would say 10 < 2
        Assert.True(Cmp("11_song.mp3", "113_song.mp3") < 0);
    }

    [Fact]
    public void Leading_zeros_order_deterministically()
    {
        // "007" trims to "" vs "7": the all-zeros side is shorter after trimming, so it orders FIRST.
        Assert.True(Cmp("007.mp3", "7.mp3") < 0);
        // "01" and "1" both trim to "1": numerically equal, so ordinal fallback decides ('0' < '1').
        Assert.True(Cmp("01.mp3", "1.mp3") < 0);
        Assert.Equal(0, Cmp("007.mp3", "007.mp3"));
    }

    [Fact]
    public void Mixed_alpha_digit_runs_interleave()
    {
        Assert.True(Cmp("v2final9.mp3", "v2final10.mp3") < 0);
        Assert.True(Cmp("v2final10.mp3", "v10final1.mp3") < 0); // first run decides: 2 < 10
    }

    [Fact]
    public void Paths_compare_segment_by_segment_and_shorter_wins()
    {
        Assert.True(Cmp("album/track1.mp3", "album/track1.mp3/extra") < 0);
        Assert.True(Cmp("cd1/track9.mp3", "cd2/track1.mp3") < 0);
        // Separators are interchangeable: both split into the same segments.
        Assert.True(Cmp(@"cd1\track9.mp3", "cd2/track1.mp3") < 0);
    }

    [Fact]
    public void Null_orders_first()
    {
        Assert.True(Cmp(null, "a") < 0);
        Assert.True(Cmp("a", null) > 0);
        Assert.Equal(0, Cmp(null, null));
    }

    [Fact]
    public void Letter_comparison_is_case_insensitive()
    {
        // T vs t must not decide; the digit run does.
        Assert.True(Cmp("Track1.mp3", "track2.mp3") < 0);
        Assert.True(Cmp("track2.mp3", "Track10.mp3") < 0);
    }

    [Fact]
    public void Sorts_a_realistic_playlist_in_human_order()
    {
        string[] files = ["10 - Outro.mp3", "2 - Verse.mp3", "1 - Intro.mp3", "03 - Chorus.mp3"];
        var sorted = files.OrderBy(f => f, NaturalPathComparer.Instance).ToArray();
        Assert.Equal(["1 - Intro.mp3", "2 - Verse.mp3", "03 - Chorus.mp3", "10 - Outro.mp3"], sorted);
    }
}
