using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class AtlasMapAdmissionTests
{
    [Fact]
    public void A_generic_key_can_open_an_ordinary_layout_but_a_named_layout_requires_its_map()
    {
        using var f = new TriggeredSummonTests.Fixture();
        var items = Program.Items;
        var atlas = new AtlasCatalog(items);
        var device = MapDevice.Load("data", row => items.Row(row)?.Hash, items);
        string name = f.Player.Character.Name!;

        MapDevice.Slot Slot(ItemCatalog.BaseType item) =>
            new(item.Hash, item.Class, item.Name, item.Row, [], 0) { Rarity = ItemRarity.Rare, BaseId = item.Id };

        var ordinary = atlas.Find("MapWorldsStrand")!;
        var named = atlas.Find("MapWorldsAbomination")!;
        var generic = Slot(items.ByHash(atlas.KeyOfTier(16)!.Value)!);

        atlas.Select(name, ordinary);
        Assert.Equal(ordinary.Hash16, device.Check([generic], name, items.Mods, out _)?.Area.Hash16);

        atlas.Select(name, named);
        Assert.Null(device.Check([generic], name, items.Mods, out string why));
        Assert.Contains("requires its own map item", why);
    }
}
