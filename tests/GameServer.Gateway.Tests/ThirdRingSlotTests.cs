using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>
/// The Unseen Hand's stat (enable_ring_slot_3) shows Ring3, Inventories.dat row 102. A ring there must count like
/// Ring1/Ring2: it is an equipment row, so its mods reach the character's stats and its gem requirements.
/// </summary>
public sealed class ThirdRingSlotTests
{
    [Fact]
    public void The_third_ring_slot_counts_its_mods()
    {
        Assert.True(PlayerInventories.Accepts(102, "Ring"));
        Assert.Contains((byte)102, ItemModStats.EquipmentRows);

        var ring = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Rings/Ring5")!) with
        {
            Rarity = ItemRarity.Unique,
            Mods = [Program.Items.Mods.ById("DexterityUnique__3")!.With(ModSection.Explicit, [50], Random.Shared)],
        };
        var inventories = new PlayerInventories();
        inventories.Put(102, new InventoryItem(inventories.NewItemId(), 0, 0, ring));

        var equipment = inventories.EquipmentSnapshot();
        Assert.Contains(equipment, e => e.Row == 102);
        Assert.Equal(50, ItemModStats.Sum(equipment.Select(e => e.Item.Item), Program.Items.Mods)["additional_dexterity"]);
    }
}
