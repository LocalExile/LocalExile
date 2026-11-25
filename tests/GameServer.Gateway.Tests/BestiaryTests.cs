using GameServer.Core;
using GameServer.Modules.Bestiary;
using Xunit;

namespace GameServer.Gateway.Tests;

public class BestiaryTests
{
    /// <summary>Which areas have a mission and what it holds: CoreLeagues Bestiary is a row the league chances never roll (so
    /// the module reads the area's base chance and map master-mission stats instead), the roll follows those stats and excludes
    /// boss/unique maps, and every map area, with or without capturable beasts among its own packs, gets the row's red and
    /// yellow beasts.</summary>
    [Fact]
    public void Roll_follows_the_map_stats_and_every_map_area_can_complete()
    {
        Assert.True(LeagueChances.Current.LeagueOf(BestiaryTables.LeagueRow)?.NotRolled);
        Assert.Equal(0, LeagueChances.Current.Count("MapAtziri1", BestiaryTables.LeagueRow, new Random(1)));
        Assert.Equal(2, LeagueChances.Current.ChanceOf("MapWorldsStrand", BestiaryTables.LeagueRow, alternate: false).Chance);
        Assert.Equal(0, LeagueChances.Current.ChanceOf("MapWorldsStrand", BestiaryTables.LeagueRow, alternate: true).Chance);
        Assert.Equal(0, LeagueChances.Current.ChanceOf("MapAtziri1", BestiaryTables.LeagueRow).Chance);
        Assert.Equal(0, LeagueChances.Current.ChanceOf("MapWorldsShapersRealm", BestiaryTables.LeagueRow).Chance);

        var empty = new Dictionary<uint, int>();
        var einhar = new Dictionary<uint, int> { [Stat.MapMasterMissionEinharPctChance] = 100 };
        var baseRng = new Random(1);
        int baseHits = Enumerable.Range(0, 1000).Count(_ => BestiaryEncounter.Rolled("MapWorldsStrand", empty, isMap: true, baseRng));
        Assert.InRange(baseHits, 1, 60);
        var ruthlessRng = new Random(1);
        Assert.Equal(0, Enumerable.Range(0, 200).Count(_ => BestiaryEncounter.Rolled("MapWorldsStrand", empty, isMap: true, ruthlessRng, hardMode: true)));
        var noBase = new Dictionary<uint, int> { [Stat.MapExtraContentHasNoBaseChanceToSpawn] = 1 };
        var noBaseRng = new Random(1);
        Assert.Equal(0, Enumerable.Range(0, 200).Count(_ => BestiaryEncounter.Rolled("MapWorldsStrand", noBase, isMap: true, noBaseRng)));

        Assert.True(BestiaryEncounter.Rolled("MapWorldsStrand", einhar, isMap: true, new Random(1)));
        Assert.True(BestiaryEncounter.Rolled("MapWorldsStrand", einhar, isMap: true, new Random(1), hardMode: true));
        Assert.False(BestiaryEncounter.Rolled("MapWorldsStrand", einhar, isMap: false, new Random(1)));
        Assert.False(BestiaryEncounter.Rolled("MapAtziri1", einhar, isMap: true, new Random(1)));
        Assert.False(BestiaryEncounter.Rolled("MapWorldsShapersRealm", einhar, isMap: true, new Random(1)));
        Assert.False(BestiaryEncounter.Rolled("MapAtziri1", new Dictionary<uint, int> { [Stat.MapBestiaryLeague] = 1 }, isMap: true, new Random(1)));
        Assert.False(BestiaryEncounter.Rolled("MapWorldsShapersRealm", new Dictionary<uint, int> { [Stat.MapBestiaryLeague] = 1 }, isMap: true, new Random(1)));
        Assert.True(BestiaryEncounter.Rolled("MapWorldsStrand", new Dictionary<uint, int> { [Stat.MapMasterMissionEinharPctChance] = 50, [Stat.MapMasterMissionEinharChancePlusPct] = 100 }, isMap: true, new Random(1)));
        Assert.True(BestiaryEncounter.Rolled("MapWorldsStrand", new Dictionary<uint, int> { [Stat.MapBestiaryLeague] = 1 }, isMap: true, new Random(1)));
        Assert.True(BestiaryEncounter.Rolled("MapWorldsStrand", new Dictionary<uint, int> { [Stat.MapBestiaryLeague] = 1 }, isMap: true, new Random(1), hardMode: true));
        Assert.False(BestiaryEncounter.Rolled("MapWorldsStrand", new Dictionary<uint, int>(einhar) { [Stat.MapDisableMissions] = 1 }, isMap: true, new Random(1)));
        Assert.False(BestiaryEncounter.Rolled("MapWorldsStrand", new Dictionary<uint, int>(einhar) { [Stat.AtlasKeystoneNoExtraContent] = 1 }, isMap: true, new Random(1)));
        Assert.False(BestiaryEncounter.Rolled("MapWorldsStrand", new Dictionary<uint, int>(einhar) { [Stat.MapNukeEverything] = 1 }, isMap: true, new Random(1)));
        Assert.False(BestiaryEncounter.Rolled("MapWorldsStrand", new Dictionary<uint, int>(einhar) { [Stat.MapAreaUnderlevelled] = 1 }, isMap: true, new Random(1)));
        Assert.False(BestiaryEncounter.Rolled("MapWorldsStrand", new Dictionary<uint, int>(einhar) { [Stat.MapDisableLeagues] = 1 << BestiaryTables.LeagueRow }, isMap: true, new Random(1)));
        Assert.Equal(100, BestiaryEncounter.ForcedChance("Anarchy=50,Bestiary=100"));
        Assert.True(BestiaryEncounter.Rolled("1_2_1", empty, isMap: false, new Random(1), forced: 100));
        Assert.False(BestiaryEncounter.Rolled("MapAtziri1", empty, isMap: true, new Random(1), forced: 100));

        for (int level = 10; level <= 100; level++) Assert.NotNull(BestiaryTables.MissionFor(level));
        Assert.Null(BestiaryTables.MissionFor(9));
        Assert.NotEmpty(BestiaryTables.RedKinds);
        Assert.All(BestiaryTables.RedKinds, red => Assert.NotEmpty(red.Minions));
        var flags = ClientTables.Table("QuestFlags").Select(r => r.Text("Id")).ToHashSet();
        Assert.Contains(BestiaryEncounter.ReceivedFlag, flags);
        Assert.Contains(BestiaryEncounter.CompleteFlag, flags);
        int maps = 0;
        foreach (var area in ClientTables.Table("WorldAreas").Where(a => a.Bool("IsMapArea") && a.Text("Id") is { Length: > 0 }))
        {
            maps++;
            var mapPlan = BestiaryTables.PlanFor(area.Text("Id")!, 70, new Random(maps));
            Assert.NotNull(mapPlan);
            Assert.Single(mapPlan.Reds);
            Assert.InRange(mapPlan.Yellow, 3, 4);
            Assert.NotEmpty(mapPlan.YellowPool);
        }
        Assert.True(maps > 0);
        Assert.Empty(BestiaryTables.PlanFor("MapAtziri1", 30, new Random(1))!.Reds);
    }

