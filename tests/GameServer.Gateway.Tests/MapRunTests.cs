using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>Map affixes, scarabs/fragments and MapDeviceRecipes (21-hideouts.md §5).</summary>
public sealed class MapRunTests
{
    static readonly Lazy<(ModCatalog Mods, MapDevice Device, IReadOnlyDictionary<int, uint> Hashes)> Loaded = new(() =>
    {
        var hashes = new Dictionary<int, uint>();
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine("data", "tables", "BaseItemTypes.json"))))
        {
            int row = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
                hashes[row++] = item.GetProperty("HASH32").GetUInt32();
        }
        var mods = new ModCatalog("data");
        return (mods, MapDevice.Load("data", row => hashes.TryGetValue(row, out uint hash) ? hash : null), hashes);
    });

    static ModCatalog Mods => Loaded.Value.Mods;
    static MapDevice Device => Loaded.Value.Device;
    static IReadOnlyDictionary<int, uint> Hashes => Loaded.Value.Hashes;

    static MapDevice.Slot Of(int row, string cls, string name, IReadOnlyList<ItemMod>? mods = null, byte quality = 0) =>
        new(Hashes[row], cls, name, row, mods ?? [], quality);

    [Fact]
    public void PackDropAndChanceStatsMatchByNameNotByScarabId()
    {
        var mods = new MapMods(new Dictionary<string, int>
        {
            ["map_12_magic_packs_drop_cards_from_area"] = 8,
            ["map_divination_card_drop_chance_+%"] = 40,
            ["map_scarab_drop_chance_+%"] = 15,
            ["map_currency_drop_chance_+%"] = 20,
            ["map_additional_rare_pack"] = 2,
            ["map_additional_magic_pack"] = 1,
        });
        Assert.Equal(8, mods.PacksDropCards);
        Assert.Equal(9, mods.ExtraMagicPacks);
        Assert.Equal(40, mods.CardDropChance);
        Assert.Equal(15, mods.ScarabDropChance);
        Assert.Equal(20, mods.CurrencyDropChance);
        Assert.Equal(2, mods.ExtraRarePacks);
    }

    [Fact]
    public void AMapsAffixesQualityAndFragmentsBecomeTheOpeningMods()
    {
        var mods = new MapMods(new Dictionary<string, int>
        {
            ["map_item_drop_quantity_+%"] = 50,
            ["map_item_drop_rarity_+%"] = 80,
            ["map_pack_size_+%"] = 20,
            ["map_monsters_life_+%"] = 25,
            ["map_monsters_damage_+%"] = 10,
        }, quality: 20);
        Assert.Equal(1.5, mods.QuantityFactor);
        Assert.Equal(80, mods.Rarity);
        Assert.Equal(1.2, mods.PackSizeFactor);
        Assert.Equal(25, mods.MonsterEffectStats()["maximum_life_+%"]);
        Assert.Equal(10, mods.MonsterEffectStats()["damage_+%"]);

        var hordes = Mods.ById("MapLargePacks")!;
        var opening = Device.Check(
            [
                Of(2630, MapDevice.MapClass, "Beach Map", [new ItemMod(ModSection.Explicit, hordes.Hash, [20, 20])], 20),
                Of(2994, MapDevice.FragmentClass, "Sacrifice at Midnight"),
            ],
            null, Mods, out string why);
        Assert.True(opening is not null, why);
        Assert.Equal(45, opening!.Mods.Quantity);
        Assert.Equal(20, opening.Mods.Get("map_pack_size_+%"));
        Assert.Equal(1.2, opening.Mods.PackSizeFactor);
    }

    [Fact]
    public void ADivinationScarabOfPlentyAddsMagicPacksThatDropCards()
    {
        var opening = Device.Check(
            [
                Of(2630, MapDevice.MapClass, "Beach Map"),
                Of(3721, MapDevice.FragmentClass, "Divination Scarab of Plenty"),
            ],
            null, Mods, out string why);
        Assert.True(opening is not null, why);
        Assert.True(opening!.Mods.PacksDropCards >= 6, opening.Mods.ToString());
        Assert.True(opening.Mods.ExtraMagicPacks >= 6);
    }

    [Fact]
    public void DeviceRecipesOpenBossAreasAndRefuseASecondScarabOfTheSameFamily()
    {
        var writ = Device.Check([Of(4177, MapDevice.FragmentClass, "The Maven's Writ")], null, Mods, out string writWhy);
        Assert.True(writ is not null, writWhy);
        Assert.Equal("MavenBoss", writ!.Area.Id);
        Assert.Equal(84, writ.Area.Level);

        var formed = Device.Check([Of(4125, MapDevice.MiscClass, "Maven's Invitation: The Formed")], null, Mods, out string formedWhy);
        Assert.True(formed is not null, formedWhy);
        Assert.Equal("MavenHub", formed!.Area.Id);

        var atziri = Device.Check(
            [
                Of(2994, MapDevice.FragmentClass, "Sacrifice at Midnight"),
                Of(2995, MapDevice.FragmentClass, "Sacrifice at Dawn"),
                Of(2996, MapDevice.FragmentClass, "Sacrifice at Noon"),
                Of(2997, MapDevice.FragmentClass, "Sacrifice at Dusk"),
            ],
            null, Mods, out string atziriWhy);
        Assert.True(atziri is not null, atziriWhy);
        Assert.Equal("MapAtziri1", atziri!.Area.Id);

        var shaper = Device.Check(
            [
                Of(3009, MapDevice.FragmentClass, "Fragment of the Phoenix"),
                Of(3010, MapDevice.FragmentClass, "Fragment of the Minotaur"),
                Of(3011, MapDevice.FragmentClass, "Fragment of the Chimera"),
                Of(3012, MapDevice.FragmentClass, "Fragment of the Hydra"),
            ],
            null, Mods, out string shaperWhy);
        Assert.True(shaper is not null, shaperWhy);
        Assert.Equal("MapWorldsShapersRealm", shaper!.Area.Id);

        var uber = Device.Check(
            [
                Of(4179, MapDevice.FragmentClass, "Cosmic Fragment"),
                Of(4179, MapDevice.FragmentClass, "Cosmic Fragment"),
                Of(4179, MapDevice.FragmentClass, "Cosmic Fragment"),
                Of(4179, MapDevice.FragmentClass, "Cosmic Fragment"),
            ],
            null, Mods, out string uberWhy);
        Assert.True(uber is not null, uberWhy);
        Assert.Equal("MapWorldsShapersRealm", uber!.Area.Id);
        Assert.Equal(85, uber.Area.Level);
        Assert.Equal(-70, uber.Mods.Get("map_monster_damage_taken_+%_final_from_atlas_keystone"));

        var refused = Device.Check(
            [
                Of(2630, MapDevice.MapClass, "Beach Map"),
                Of(3659, MapDevice.FragmentClass, "Rusted Breach Scarab"),
                Of(3659, MapDevice.FragmentClass, "Rusted Breach Scarab"),
            ],
            null, Mods, out string why);
        Assert.Null(refused);
        Assert.Contains("fragment", why, StringComparison.OrdinalIgnoreCase);
    }
}
