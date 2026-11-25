using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class SpectrumAndTattooJewelTests
{
    [Fact]
    public void Grand_spectra_roll_one_variant_and_count_active_copies_across_bases()
    {
        var items = Program.Items;
        var uniques = new UniqueCatalog(items);
        var random = new Random(18);
        var variants = new Dictionary<string, Item>();
        foreach (var definition in uniques.All.Where(d => d.Name == "Grand Spectrum"))
        {
            var hashes = new HashSet<ushort>();
            for (var roll = 0; roll < 48; roll++)
            {
                var item = Assert.IsType<Item>(uniques.Create(definition, random));
                var mod = Assert.Single(item.Mods);
                hashes.Add(mod.Hash);
                variants[items.Mods.ByHash(mod.Hash)!.Id] = item;
                Assert.True(GrandSpectrum.IsSpectrum(item, items.Mods));
            }
            Assert.Equal(3, hashes.Count);
        }
        Assert.Equal(9, variants.Count);
        var red = variants["AllResistancePerStackableJewelUnique__1"];
        var blue = variants["MinimumPowerChargesPerStackableJewelUnique__1"];
        var sources = ItemModStats.Global([red, red, blue], items.Mods, Program.Stats);
        GrandSpectrum.Apply(sources, Program.Stats);
        Assert.Equal(42, sources[Stat.BaseResistAllElementsPct]);
        Assert.Equal(3, sources[Stat.BaseMinimumPowerCharges]);
    }

    [Fact]
    public void Warriors_tale_doubles_only_active_tattoos_in_the_allocated_sockets_radius()
    {
        var path = Path.Combine(Path.GetTempPath(), $"warriors-tale-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        var player = new Player(new(1, "tattoos", 100, 100, 100, 100, null, []), 1, 1)
            { Character = { Name = "TattooRadius" } };
        try
        {
            FoulbornDreamsKeepTheAllocatedResistance(player);
            var tree = Game.PassivesFor(player);
            var socket = tree.JewelNodeForSlot(1)!.Value;
            var jewel = new UniqueCatalog(Program.Items).Create("Warrior's Tale", new Random(2))!;
            var radius = RadiusJewelCatalogue.ReadConversion(jewel, Program.Items.Mods)!.Radius;
            var inside = tree.NodesInRadius(socket, radius);
            bool Strength(ushort n) => tree.Find(n)!.Stats.Any(s => Program.Stats.Name(s.Key) == "base_strength" && s.Value == 10);
            var near = inside.First(Strength);
            var far = tree.NodesInRadius(socket, 20000).First(n => !inside.Contains(n) && Strength(n));
            player.Character.Tattoos[near] = player.Character.Tattoos[far] = "Tattoo of the Ngamahu Firewalker";
            player.Character.TattooRevision++;
            uint fire = Stat.BaseFireDamageResistancePct;
            int Resist(params ushort[] nodes) => Game.EquipmentStatSources(player, [], nodes, 100).Sources.GetValueOrDefault(fire);
            var initial = Resist(near, far, socket);
            Game.ItemsOf(player).Put(PlayerInventories.PassiveJewels, new(12, 1, 0, jewel));
            Assert.Equal(initial + 6, Resist(near, far, socket));
            Assert.Equal(initial, Resist(near, far));
            Game.ItemsOf(player).Remove(PlayerInventories.PassiveJewels, 12);
            Assert.Equal(initial, Resist(near, far, socket));

            // The +10 Dexterity passive 36287 is within both radii on the 3.29.3c tree.
            byte Slot(ushort node) => (byte)Enumerable.Range(0, 60).Single(i => tree.JewelNodeForSlot(i) == node);
            var meek = new UniqueCatalog(Program.Items).Create("Might of the Meek", new Random(3))!;
            player.Character.Tattoos[36287] = "Tattoo of the Arohongui Moonwarden";
            player.Character.TattooRevision++;
            Game.ItemsOf(player).Put(PlayerInventories.PassiveJewels, new(12, Slot(32763), 0, jewel));
            Game.ItemsOf(player).Put(PlayerInventories.PassiveJewels, new(13, Slot(60735), 0, meek));
            uint cold = Stat.BaseColdDamageResistancePct;
            int Cold(params ushort[] nodes) => Game.EquipmentStatSources(player, [], nodes, 100).Sources.GetValueOrDefault(cold);
            Assert.Equal(15, Cold(32763, 60735, 36287) - Cold(32763, 60735));
        }
        finally
        {
            PlayerInventories.Forget(player.Character.Name!);
            TestEnvironment.ReleaseSaves(path);
        }
    }

    static void FoulbornDreamsKeepTheAllocatedResistance(Player player)
    {
        const ushort socket = 31683;
        var tree = Game.PassivesFor(player);
        byte slot = (byte)Enumerable.Range(0, 60).Single(i => tree.JewelNodeForSlot(i) == socket);
        var inside = tree.NodesInRadius(socket, RadiusJewelCatalogue.EffectRadius(1500));
        var items = Game.ItemsOf(player);
        var results = new List<int>();
        foreach (var (name, mod, node, from, to, scale) in new[]
        {
            ("The Red Dream", "MutatedUniqueJewel85FireResistAlsoGrantsMaximumLifePercent", (ushort)33508,
                Stat.BaseFireDamageResistancePct, Stat.MaximumLifePlusPct, 50),
            ("The Green Dream", "MutatedUniqueJewel87ColdResistAlsoGrantsMaximumManaPercent", (ushort)36881,
                Stat.BaseColdDamageResistancePct, Stat.MaximumManaPlusPct, 75),
            ("The Blue Dream", "MutatedUniqueJewel89LightningResistAlsoGrantsMaximumESPercent", (ushort)35503,
                Stat.BaseLightningDamageResistancePct, Stat.MaximumEnergyShieldPlusPct, 75),
        })
        {
            Assert.Contains(node, inside);
            Assert.Contains(tree.Find(node)!.Stats, stat => stat.Key == from && stat.Value == 18);
            ushort far = tree.NodesInRadius(socket, 20000).First(n => !inside.Contains(n)
                && tree.Find(n)!.Stats.Any(stat => stat.Key == from && stat.Value > 0));
            var entry = RadiusJewelCatalogue.Find(name)!;
            var jewel = Assert.IsType<Item>(RadiusJewelCatalogue.Create(Program.Items, entry with { ConversionMod = mod }));
            uint hash = Program.Items.Mods.ById(mod)!.Hash;
            Assert.Equal(new[] { scale, 1500 }, Assert.Single(jewel.Mods, value => value.Hash == hash).Values);
            uint id = items.Add(PlayerInventories.PassiveJewels, slot, 0,
                jewel with { Mods = [.. jewel.Mods.Where(value => value.Hash != hash)] })!.Id;
            var plain = Measure(socket, node);
            var unallocated = Measure(socket);
            var outside = Measure(socket, far);
            var unattached = Measure(node);
            items.Change(PlayerInventories.PassiveJewels, id, _ => jewel);
            var modified = Measure(socket, node);
            Assert.Equal(plain.Resistance, modified.Resistance);
            Assert.Equal(unallocated, Measure(socket));
            Assert.Equal(outside, Measure(socket, far));
            Assert.Equal(unattached, Measure(node));
            int delta = modified.Resource - plain.Resource;
            Console.WriteLine($"{name} node {node}: resistance {modified.Resistance}, increased pool {plain.Resource} -> {modified.Resource}");
            results.Add(delta);
            items.Remove(PlayerInventories.PassiveJewels, id);

            (int Resistance, int Resource) Measure(params ushort[] allocated)
            {
                var stats = Game.EquipmentStatSources(player, [], allocated, 100).Sources;
                return (stats.GetValueOrDefault(from), stats.GetValueOrDefault(to));
            }
        }
        Assert.Equal(new[] { 9, 13, 13 }, results);
    }
}
