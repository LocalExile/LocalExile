using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ItemModsTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly Lazy<ItemCatalog> Catalog = new(() => new ItemCatalog(Data()));

    [Fact]
    public void A_new_driftwood_wand_has_its_spell_damage_implicit()
    {
        var wand = ItemCatalog.Create(Catalog.Value.Row(384)!);

        var mod = Assert.Single(wand.Mods);
        var row = Catalog.Value.Mods.ByHash(mod.Hash)!;
        Assert.Equal("SpellDamageOnWeaponImplicitWand1", row.Id);
        Assert.Equal(ModSection.Implicit, mod.Section);
        Assert.InRange(Assert.Single(mod.Values), 8, 12);
    }

}
