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
}
