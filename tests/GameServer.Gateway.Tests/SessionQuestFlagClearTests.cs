using Xunit;

namespace GameServer.Gateway.Tests;

/// <summary>
/// Leaving an area drops the connection's session quest flags, and the clearing set excludes
/// the flags every character is given. This test pins the REASON that exclusion exists, which is a fact about the
/// shipped client data rather than about our code: five of the six <see cref="Quests.EveryCharacter"/> flags are
/// also SessionQuestFlags rows, so a clear that went by IsSession alone would strip the hideout's own access, the
/// map device and the jukebox on every area change. If a later client build makes a sixth one a session row, the
/// clear in Travel.ClearSessionQuestFlags keeps working (it excludes the whole EveryCharacter set) and this test
/// fails to say the ground moved.
/// </summary>
public class SessionQuestFlagClearTests
{
    [Fact]
    public void Five_of_the_every_character_flags_are_session_rows_so_the_clear_must_exclude_them()
    {
        var states = QuestStates.Default;
        string[] session = [.. Quests.EveryCharacter.Where(states.IsSession)];
        Assert.Equal(
            ["CanEditHideout", "HideoutJukeboxEnabled", "HideoutUnlocked", "MapDeviceActive", "ThisIsMyHideout"],
            [.. session.Order()]);
        // The other one is not a session row, so it is saved and must survive an area change either way.
        Assert.Equal(["EndGameAvailable"],
            [.. Quests.EveryCharacter.Where(f => !states.IsSession(f)).Order()]);
    }
}
