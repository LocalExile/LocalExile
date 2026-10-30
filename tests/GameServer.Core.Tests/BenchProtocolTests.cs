using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

public class BenchProtocolTests
{
    [Fact]
    public void Balance_inventory_is_separate_from_fixed_character_slots_and_regular_stash_tabs()
    {
        Assert.Equal("00C000FFFFFE00121AFFFF00000000", Convert.ToHexString(
            InstanceInventories.AddInventory(InstanceBench.BalanceInventoryId, InstanceBench.BalanceSlot)));
        Assert.DoesNotContain(InstanceInventories.PlayerDefaults, slot => slot.Kind == InstanceBench.BalanceSlot.Kind);
    }

    [Fact]
    public void Ctrl_click_reads_the_item_and_the_into_bench_flag()
    {
        // Captured live: Ctrl + click on an Iron Ring (item 561) in the main inventory with the bench open.
        Assert.Equal((561u, true), InstanceBench.ReadMove(Convert.FromHexString("00E90000023180")));
        Assert.Equal((561u, false), InstanceBench.ReadMove(Convert.FromHexString("00E90000023100")));
        Assert.Equal((561u, false), InstanceBench.ReadMove(Convert.FromHexString("00E90000023140")));
    }
}
