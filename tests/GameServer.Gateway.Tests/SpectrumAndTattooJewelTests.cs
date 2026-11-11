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
        Assert.Equal(42, sources[Program.Stats.Key("base_resist_all_elements_%")!.Value]);
        Assert.Equal(3, sources[Program.Stats.Key("base_minimum_power_charges")!.Value]);
    }

    [Fact]
    public void Warriors_tale_doubles_only_active_tattoos_in_the_allocated_sockets_radius()
    {
        var path = Path.Combine(Path.GetTempPath(), $"warriors-tale-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        var conn = new ClientState(new(1, "tattoos", 100, 100, 100, 100, null, []), 1, 1)
            { Character = "TattooRadius" };
        try
        {
            var tree = Game.PassivesFor(conn);
            var socket = tree.JewelNodeForSlot(1)!.Value;
            var jewel = new UniqueCatalog(Program.Items).Create("Warrior's Tale", new Random(2))!;
            var radius = RadiusJewelCatalogue.ReadConversion(jewel, Program.Items.Mods)!.Radius;
            var inside = tree.NodesInRadius(socket, radius);
            bool Strength(ushort n) => tree.Find(n)!.Stats.Any(s => Program.Stats.Name(s.Key) == "base_strength" && s.Value == 10);
            var near = inside.First(Strength);
            var far = tree.NodesInRadius(socket, 20000).First(n => !inside.Contains(n) && Strength(n));
            conn.Tattoos[near] = conn.Tattoos[far] = "Tattoo of the Ngamahu Firewalker";
            conn.TattooRevision++;
            uint fire = Program.Stats.Key("base_fire_damage_resistance_%")!.Value;
            int Resist(params ushort[] nodes) => Game.EquipmentStatSources(conn, [], nodes, 100).Sources.GetValueOrDefault(fire);
            var initial = Resist(near, far, socket);
            Game.ItemsOf(conn).Put(PlayerInventories.PassiveJewels, new(12, 1, 0, jewel));
            Assert.Equal(initial + 6, Resist(near, far, socket));
            Assert.Equal(initial, Resist(near, far));
            Game.ItemsOf(conn).Remove(PlayerInventories.PassiveJewels, 12);
            Assert.Equal(initial, Resist(near, far, socket));
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
