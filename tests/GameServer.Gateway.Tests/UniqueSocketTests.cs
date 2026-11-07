using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class UniqueSocketTests
{
    [Fact]
    public void Ordinary_uniques_get_initial_sockets_without_overwriting_special_layouts()
    {
        var items = new ItemCatalog("data");
        var uniques = new UniqueCatalog(items);
        foreach (string name in new[] { "Goldrim", "Lifesprig", "Wanderlust" })
        {
            var item = Assert.IsType<Item>(uniques.Create(name, new Random(17)));
            Assert.Equal(2, item.Sockets.Count);
            Assert.Equal(new byte[] { 2 }, item.Links);
            Assert.All(item.Sockets, socket => Assert.Null(socket.Gem));
        }
        // These layouts need their own implementation; the ordinary fallback must not invent two gem sockets.
        foreach (string name in new[] { "Kaom's Heart", "Actum", "Loreweave", "Tombfist", "Malachai's Artifice", "Mageblood" })
        {
            var item = Assert.IsType<Item>(uniques.Create(name, new Random(17)));
            Assert.Empty(item.Sockets);
            Assert.Null(item.Links);
        }
        // Skin of the Loyal is the six-linked random-colour layout, and in 3.29 it cannot roll a white socket.
        var loyal = Assert.IsType<Item>(uniques.Create("Skin of the Loyal", new Random(17)));
        Assert.Equal(6, loyal.Sockets.Count);
        Assert.Equal(new byte[] { 6 }, loyal.Links);
        Assert.All(loyal.Sockets, socket => Assert.NotEqual(SocketColour.White, socket.Colour));
        Assert.Equal(1, ItemModStats.Local(loyal, items.Mods).GetValueOrDefault("local_socketed_gem_level_+"));
        Assert.NotEqual(0, ItemModStats.Sum([loyal], items.Mods).GetValueOrDefault("global_defences_+%"));
        // Dialla's Malefaction has no fixed count: ordinary sockets, still never white.
        var dialla = Assert.IsType<Item>(uniques.Create("Dialla's Malefaction", new Random(17)));
        Assert.Equal(2, dialla.Sockets.Count);
        Assert.Equal(new byte[] { 2 }, dialla.Links);
        Assert.All(dialla.Sockets, socket => Assert.NotEqual(SocketColour.White, socket.Colour));
        foreach (var robe in new[] { loyal, dialla })
            Assert.Equal(1, ItemModStats.Local(robe, items.Mods).GetValueOrDefault("local_sockets_cannot_be_white"));
    }

    [Fact]
    public void Tabula_keeps_six_white_linked_sockets_through_identification_and_save_without_waiving_gem_requirements()
    {
        var items = new ItemCatalog("data");
        var uniques = new UniqueCatalog(items);
        var definition = Assert.IsType<UniqueCatalog.Definition>(uniques.Find("Tabula Rasa"));
        var robe = Assert.IsType<Item>(uniques.Create(definition with { ItemLevel = 9 }, new Random(17))) with { Identified = false };
        Assert.Equal(6, robe.Sockets.Count);
        Assert.All(robe.Sockets, socket => Assert.Equal(SocketColour.White, socket.Colour));
        Assert.Equal(new byte[] { 6 }, robe.Links);
        var wisdom = Assert.IsType<ItemCatalog.BaseType>(items.Find("Metadata/Items/Currency/CurrencyIdentification"));
        Assert.True(CurrencyEffects.TryAction(items, new ItemRolls(items), robe, wisdom, new Random(17), out var identified));
        robe = Assert.IsType<Item>(identified);
        Assert.True(robe.Identified);
        robe = Assert.IsType<Item>(CharacterSaves.FromSaved(CharacterSaves.ToSaved(robe, items.Mods), items));
        Assert.Equal(6, robe.Sockets.Count);
        Assert.All(robe.Sockets, socket => Assert.Equal(SocketColour.White, socket.Colour));
        Assert.Equal(new byte[] { 6 }, robe.Links);
        var type = Assert.IsType<ItemCatalog.BaseType>(items.ByHash(robe.TypeHash));
        Assert.Equal(1, ItemModStats.LevelRequirement(robe, type.DropLevel, items.Mods));
        Assert.Equal(0, ItemDefences.Calculate(robe, items.Defences(type)!.Value, items.Mods).EnergyShield);
        var local = ItemModStats.Local(robe, items.Mods);
        local["local_energy_shield"] = 100;
        local["local_energy_shield_+%"] = 200;
        Assert.Equal(0, ItemDefences.Of(items.Defences(type)!.Value, local, 20).EnergyShield);
        var own = ItemModStats.ApplyBaseRequirementOverrides(new(1, robe, 0, 0, 17, 1), items.Mods);
        Assert.Equal(0, own.Intelligence);
        Assert.Empty(ItemModStats.ResolveEquipment([own], (0, 0, 0), 1, items.Mods).Inactive);
        Assert.Empty(ItemModStats.ResolveEquipment([new(1, robe, 0, 0, 17, 1)], (0, 0, 0), 1, items.Mods).Inactive);
        // Gem requirements are merged after the clothing's own waiver, just as in Progression.WithGems.
        var withGemRequirements = own with { Intelligence = 100, Level = 10 };
        Assert.Contains(1u, ItemModStats.ResolveEquipment([withGemRequirements], (0, 0, 0), 10, items.Mods).Inactive);
        Assert.Contains(1u, ItemModStats.ResolveEquipment([withGemRequirements], (100, 100, 100), 9, items.Mods).Inactive);
        Assert.Empty(ItemModStats.ResolveEquipment([withGemRequirements], (100, 100, 100), 10, items.Mods).Inactive);
    }
}
