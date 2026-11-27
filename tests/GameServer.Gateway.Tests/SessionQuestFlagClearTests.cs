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
    public void Four_of_the_every_character_flags_are_session_rows_so_the_clear_must_exclude_them()
    {
        var states = QuestStates.Default;
        string[] session = [.. Quests.EveryCharacter.Where(states.IsSession)];
        Assert.Equal(
            ["CanEditHideout", "HideoutJukeboxEnabled", "HideoutUnlocked", "ThisIsMyHideout"],
            [.. session.Order()]);
        // MapDeviceActive is now granted transiently in hideouts / on Map Device activation rather than globally.
        Assert.True(states.IsSession("MapDeviceActive"));
        Assert.DoesNotContain("MapDeviceActive", Quests.EveryCharacter);
        // The other one is not a session row, so it is saved and must survive an area change either way.
        Assert.Equal(["EndGameAvailable"],
            [.. Quests.EveryCharacter.Where(f => !states.IsSession(f)).Order()]);

        const string sessionFlag = "a1q6-FairgravesAggro";
        const string persistentFlag = "a1q1-KilledHillock";
        Assert.True(states.IsSession(sessionFlag));
        Assert.DoesNotContain(sessionFlag, Quests.EveryCharacter);
        Assert.False(states.IsSession(persistentFlag));

        var player = new Player(new(0x1234, "test_area", 100, 100, 50, 50, null, []), 1, 1)
        {
            InstanceNumber = 42,
            Character = { Name = "SessionFlagTest" },
        };
        player.Character.QuestFlags.UnionWith(Quests.EveryCharacter);
        player.Character.QuestFlags.Add(sessionFlag);
        player.Character.QuestFlags.Add(persistentFlag);

        // Same area + same instance (e.g. ascendancy/descendancy reload): keeps session flags.
        Game.ClearSessionQuestFlags(player, 0x1234, 42);
        Assert.Contains(sessionFlag, player.Character.QuestFlags);
        Assert.Contains(persistentFlag, player.Character.QuestFlags);
        Assert.All(Quests.EveryCharacter, f => Assert.Contains(f, player.Character.QuestFlags));

        // Same area + new instance (e.g. Ctrl-click fresh instance or another map on the same tileset): clears non-EveryCharacter session flags.
        Game.ClearSessionQuestFlags(player, 0x1234, 43);
        Assert.DoesNotContain(sessionFlag, player.Character.QuestFlags);
        Assert.Contains(persistentFlag, player.Character.QuestFlags);
        Assert.All(Quests.EveryCharacter, f => Assert.Contains(f, player.Character.QuestFlags));

        // Different area: clears non-EveryCharacter session flags while keeping EveryCharacter and non-session flags.
        player.Character.QuestFlags.Add(sessionFlag);
        Game.ClearSessionQuestFlags(player, 0x5678, 44);
        Assert.DoesNotContain(sessionFlag, player.Character.QuestFlags);
        Assert.Contains(persistentFlag, player.Character.QuestFlags);
        Assert.All(Quests.EveryCharacter, f => Assert.Contains(f, player.Character.QuestFlags));
    }
}
