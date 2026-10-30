namespace GameServer.Gateway.Tests;

public class QuestTests
{
    const string Strand = "1_1_1", Town = "1_1_town", Coast = "1_1_2";
    const string Hillock = "Metadata/Monsters/ZombieBoss/ZombieBossHillockNormal";

    [Fact]
    public void Killing_Hillock_on_the_Strand_sets_KilledHillock()
    {
        Assert.Equal(["a1q1-KilledHillock"], Quests.OnMonsterKilled(Strand, Hillock));
        Assert.Empty(Quests.OnMonsterKilled(Strand, "Metadata/Monsters/Zombies/ZombieBite"));
        Assert.Empty(Quests.OnMonsterKilled(Coast, Hillock));
    }

    [Fact]
    public void Coming_near_Hillock_on_the_Strand_sets_SeenHillock()
    {
        Assert.Equal([Hillock], Quests.Watched(Strand));
        Assert.Empty(Quests.Watched(Coast));
        Assert.Equal(["a1q1-SeenHillock"], Quests.OnMonsterSeen(Strand, Hillock));
        Assert.Empty(Quests.OnMonsterSeen(Strand, "Metadata/Monsters/Zombies/ZombieBite"));
        Assert.Empty(Quests.OnMonsterSeen(Coast, Hillock));
    }

    [Fact]
    public void The_Strands_force_wall_stands_until_Hillock_is_dead()
    {
        const string wall = "Metadata/Terrain/Act1/Area1/Objects/Tutorial_Blocker_3";
        Assert.Equal("a1q1-KilledHillock", Quests.RoomObjectRemovedBy(Strand, wall));
        // The opening tutorial's wall and the Doorman are not managed yet: not created.
        Assert.Null(Quests.RoomObjectRemovedBy(Strand, "Metadata/Terrain/Act1/Area1/Objects/Tutorial_Blocker_1"));
        Assert.Null(Quests.RoomObjectRemovedBy(Strand, "Metadata/NPC/Act1/Doorman"));
        Assert.Null(Quests.RoomObjectRemovedBy(Coast, wall));
    }

    [Fact]
    public void Killing_Hillock_levels_the_character_to_two()
    {
        Assert.Equal(2, Quests.LevelAfterKill(Strand, Hillock));
        Assert.Equal(0, Quests.LevelAfterKill(Strand, "Metadata/Monsters/Zombies/ZombieBite"));
        Assert.Equal(0, Quests.LevelAfterKill(Coast, Hillock));
    }

    [Fact]
    public void Portals_are_off_where_the_area_has_MapNoPortals()
    {
        // WorldAreas 1_1_1 ModsKeys [1] -> Mods row 1 MapNoPortals: StatsKey1 0 = map_disable_portal_use, 1.
        var mods = AreaModCatalog.Parse(
            """[{"Id":"1_1_1","ModsKeys":[1]},{"Id":"1_1_2","ModsKeys":[]}]""",
            """[{"Id":"Other","StatsKey1":1,"Stat1Min":5},{"Id":"MapNoPortals","StatsKey1":0,"Stat1Min":1,"StatsKey2":null}]""",
            """[{"Id":"map_disable_portal_use"},{"Id":"something_else"}]""");
        Assert.True(mods.DisablesPortals(Strand));
        Assert.False(mods.DisablesPortals(Coast));
        Assert.False(mods.DisablesPortals("no_such_area"));
    }

    [Fact]
    public void The_Strands_way_into_Lioneyes_Watch_needs_Hillock_dead()
    {
        Assert.Equal("a1q1-KilledHillock", Quests.FlagToEnter(Strand, Town));
        Assert.Null(Quests.FlagToEnter(Coast, Town));
        Assert.Null(Quests.FlagToEnter(Town, Strand));
    }

    [Fact]
    public void Quest_flags_go_to_the_client_as_their_QuestFlags_rows()
    {
        // data/tables/QuestFlags.json: row index = the flag the client's Player component keeps (16-waypoints.md §3).
        var rows = Quests.ParseRows("""[{"Id":"a1q1-KilledHillock"},{"Id":"a1q1-SeenHillock"},{"Id":"a1q1-QuestRewardTaken"}]""");

        Assert.Equal([0, 2], Quests.Rows(["a1q1-QuestRewardTaken", "a1q1-KilledHillock", "no-such-flag"], rows));
    }
}
