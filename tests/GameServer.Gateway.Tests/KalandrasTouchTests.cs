using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class KalandrasTouchTests
{
    [Fact]
    public void Kalandras_touch_counts_the_opposite_rings_mods_again_in_either_slot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kalandra-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        var items = Program.Items;
        var player = new Player(new(1, "kalandra", 100, 100, 100, 100, null, []), 1, 1) { Character = { Name = "KalandraStats" } };
        try
        {
            var touch = new UniqueCatalog(items).Create("Kalandra's Touch", new Random(3))!;
            Assert.True(Game.ReflectsOtherRing(touch, items.Mods));
            var ring = ItemCatalog.Create(items.Find("Metadata/Items/Rings/Ring5")!) with
            {
                Rarity = ItemRarity.Magic,
                Mods = [new(ModSection.Explicit, items.Mods.ById("FireResist1")!.Hash, [10]),
                    new(ModSection.Explicit, items.Mods.ById("DexterityUnique__3")!.Hash, [40])],
            };
            uint fire = Stat.BaseFireDamageResistancePct;
            uint dex = Stat.AdditionalDexterity;
            uint id = 200;
            (int Fire, int Dex) Worn(params (byte Row, Item Item)[] worn)
            {
                // New item ids each time: the stat source cache compares worn items, not their rows.
                var stats = Game.EquipmentStatSources(player, [.. worn.Select(w => (w.Row, new InventoryItem(id++, 0, 0, w.Item)))],
                    [], 100).Sources;
                return (stats.GetValueOrDefault(fire), stats.GetValueOrDefault(dex));
            }
            Assert.Equal((10, 40), Worn((6, ring)));
            Assert.Equal((20, 80), Worn((6, ring), (7, touch)));
            Assert.Equal((20, 80), Worn((6, touch), (7, ring)));
            // Nothing to reflect: another Kalandra's Touch, an empty opposite slot, or the third ring slot (no opposite).
            Assert.Equal((0, 0), Worn((6, touch), (7, touch with { })));
            Assert.Equal((0, 0), Worn((7, touch)));
            Assert.Equal((10, 40), Worn((6, ring), (102, touch)));
        }
        finally { PlayerInventories.Forget(player.Character.Name!); TestEnvironment.ReleaseSaves(path); }
    }
}
