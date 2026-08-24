using System.Threading.Tasks;
using Pulsar.Tests.Fakes;
using Xunit;

namespace Pulsar.Tests;

public sealed class BgmMuterTests
{
    [Fact]
    public async Task Listening_mutes_bgm_and_restores_the_previous_state()
    {
        var game = new FakeGameBgmControl();
        await using var muter = new BgmMuter(new Configuration(), game);

        await muter.SetListening(true);
        Assert.True(game.Muted);

        await muter.SetListening(false);
        Assert.False(game.Muted);
        Assert.Equal(2, game.SetCount);
    }

    [Fact]
    public async Task Overlapping_reasons_restore_only_after_the_last_reason_clears()
    {
        var game = new FakeGameBgmControl();
        await using var muter = new BgmMuter(new Configuration(), game);

        await muter.SetListening(true);
        await muter.SetBroadcasting(true);
        await muter.SetListening(false);

        Assert.True(game.Muted);
        Assert.Equal(1, game.SetCount);

        await muter.SetBroadcasting(false);
        Assert.False(game.Muted);
        Assert.Equal(2, game.SetCount);
    }

    [Fact]
    public async Task The_two_config_options_are_independent_and_refresh_live()
    {
        var config = new Configuration
        {
            MuteGameBgmWhileListening = false,
            MuteGameBgmWhileBroadcasting = true,
        };
        var game = new FakeGameBgmControl();
        await using var muter = new BgmMuter(config, game);

        await muter.SetListening(true);
        Assert.False(game.Muted);

        config.MuteGameBgmWhileListening = true;
        await muter.Refresh();
        Assert.True(game.Muted);

        config.MuteGameBgmWhileListening = false;
        await muter.Refresh();
        Assert.False(game.Muted);

        await muter.SetBroadcasting(true);
        Assert.True(game.Muted);
    }

    [Fact]
    public async Task An_originally_muted_game_is_left_muted()
    {
        var game = new FakeGameBgmControl(true);
        await using var muter = new BgmMuter(new Configuration(), game);

        await muter.SetListening(true);
        await muter.SetListening(false);

        Assert.True(game.Muted);
        Assert.Equal(0, game.SetCount);
    }

    [Fact]
    public async Task A_manual_unmute_is_not_overwritten_when_the_session_ends()
    {
        var game = new FakeGameBgmControl();
        await using var muter = new BgmMuter(new Configuration(), game);
        await muter.SetListening(true);

        game.UserSetsMuted(false);
        await muter.SetListening(false);

        Assert.False(game.Muted);
        Assert.Equal(1, game.SetCount);
    }

    [Fact]
    public async Task Disposal_restores_bgm_during_an_active_session()
    {
        var game = new FakeGameBgmControl();
        var muter = new BgmMuter(new Configuration(), game);
        await muter.SetBroadcasting(true);

        await muter.DisposeAsync();

        Assert.False(game.Muted);
        Assert.Equal(2, game.SetCount);
    }
}
