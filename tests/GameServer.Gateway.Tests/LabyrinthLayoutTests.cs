using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;

namespace GameServer.Gateway.Tests;

public sealed class LabyrinthLayoutTests
{
    // The client's own layout for seed 0, Normal (scripts/re/frida/lab-layout.py): its rooms, connections (-1: out) and
    // secrets with their numbers; the port matched all 168 dumped (42 seeds × 4 tiers, lg.ps1 lablayout).
    [Fact]
    public void TheDaysLayoutMatchesTheClients()
    {
        static byte[]? Read(string path) => GamePack.Default.TryRead(path);
        var layout = LabyrinthLayout.Build(Read, 0, 1);
        var sections = new RawDat(Read("Data/LabyrinthSection.datc64")!);
        var secrets = new RawDat(Read("Data/LabyrinthSecrets.datc64")!);
        string Secret(int row, int number) => row < 0 ? "-" : $"{secrets.String(row, 0)}:{number}";
        var got = layout.Nodes.Select(n =>
            $"{sections.String(n.Section, 0)}#{n.Place} [{string.Join(",", n.Links)}] {Secret(n.Secret1, n.Number1)} {Secret(n.Secret2, n.Number2)}");
        Assert.Equal(
        [
            "Entry_1_Simple#1 [-1,1] Trinket:37 Gauntlet:52",
            "Entry_1_Simple#2 [0,2] ToggleStatuesNormal1:45 -",
            "Middle_1_SimpleS#1 [1,4,3] Trinket:38 Hidden:53",
            "Middle_1_SimpleS#2 [2,4] Hidden:54 Payload:55",
            "Middle_1_SimpleS#3 [3,2,5] Lieutenants1:48 ToggleStatuesNormal2:45",
            "End_1_BranchN#1 [4,6,7] Hidden:57 -",
            "End_1_BranchN#2 [5,7] SilverKey:39 SilverDoorReward:40",
            "End_1_BranchN#3 [5,6,9,8] Puzzle:51 Hidden:56",
            "End_1_BranchN#4 [7,9] SilverKey:42 SilverDoorReward:43",
            "End_1_BranchN#5 [7,8,-1] ToggleStatuesNormal2:45 Lieutenants2:48",
        ], got);
        // The node the client reads from the area's extra data (0x141ec7220 flag 0x08): seed, row, difficulty.
        Assert.Equal(new byte[] { 0x08, 0, 0, 0, 0, 0, 0x34, 0x12, 1 }, LabyrinthDay.ExtraData(new(0, 0x1234, 1)));
    }

    // C→S 0x39 is taken only in the Ascendancy Chamber's room: every tier's FinalBoss areas, some with a "_#", and no other.
    [Theory]
    [InlineData("1_Labyrinth_boss_2_end", true)]
    [InlineData("1_Labyrinth_boss_3_end_#", true)]
    [InlineData("EndGame_Labyrinth_boss_1_end", true)]
    [InlineData("1_Labyrinth_boss_2", false)]
    [InlineData("1_Labyrinth_EH_end_bottleneck", false)]
    [InlineData("1_3_town", false)]
    public void OnlyTheLastRoomsAreTheChamber(string area, bool chamber) => Assert.Equal(chamber, LabyrinthDay.IsChamber(area));

    // Izaro's three Aspirant's Trials of a Normal day (each section's last room) share his life bar: each starts where
    // the one before left him, the last ends at 0 in the Ascendancy Chamber's area; winning one takes his icon off the
    // run's map (0x1cb bit = the section's number).
    [Fact]
    public void IzarosTrialsShareHisLife()
    {
        var trials = IzaroFights.Trials(20764, 1).ToList();
        Assert.Equal(["1_Labyrinth_boss_1", "1_Labyrinth_boss_2", "1_Labyrinth_boss_2_end"], trials.Select(t => t.Area));
        Assert.Equal([(100, 66), (66, 33), (33, 0)], trials.Select(t => (t.Trial.StartPercent, t.Trial.LeavesAtPercent)));
        Assert.True(trials[^1].Trial.Last && LabyrinthDay.IsChamber(trials[^1].Area!));
        Assert.Single(trials.Select(t => t.Trial.Variety).Distinct());
        Assert.Equal(0b0000_0100, LabyrinthDay.Progress([], [2])[2]);
    }
}
