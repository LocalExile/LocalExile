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

    [Fact]
    public void Types_without_a_mods_component_get_no_mods()
    {
        var scroll = Catalog.Value.Find("Metadata/Items/Currency/CurrencyIdentification")!;
        Assert.Empty(ItemCatalog.Create(scroll).Mods);
    }

    [Fact]
    public void A_mod_gets_exactly_one_value_per_stat()
    {
        var mods = Catalog.Value.Mods;
        var life = mods.ById("IncreasedLife1")!;
        Assert.Single(life.Stats);
        Assert.Equal([7], life.With(ModSection.Explicit, [7, 99], Random.Shared).Values);
        Assert.InRange(life.With(ModSection.Explicit, [], Random.Shared).Values.Single(), life.Stats[0].Min, life.Stats[0].Max);
        Assert.Equal(ModSection.Explicit, life.DefaultSection);
    }

    [Fact]
    public void Rarity_identification_and_mods_survive_a_save()
    {
        var mods = Catalog.Value.Mods;
        var item = ItemCatalog.Create(Catalog.Value.Row(384)!) is var w
            ? w with { Rarity = ItemRarity.Rare, Identified = false, Mods = [.. w.Mods, mods.ById("IncreasedLife1")!.With(ModSection.Explicit, [9], Random.Shared)] }
            : null!;

        var saved = CharacterSaves.ToSaved(item, mods);
        Assert.Equal(["SpellDamageOnWeaponImplicitWand1", "IncreasedLife1"], saved.Mods!.Select(m => m.Id));
        var back = CharacterSaves.FromSaved(saved, Catalog.Value)!;

        Assert.Equal(ItemRarity.Rare, back.Rarity);
        Assert.False(back.Identified);
        Assert.Equal(item.Mods.Select(m => (m.Section, m.Hash, string.Join(",", m.Values))),
            back.Mods.Select(m => (m.Section, m.Hash, string.Join(",", m.Values))));
    }

    [Fact]
    public void Saves_from_before_mods_get_the_types_implicits_and_unknown_mods_stop_the_load()
    {
        var old = new CharacterSaves.SavedItem(Catalog.Value.Row(384)!.Hash);
        Assert.Single(CharacterSaves.FromSaved(old, Catalog.Value).Mods);

        // Dropping the mod would lose it at the next save: the load stops instead.
        var unknown = old with { Mods = [new("NoSuchMod", ModSection.Explicit, [1])] };
        Assert.Throws<UnknownSavedItemException>(() => CharacterSaves.FromSaved(unknown, Catalog.Value));
    }
}
