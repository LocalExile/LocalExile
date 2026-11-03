using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// The one-modifier removal behind /item_mod remove.
public sealed class ItemModEditTests
{
    [Fact]
    public void Remove_takes_the_first_matching_mod_and_only_from_the_named_section()
    {
        var items = new ItemCatalog("data");
        var type = items.All.First(t => t.Name == "Iron Ring");
        var life = items.Mods.ById("IncreasedLife7")!;
        var fire = items.Mods.ById("FireResist1")!;
        var created = ItemCatalog.Create(type);
        var item = created with
        {
            Mods =
            [
                .. created.Mods,
                life.With(ModSection.Explicit, [80], new Random(1)),
                fire.With(ModSection.Explicit, [40], new Random(1)),
            ],
        };

        Assert.True(ItemModEdits.Contains(item, life.Hash));
        var removed = ItemModEdits.Remove(item, life.Hash);
        Assert.False(ItemModEdits.Contains(removed, life.Hash));
        Assert.True(ItemModEdits.Contains(removed, fire.Hash));
        Assert.Equal(item.Mods.Count - 1, removed.Mods.Count);

        // A section that does not hold the modifier leaves the item as it was.
        Assert.Same(item, ItemModEdits.Remove(item, life.Hash, ModSection.Implicit));
        Assert.False(ItemModEdits.Contains(item, life.Hash, ModSection.Implicit));
    }
}
