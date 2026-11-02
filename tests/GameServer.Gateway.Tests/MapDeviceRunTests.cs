using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>
/// The map device's activation path end to end (21-hideouts.md §5): a map's affixes, quality and each scarab or
/// fragment's MapFragmentMods become the instance's mods; MapDeviceRecipes open their boss areas; a second scarab of the
/// same family is refused; the quality chisels and Vaal keep the tables' own rows; Maven invitations and their
/// witnesses resolve; and a recipe's phases hold its last boss while the voidstones unlock from those kills.
/// <para>
/// One test because AGENTS.md allows two per PR and these share a single load of the real tables.
/// </para>
/// </summary>
public sealed class MapDeviceRunTests
{
    static readonly Lazy<(ModCatalog Mods, ItemCatalog Items, MapDevice Device, IReadOnlyDictionary<int, uint> Hashes)> Loaded = new(() =>
    {
        var hashes = new Dictionary<int, uint>();
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine("data", "tables", "BaseItemTypes.json"))))
        {
            int row = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
                hashes[row++] = item.GetProperty("HASH32").GetUInt32();
        }
        var mods = new ModCatalog("data");
        return (mods, new ItemCatalog("data"),
            MapDevice.Load("data", row => hashes.TryGetValue(row, out uint hash) ? hash : null), hashes);
    });

    static ModCatalog Mods => Loaded.Value.Mods;
    static ItemCatalog Items => Loaded.Value.Items;
    static MapDevice Device => Loaded.Value.Device;
    static ItemRolls Rolls => new(Items);
    static MapBossFights Fights => MapBossFights.Current;

    static MapDevice.Slot Of(int row, string cls, string name, IReadOnlyList<ItemMod>? mods = null, byte quality = 0) =>
        new(Loaded.Value.Hashes[row], cls, name, row, mods ?? [], quality);

    [Fact]
    public void The_map_devices_activation_path_holds_end_to_end()
    {
        // The stat readers behind the opening: matched by name, not by a scarab id.
        var stats = new MapMods(new Dictionary<string, int>
        {
            ["map_12_magic_packs_drop_cards_from_area"] = 8,
            ["map_divination_card_drop_chance_+%"] = 40,
            ["map_scarab_drop_chance_+%"] = 15,
            ["map_currency_drop_chance_+%"] = 20,
            ["map_additional_rare_pack"] = 2,
            ["map_additional_magic_pack"] = 1,
            ["map_item_drop_quantity_+%"] = 50,
            ["map_item_drop_rarity_+%"] = 80,
            ["map_pack_size_+%"] = 20,
            ["map_monsters_life_+%"] = 25,
            ["map_monsters_damage_+%"] = 10,
        }, quality: 20);
        Assert.Equal(8, stats.PacksDropCards);
        Assert.Equal(9, stats.ExtraMagicPacks);
        Assert.Equal(40, stats.CardDropChance);
        Assert.Equal(15, stats.ScarabDropChance);
        Assert.Equal(20, stats.CurrencyDropChance);
        Assert.Equal(2, stats.ExtraRarePacks);
        Assert.Equal(1.5, stats.QuantityFactor);
        Assert.Equal(80, stats.Rarity);
        Assert.Equal(1.2, stats.PackSizeFactor);
        Assert.Equal(25, stats.MonsterEffectStats()["maximum_life_+%"]);
        Assert.Equal(10, stats.MonsterEffectStats()["damage_+%"]);

        // A map's affixes, quality and a fragment become the opening's mods.
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

        // A scarab's own mods reach the opening (Divination Scarab of Plenty: extra packs that drop cards).
        var plenty = Device.Check(
            [
                Of(2630, MapDevice.MapClass, "Beach Map"),
                Of(3721, MapDevice.FragmentClass, "Divination Scarab of Plenty"),
            ],
            null, Mods, out string plentyWhy);
        Assert.True(plenty is not null, plentyWhy);
        Assert.True(plenty!.Mods.PacksDropCards >= 6, plenty.Mods.ToString());
        Assert.True(plenty.Mods.ExtraMagicPacks >= 6);

        // Recipes open their boss areas, and a second scarab of the same family is refused.
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
        var uber = Device.Check(
            [
                Of(4179, MapDevice.FragmentClass, "Cosmic Fragment"),
                Of(4179, MapDevice.FragmentClass, "Cosmic Fragment"),
                Of(4179, MapDevice.FragmentClass, "Cosmic Fragment"),
                Of(4179, MapDevice.FragmentClass, "Cosmic Fragment"),
            ],
            null, Mods, out string uberWhy);
        Assert.True(uber is not null, uberWhy);
        Assert.Equal(-70, uber!.Mods.Get("map_monster_damage_taken_+%_final_from_atlas_keystone"));
        var refused = Device.Check(
            [
                Of(2630, MapDevice.MapClass, "Beach Map"),
                Of(3659, MapDevice.FragmentClass, "Rusted Breach Scarab"),
                Of(3659, MapDevice.FragmentClass, "Rusted Breach Scarab"),
            ],
            null, Mods, out string refusedWhy);
        Assert.Null(refused);
        Assert.Contains("fragment", refusedWhy, StringComparison.OrdinalIgnoreCase);

        // The map quality chisels use the tables' AlternateQualityTypes, and one sets it on the item.
        Assert.True(string.IsNullOrEmpty(Items.Error), Items.Error);
        var chisels = Items.All.Where(t => Items.CurrencyAction(t) == "add_map_alt_quality").ToList();
        Assert.True(chisels.Count >= 6, $"only {chisels.Count} add_map_alt_quality currencies");
        var mapBase = Items.All.FirstOrDefault(t => t.Supported && CurrencyEffects.IsMap(t));
        Assert.NotNull(mapBase);
        foreach (var chisel in chisels)
        {
            Assert.True(Items.AlternateQualityOf(chisel) is not null, chisel.Name);
            Assert.True(CurrencyEffects.HandlesAction(Items.CurrencyAction(chisel)), chisel.Name);
            Assert.True(CurrencyEffects.TryAction(Items, Rolls, ItemCatalog.Create(mapBase!), chisel, new Random(1), out var chiselled));
            Assert.Equal(1, chiselled!.Quality);
            Assert.Equal(Items.AlternateQualityOf(chisel), chiselled.AlternateQualityType);
        }

        // Vaal turns a white map rare and corrupted, with explicit mods.
        Assert.True(CurrencyEffects.TryAction(Items, Rolls,
            ItemCatalog.Create(mapBase!) with { ItemLevel = (byte)Math.Clamp(mapBase!.DropLevel, 1, 100) },
            Items.All.First(t => Items.CurrencyAction(t) is "corrupt_item"), new Random(1), out var vaal));
        Assert.NotNull(vaal);
        Assert.True(vaal!.Corrupted);
        Assert.Equal(ItemRarity.Rare, vaal.Rarity);
        Assert.Contains(vaal.Mods, m => m.Section == ModSection.Explicit);

        // Maven invitations: their witnesses are Maven-fight areas or Atlas map bosses.
        var invitation = Fights.Witnesses(4125);
        Assert.Equal(4, invitation.Count);
        Assert.Contains("Metadata/Monsters/AtlasBosses/HydraBoss", invitation);
        Assert.Contains("Metadata/Monsters/AtlasBosses/ChimeraBoss", invitation);
        Assert.Equal(3, Fights.InvitationOf(4131)!.Count);
        Assert.Equal(3, Fights.Witnesses(4131).Count);

        // A recipe's phases hold its last boss, and its voidstones unlock from those kills.
        Assert.Equal("Metadata/Monsters/MavenBoss/MavenBrainBoss", Fights.PhaseOf("MavenBoss")!.Last);
        Assert.Equal("Metadata/Monsters/Atziri/Atziri", Fights.PhaseOf("MapAtziri1")!.Last);
        var none = new HashSet<string>(StringComparer.Ordinal);
        Assert.Equal("Metadata/Items/AtlasUpgrades/AtlasUpgradePrimordial1",
            Fights.Resolve("Metadata/Monsters/MavenBoss/MavenBrainBoss", new HashSet<int>(), none).Ready!.ItemId);
        Assert.Equal(0, Fights.StoneByItem("Metadata/Items/AtlasUpgrades/AtlasUpgradePrimordial1")!.Slot);
        Assert.Equal(3, Fights.StoneByItem("Metadata/Items/AtlasUpgrades/AtlasUpgradePrimordial4")!.Slot);
        Assert.Null(Fights.StoneByItem("Metadata/Items/Maps/MapWorldsBeach"));
    }
}
