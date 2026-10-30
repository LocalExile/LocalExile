using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Tests;

// C→S 0xe1 (Ctrl + click to an affinity tab), 0xd3 (tab affinities) and 0x8a (Ctrl + click buy), read from the bytes the
// client sent live on 2026-10-20.
public class StashMessageTests
{
    [Fact]
    public void Ctrl_click_to_an_affinity_tab_names_the_item_and_the_tab()
    {
        var m = InstanceStash.ReadMoveToAffinityTab(Convert.FromHexString("00E1000000080000225E01000503"));

        Assert.Equal(8u, m.ItemId);
        Assert.Equal([((ushort)5, (byte)3)], m.Tabs);   // the Currency tab, affinity CurrencyItems
        Assert.Equal(0x5e22, m.Token);
        Assert.Equal(0x40000000u, InstanceStash.ReadMoveToAffinityTab(Convert.FromHexString("00E14000000000007C7201000503")).ItemId);
    }

}
