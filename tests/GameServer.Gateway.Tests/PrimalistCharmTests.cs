using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class PrimalistCharmTests
{
    [Fact]
    public void Charm_socket_needs_its_descendancy_node_and_its_modifier_applies_then_leaves()
    {
        var path = Path.Combine(Path.GetTempPath(), $"charm-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        var player = new Player(new(1, "charms", 100, 100, 100, 100, null, []), 1, 1)
        {
            Character = { Name = "Primalist" + Guid.NewGuid().ToString("N") },
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000)
        };
        try
        {
            var tree = Game.PassivesFor(player);
            int cell = Enumerable.Range(0, 60).First(i => tree.CharmNodeForSlot(i) is not null);
            ushort node = tree.CharmNodeForSlot(cell)!.Value;
            Assert.True(tree.IsCharmSocket(node));

            var baseType = Program.Items.Find("Metadata/Items/AnimalCharms/StrAnimalCharm")!;
            var mod = Program.Items.Mods.ById("AnimalCharmMaximumFireDamageResistance1")!;
            var charm = ItemCatalog.Create(baseType) with
            {
                Rarity = ItemRarity.Magic,
                Mods = [new ItemMod(ModSection.Explicit, mod.Hash, [2])],
            };

            Assert.NotNull(Game.PassiveJewelRefusal(player, cell, 0, charm));
            player.Character.DescendancyRow = 3;
            player.Character.DescendancyNodes.Add(node);
            Assert.Null(Game.PassiveJewelRefusal(player, cell, 0, charm));

            uint fire = Stat.BaseMaximumFireDamageResistancePct;
            int before = Game.CharacterStatSources(player).Stats.GetValueOrDefault(fire);
            Game.ItemsOf(player).Put(PlayerInventories.PassiveJewels, new(90, (byte)cell, 0, charm));
            Assert.Equal(before + 2, Game.CharacterStatSources(player).Stats.GetValueOrDefault(fire));

            player.Character.DescendancyNodes.Clear();
            Game.NormalizePassiveJewels(player);
            Assert.Empty(Game.ItemsOf(player).Items(PlayerInventories.PassiveJewels));
            Assert.Single(Game.ItemsOf(player).Items(PlayerInventories.MainInventory), i => i.Item.TypeHash == charm.TypeHash);
            Assert.Equal(before, Game.CharacterStatSources(player).Stats.GetValueOrDefault(fire));
        }
        finally
        {
            PlayerInventories.Forget(player.Character.Name!);
            TestEnvironment.ReleaseSaves(path);
        }
    }

    [Fact]
    public void A_charm_rolls_magic_with_one_modifier()
    {
        var baseType = Program.Items.Find("Metadata/Items/AnimalCharms/DexAnimalCharm")!;
        var rolled = new ItemRolls(Program.Items).Roll(ItemCatalog.Create(baseType) with { ItemLevel = 100 },
            ItemRarity.Rare, new Random(7));
        Assert.NotNull(rolled);
        Assert.Equal(ItemRarity.Magic, rolled.Rarity);
        Assert.Single(rolled.Mods, m => m.Section == ModSection.Explicit);
    }
}
