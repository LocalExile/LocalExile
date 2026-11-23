using GameServer.Core;
using GameServer.Modules.Betrayal;
using Xunit;
using static GameServer.Modules.Betrayal.BetrayalTables;

namespace GameServer.Gateway.Tests;

public class BetrayalTests
{
    static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    /// <summary>Which areas have Jun's mission, who leads it, where its pieces stand and how its objects are written:
    /// CoreLeagues Betrayal is a row the league chances never roll, so the module reads the area's base chance and the map's
    /// master-mission stats; the members are the board's Transportation and Intervention members; the creates are the
    /// official bodies.</summary>
    [Fact]
    public void Roll_follows_the_map_stats_and_the_mission_has_the_official_pieces()
    {
        Assert.True(LeagueChances.Current.LeagueOf(LeagueRow)?.NotRolled);
        Assert.Equal(0, LeagueChances.Current.Count("MapWorldsStrand", LeagueRow, new Random(1)));
        Assert.Equal(2, LeagueChances.Current.ChanceOf("MapWorldsStrand", LeagueRow).Chance);
        Assert.Equal(0, LeagueChances.Current.ChanceOf("MapWorldsStrand", LeagueRow, alternate: true).Chance);
        Assert.Equal(0, LeagueChances.Current.ChanceOf("MapAtziri1", LeagueRow).Chance);

        var empty = new Dictionary<uint, int>();
        var jun = new Dictionary<uint, int> { [Stat.MapMasterMissionJunPctChance] = 100 };
        var rng = new Random(1);
        Assert.InRange(Enumerable.Range(0, 1000).Count(_ => BetrayalPlan.Rolled("MapWorldsStrand", empty, isMap: true, rng)), 1, 60);
        var ruthless = new Random(1);
        Assert.Equal(0, Enumerable.Range(0, 200).Count(_ => BetrayalPlan.Rolled("MapWorldsStrand", empty, isMap: true, ruthless, hardMode: true)));
        Assert.True(BetrayalPlan.Rolled("MapWorldsStrand", jun, isMap: true, new Random(1)));
        Assert.True(BetrayalPlan.Rolled("MapWorldsStrand", jun, isMap: true, new Random(1), hardMode: true));
        Assert.True(BetrayalPlan.Rolled("MapWorldsStrand", new Dictionary<uint, int> { [Stat.MapMasterMissionJunPctChance] = 50,
            [Stat.MapMasterMissionJunChancePlusPct] = 100 }, isMap: true, new Random(1)));
        Assert.True(BetrayalPlan.Rolled("MapWorldsStrand", new Dictionary<uint, int> { [Stat.MapBetrayalLeague] = 1 }, isMap: true, new Random(1)));
        Assert.False(BetrayalPlan.Rolled("MapWorldsStrand", jun, isMap: false, new Random(1)));
        Assert.False(BetrayalPlan.Rolled("2_10_6", jun, isMap: false, new Random(1)));
        Assert.False(BetrayalPlan.Rolled("MapAtziri1", jun, isMap: true, new Random(1)));
        Assert.False(BetrayalPlan.Rolled("MapAtziri1", new Dictionary<uint, int> { [Stat.MapBetrayalLeague] = 1 }, isMap: true, new Random(1)));
        foreach (uint off in new[] { Stat.MapDisableMissions, Stat.AtlasKeystoneNoExtraContent, Stat.MapNukeEverything, Stat.MapAreaUnderlevelled })
            Assert.False(BetrayalPlan.Rolled("MapWorldsStrand", new Dictionary<uint, int>(jun) { [off] = 1 }, isMap: true, new Random(1)));
        Assert.False(BetrayalPlan.Rolled("MapWorldsStrand", new Dictionary<uint, int>(jun) { [Stat.MapDisableLeagues] = 1 << LeagueRow }, isMap: true, new Random(1)));
        Assert.Equal(0, Enumerable.Range(0, 200).Count(_ => BetrayalPlan.Rolled("MapWorldsStrand",
            new Dictionary<uint, int> { [Stat.MapExtraContentHasNoBaseChanceToSpawn] = 1 }, isMap: true, rng)));
        Assert.Equal(100, BetrayalPlan.ForcedChance("Anarchy=50,Betrayal=100"));
        Assert.True(BetrayalPlan.Rolled("2_10_6", empty, isMap: false, new Random(1), forced: 100));
        Assert.False(BetrayalPlan.Rolled("MapAtziri1", empty, isMap: true, new Random(1), forced: 100));

        // The leaders: the BetrayalTargets rows in the Transportation and Intervention divisions, each a monster the server writes.
        Assert.Equal(["Vagan:Transportation", "Vorici:Intervention", "Jorgin:Transportation", "Korell:Transportation", "Aisling:Intervention"],
            Members.Select(m => $"{m.Id}:{m.Job}"));
        Assert.All(Members, m => Assert.True(Game.Monsters.All[m.Variety].Supported));
        foreach (string guard in new[] { BetrayalPlan.GuardMelee1, BetrayalPlan.GuardMelee2, BetrayalPlan.GuardRanged })
            Assert.True(Game.Monsters.Find(guard)?.Supported);
        Assert.NotNull(AssassinationRules);

        // The formation on open ground: the three carts and all eleven guards; the six portals 41 to 73 cells from the member.
        static bool Open(int x, int y) => x is >= 0 and < 400 && y is >= 0 and < 400;
        var thane = Members.First(m => m.Id == "Jorgin");
        var escort = BetrayalPlan.Place(thane, (200, 200), Open, new Random(3))!;
        Assert.Equal((3, 11, 0, (200, 200)), (escort.Carts.Count, escort.Guards.Count, escort.Spawners.Count, escort.MemberCell));
        var intervention = BetrayalPlan.Place(Members.First(m => m.Job == Job.Intervention), (200, 200), Open, new Random(3))!;
        Assert.Equal<int>(BetrayalPlan.SpawnerTiers, intervention.Spawners.Select(s => s.Tier));
        Assert.All(intervention.Spawners, s => Assert.InRange(double.Hypot(s.Cell.X - 200, s.Cell.Y - 200), 40.5, 73.5));
        Assert.Empty(intervention.Carts);
        Assert.Null(BetrayalPlan.Place(thane, (200, 200), (_, _) => false, new Random(3)));

        // Jun asleep, a cart and the MakeChoice of member 0xb35 (BetrayalTargets row 12) as the official server created them:
        // the flag and property count, the properties (u32 key, 00 01 01, u32 value), Positioned (x, y, rotation, 4 bytes), the rest.
        Assert.Equal("0001" + "a5092ba2000101a3030000" + "c4020000" + "49020000" + "997bc040" + "00000000" + "400101010000",
            Hex(BetrayalWire.JunBody((708, 585), BitConverter.Int32BitsToSingle(0x40c07b99), flag: 0)));
        Assert.Equal("0000" + "8f020000" + "11020000" + "ba6d3c40" + "00000000" + "100000803f01" + "020000000000" + "0000",
            Hex(BetrayalWire.CartBody((655, 529), BitConverter.Int32BitsToSingle(0x403c6dba))));
        Assert.Equal("0201" + "00406d63000101350b0000" + "7c020000" + "39020000" + "e4cb9640" + "00000000" + "01" + "01000c00" + "0000",
            Hex(BetrayalWire.ChoiceBody((636, 569), BitConverter.Int32BitsToSingle(0x4096cbe4), 0xb35, 12)));
        // The assassination portal is awake at Transitionable 1; the entrance portal carries its ao as UTF-16 after its length.
        Assert.Equal((21, (byte)2, (byte)1), (BetrayalWire.SpawnerBody((1, 2)).Length, BetrayalWire.SpawnerBody((1, 2))[0], BetrayalWire.SpawnerBody((1, 2))[19]));
        byte[] portal = BetrayalWire.PortalBody((660, 569), 0, 0xb35);
        Assert.Equal(63 + 2 * BetrayalWire.EntrancePortalAo.Length, portal.Length);
        Assert.Equal((uint)BetrayalWire.EntrancePortalAo.Length, BitConverter.ToUInt32(portal, 46));
    }

