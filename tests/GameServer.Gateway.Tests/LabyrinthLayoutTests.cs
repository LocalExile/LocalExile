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

    // The official Eternal Labyrinth of 2026-11-07 (poelab.com's layout, its rooms' names and areacodes): the day's seed is
    // the day's number since 1970 (20764), and each room's area is the official server's pick (LabyrinthLayout.AreaPick).
    [Fact]
    public void TheDaysRoomsAreTheOfficialOnes() => Assert.Equal(
    [
        "OH_branch", "RT_straight", "DG_quad_door", "RT_end_bottleneck", "boss_1",
        "DG_branch_bottleneck_1_#", "EH_end_bottleneck", "P_end_bottleneck", "C_branch_bottleneck_1", "boss_1",
        "C_branch_bottleneck_1", "RT_branch_bottleneck_1", "C_end_bottleneck", "DG_branch_bottleneck_1_#", "EH_end_bottleneck", "boss_2_end",
    ], LabyrinthDay.For(20764, 4).Areas.Select(a => a?.Replace("EndGame_Labyrinth_", "")));

    // `GameServer.Gateway labyrinth 2026-11-07` (LabyrinthDay.Listing), the Eternal tier against poelab.com's map of that
    // day: Argus in room 2, the silver key in 3, gauntlets in 4, 6 (escort: Payload) and 7, the puzzle in 11, Darkshrines
    // (Hidden) in 1, 11 and 12, Izaro after 5, 10 and 16; the rooms where their map positions put them.
    [Fact]
    public void TheListingDrawsTheDaysMap()
    {
        var lines = LabyrinthDay.Listing(LabyrinthDay.SeedOf(new DateOnly(2026, 11, 7)), true).ToList();
        Assert.Equal("{\"type\":\"day\",\"seed\":20764,\"date\":\"2026-11-07\"}", lines[0].ToJsonString());
        var rooms = lines.Where(l => (string?)l["type"] == "room" && (int)l["tier"]! == 4).ToList();
        string Room(System.Text.Json.Nodes.JsonObject r) =>
            $"{r["slot"]}:{r["x"]},{r["y"]} {string.Join(" ", r["secrets"]!.AsArray().Select(s => (string?)s!["id"]))}".TrimEnd() +
            ((int)r["izaro"]! > 0 ? " Izaro" : "");
        Assert.Equal(
        [
            "0:0,1 Hidden", "0:1,0 SilverKey Argus", "0:2,1 GoldDoor SilverKey", "0:2.5,0.5 GoldKey Gauntlet",
            "0:3,1 ChargeDisruptorsEndgame1 Izaro",
            "1:0,1 Payload", "1:0.5,0 SilverKey Gauntlet", "1:2,2 SilverDoorReward", "1:1.5,1",
            "1:3,1 PowerPylonsEndgame1 ChargeDisruptorsEndgame2 Izaro",
            "2:0,1 Puzzle Hidden", "2:1,0 Hidden", "2:1.3,0.7 SilverDoorReward", "2:2,2", "2:1.7,1.3 SilverDoorReward",
            "2:3,1 ChargeDisruptorsEndgame2 PowerPylonsEndgame2 Izaro",
        ], rooms.Select(Room));
        Assert.Equal("[11,13,-1]", rooms[^1]["links"]!.ToJsonString());
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
}