    /// <summary>One instance's mission from its first kill to its last: Einhar comes once, at the first capture; a kill of
    /// anything else or of a beast already taken changes nothing; the last capture completes the mission. A player who comes
    /// in later (a party member, a portal re-entry) reads the same flags, since the mission is the instance's. The effect
    /// bodies are the official bytes.</summary>
    [Fact]
    public void Mission_lifecycle_is_the_instances_and_the_effects_are_the_official_bytes()
    {
        var run = new BestiaryRun { MissionId = "11", Level = 70 };
        object[] bodies = [new(), new(), new(), new(), new()];
        for (int i = 0; i < bodies.Length; i++) run.Add((uint)(0x08000001 + i), bodies[i], red: i == 0);
        Assert.Equal((5, 0, false, false), (run.Total, run.Captured, run.Received, run.Complete));
        Assert.Null(run.Kill(new object()));
        var first = run.Kill(bodies[3]);
        Assert.True(first is { First: true, Last: false });
        Assert.Null(run.Kill(bodies[3]));
        Assert.True(run.Received && !run.Complete);
        foreach (int i in new[] { 1, 2, 4 }) Assert.True(run.Kill(bodies[i]) is { First: false, Last: false });
        Assert.False(run.Complete);
        Assert.True(run.Kill(bodies[0]) is { Last: true, Beast.Red: true });
        Assert.True(run.Received && run.Complete && run.Captured == 5);
        Assert.False(run.TryArrive());
        AssertEffectBodies();
    }

    /// <summary>The four ServerEffect bodies as the official server sent them: the flag and property count, the properties,
    /// the Positioned cell and rotation, the flags word and height group, the Animated byte, the ao path as UTF-16 with its
    /// character count, and the leave effect's 13-byte tail.</summary>
    static void AssertEffectBodies()
    {
        Assert.Equal(Convert.FromHexString("0200410100008e0200000000003f0000000001570000004d0065007400610064006100740061002f0045006600660065006300740073002f005300700065006c006c0073002f006d006f006e00730074006500720073005f0065006600660065006300740073002f004c00650061006700750065005f00420065007300740069006100720079002f00450069006e006800610072002f007400720061006e0073006900740069006f006e002f006400690073006100700070006500610072002e0061006f0000"),
            BestiaryEffects.ArriveBody((321, 654), 0.5));
        Assert.Equal(Convert.FromHexString("02023665401400010107000000a475c34a00010a01410100008e0200000000003f0000000001570000004d0065007400610064006100740061002f0045006600660065006300740073002f005300700065006c006c0073002f006d006f006e00730074006500720073005f0065006600660065006300740073002f004c00650061006700750065005f00420065007300740069006100720079002f00450069006e006800610072002f007400720061006e0073006900740069006f006e002f006400690073006100700070006500610072002e0061006f0000"),
            BestiaryEffects.TeleportBody((321, 654), 0.5, 7));
        Assert.Equal(Convert.FromHexString("0200410100008e0200000000003f000004000000000000000000016e0000004d0065007400610064006100740061002f0045006600660065006300740073002f005300700065006c006c0073002f006d006f006e00730074006500720073005f0065006600660065006300740073002f004c00650061006700750065005f00420065007300740069006100720079002f00450069006e006800610072002f0074007200610069006c00650072002f0066006900720065005f0068006100720070006f006f006e005f00670075006e002f0066006900720065005f00670075006e005f006e006f005f0070006f007200740061006c002e0061006f0000"),
            BestiaryEffects.HarpoonBody((321, 654), 0.5));
        Assert.Equal(Convert.FromHexString("02023665401400010107000000a475c34a00010a01410100008e0200000000000000000400000000000000008009570000004d0065007400610064006100740061002f0045006600660065006300740073002f005300700065006c006c0073002f006d006f006e00730074006500720073005f0065006600660065006300740073002f004c00650061006700750065005f00420065007300740069006100720079002f00450069006e006800610072002f007400720061006e0073006900740069006f006e002f006400690073006100700070006500610072002e0061006f0001000000000000000000803f00"),
            BestiaryEffects.LeaveBody((321, 654), 7));
    }
}