    /// <summary>One instance's mission from its start to its end: each step happens once whoever reaches it, the Intervention's
    /// ruleset data follows the fight (a party member or a re-entry reads the same), and what follows a step comes on the
    /// area's frames, not on real time.</summary>
    [Fact]
    public void Mission_lifecycle_is_the_instances_and_runs_on_frames()
    {
        var spot = (200, 200);
        var assassin = Members.First(m => m.Job == Job.Intervention);
        var run = new BetrayalRun(BetrayalPlan.Place(assassin, spot, (_, _) => true, new Random(5))!, 70);
        Assert.Equal((BetrayalRun.Stage.Waiting, (byte?)0), (run.Current, run.RulesetPayload));
        Assert.False(run.Advance(BetrayalRun.Stage.Fight));
        Assert.True(run.Advance(BetrayalRun.Stage.Waiting));
        Assert.False(run.Advance(BetrayalRun.Stage.Waiting));
        Assert.Equal((BetrayalRun.Stage.Fight, (byte?)1), (run.Current, run.RulesetPayload));
        run.After(BetrayalRun.PortalFrames, BetrayalRun.Due.RemovePortal);
        Assert.Empty(run.Step((BetrayalRun.PortalFrames - 1) * BetrayalRun.FrameSeconds));
        Assert.Equal([BetrayalRun.Due.RemovePortal], run.Step(BetrayalRun.FrameSeconds));
        Assert.Empty(run.Step(10));
        Assert.True(run.Advance(BetrayalRun.Stage.Fight));
        Assert.Equal((BetrayalRun.Stage.Defeated, (byte?)0), (run.Current, run.RulesetPayload));
        Assert.True(run.Advance(BetrayalRun.Stage.Defeated));
        Assert.False(run.Advance(BetrayalRun.Stage.Defeated));
        run.After(BetrayalRun.LeaveFrames, BetrayalRun.Due.Leave);
        run.After(BetrayalRun.InterrogateFrames, BetrayalRun.Due.Interrogate);
        Assert.Equal([BetrayalRun.Due.Interrogate, BetrayalRun.Due.Leave], run.Step(BetrayalRun.LeaveFrames * BetrayalRun.FrameSeconds));
        Assert.True(run.Advance(BetrayalRun.Stage.Chosen));
        Assert.Equal((BetrayalRun.Stage.Done, (byte?)null), (run.Current, run.RulesetPayload));
        Assert.False(run.Advance(BetrayalRun.Stage.Done));

        var escort = new BetrayalRun(BetrayalPlan.Place(Members.First(m => m.Job == Job.Transportation), spot, (_, _) => true, new Random(5))!, 70);
        Assert.Null(escort.RulesetPayload);
        Assert.True(escort.Advance(BetrayalRun.Stage.Waiting));
        Assert.Null(escort.RulesetPayload);
    }
}
