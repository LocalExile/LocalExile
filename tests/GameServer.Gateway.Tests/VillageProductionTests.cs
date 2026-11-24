using GameServer.Modules.Village;

namespace GameServer.Gateway.Tests;

/// <summary>
/// Kingsmarch's mining and smelting lines: the rate the client's own formula gives (3600 × the crew's summed job-level
/// efficiency ÷ BaseProductionTime, fact F-111), the counters the settlement carries, and the ore a claim puts into the
/// town. The numbers are the shipped tables': Crimson Iron takes 120 seconds a unit and a rank-1 worker's efficiency is
/// 18, so one miner makes 540 units an hour.
/// </summary>
public sealed class VillageProductionTests
{
    static VillageModule.Settlement Town(int jobRow, byte skill, params (int Row, uint Amount)[] resources)
    {
        var settlement = new VillageModule.Settlement();
        settlement.Assignments[1] = (byte)jobRow;
        settlement.Ranks[1] = skill;
        foreach (var (row, amount) in resources) settlement.Resources[row] = amount;
        return settlement;
    }

    [Fact]
    public void MiningAndSmeltingRunAtTheClientsRate()
    {
        VillageProduction.LoadGameData();

        // One rank-1 miner on "Mining Crimson Iron" (VillageJobs row 1, the production row that makes IronOre from
        // PendingIronOre in 120 seconds): 3600 × 18 ÷ 120 = 540 units an hour, and a rank-5 miner (efficiency 63) 1890.
        var mining = Town(1, 1, (0, 1000));
        var hour = VillageProduction.Advance(mining, 3600);
        Assert.Equal(540, hour.Units);
        Assert.Equal(460u, mining.Resources[0]);
        Assert.Equal(540u, mining.Resources[5]);

        var veteran = Town(1, 5, (0, 4000));
        Assert.Equal(1890, VillageProduction.Advance(veteran, 3600).Units);

        // The unit in progress is the production counter, in hundredths of the unit's own BaseProductionTime: six
        // seconds of a 120-second unit that a rank-1 miner takes 6.67 seconds of is 0.9 units, so 100 × 120 × 0.9.
        var half = Town(1, 1, (0, 100));
        Assert.Equal(0u, VillageProduction.Counter(half, 4));
        VillageProduction.Advance(half, 6);
        Assert.InRange(VillageProduction.Counter(half, 4), 10799u, 10800u);

        // Smelting eats the ore the miners made: VillageJobs row 6 (Smelting Crimson Iron) makes RefinedIron (15) out of
        // IronOre (5) at the same 120 seconds, so a rank-1 smith turns 100 ore into 45 bars in five minutes.
        var smelting = Town(6, 1, (5, 100));
        Assert.Equal(45, VillageProduction.Advance(smelting, 300).Units);
        Assert.Equal(55u, smelting.Resources[5]);
        Assert.Equal(45u, smelting.Resources[15]);
    }

    [Fact]
    public void AClaimMarksOreTheMinersThenWork()
    {
        VillageProduction.LoadGameData();

        // The rank of the ore a map's deposit was worth picks its "Ore Marked" row (1..5 -> 0..4), and a crew whose own
        // ore has nothing marked works another one the town holds instead (F-112): a Crimson Iron miner with only
        // Bismuth marked mines Bismuth.
        var settlement = Town(1, 1);
        Assert.Equal(10u, VillageProduction.AddPendingOre(settlement, 4, 10));
        Assert.Equal(0u, VillageProduction.Resource(settlement, 0));
        Assert.Equal(10u, VillageProduction.Resource(settlement, 3));

        var moved = VillageProduction.Advance(settlement, 3600);
        Assert.Equal(10, moved.Units);
        Assert.Equal(0u, settlement.Resources[3]);
        Assert.Equal(10u, settlement.Resources[8]);
    }
}