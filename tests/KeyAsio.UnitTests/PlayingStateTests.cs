using KeyAsio.Configuration;
using KeyAsio.Configuration.Models;
using KeyAsio.Plugins.Contracts.Sync;
using KeyAsio.Sync;
using KeyAsio.Sync.States;

namespace KeyAsio.UnitTests;

public sealed class PlayingStateTests
{
    [Theory]
    [InlineData(Mods.NoMod, false, false, false)]
    [InlineData(Mods.NoMod, true, false, true)]
    [InlineData(Mods.NoMod, false, true, true)]
    [InlineData(Mods.Autoplay, false, false, true)]
    [InlineData(Mods.Cinema, false, false, true)]
    [InlineData(Mods.Relax, false, false, true)]
    [InlineData(Mods.DoubleTime, false, false, false)]
    [InlineData(Mods.Unknown, false, false, false)]
    public void HitsoundPlayback_ChoosesAutoOnlyForAutomaticSessions(
        Mods mods, bool isReplay, bool autoMode, bool expected)
    {
        var context = new SyncSessionContext(new AppSettings()) { PlayMods = mods, IsReplay = isReplay };

        Assert.Equal(expected, context.ShouldAutoPlayHitsounds(autoMode));
    }

    [Theory]
    [InlineData(Mods.NoMod, 1.0)]
    [InlineData(Mods.DoubleTime, 1.5)]
    [InlineData(Mods.Nightcore, 1.5)]
    [InlineData(Mods.HalfTime, 0.75)]
    [InlineData(Mods.Unknown, 1.0)]
    public void PlayMods_UpdatesPlaybackRateWithoutMisinterpretingUnknown(Mods mods, double expectedRate)
    {
        var context = new SyncSessionContext(new AppSettings()) { PlayMods = mods };

        Assert.Equal(expectedRate, context.PlaybackRate);
    }

    [Theory]
    [InlineData(0, 30, 0, false)]
    [InlineData(1, 19, 0, false)]
    [InlineData(1, 20, 0, true)]
    [InlineData(1, 30, 29, true)]
    [InlineData(1, 30, 30, false)]
    public void StableComboBreak_PreservesMemoryWorkarounds(int score, int oldCombo, int newCombo, bool expected)
    {
        Assert.Equal(expected,
            PlayingState.ShouldPlayComboBreak(GameClientType.Stable, score, oldCombo, newCombo));
    }

    [Theory]
    [InlineData(0, 20, 0, false)]
    [InlineData(0, 21, 0, true)]
    [InlineData(0, 30, 29, false)]
    [InlineData(0, 30, 30, false)]
    public void LazerComboBreak_DoesNotRequireScoreOrJudgements(int score, int oldCombo, int newCombo, bool expected)
    {
        Assert.Equal(expected,
            PlayingState.ShouldPlayComboBreak(GameClientType.Lazer, score, oldCombo, newCombo));
    }
}
