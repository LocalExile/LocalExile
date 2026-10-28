using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class EssenceCraftingTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RepositoryRoot); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "data/tables/Essences.json"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables/Essences.json");
    }

    static readonly Lazy<(ItemCatalog Catalog, EssenceCrafting Essences, ItemRolls Rolls,
        IReadOnlyList<ItemCatalog.BaseType> Types)> Setup = new(() =>
    {
        string data = Data();
        var catalog = new ItemCatalog(data);
        var rolls = new ItemRolls(catalog, data);
        using var bases = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(data, "tables/BaseItemTypes.json")));
        var types = Enumerable.Range(0, bases.RootElement.GetArrayLength()).Select(i => catalog.Row(i)!)
            .Where(t => t.Supported && t.Components.Contains("Mods")).OrderBy(t => t.ModDomain == 1 ? 0 : 1)
            .GroupBy(t => t.Class).Select(g => g.First()).ToList();
        return (catalog, new EssenceCrafting(catalog, rolls, data), rolls, types);
    });

    public static IEnumerable<object[]> EveryEssence() => Setup.Value.Essences.All.Select(e => new object[] { e.Id });

    [Theory]
    [MemberData(nameof(EveryEssence))]
    public void Every_essence_has_the_exact_guaranteed_affix_on_every_supported_class(string essenceId)
    {
        var (catalog, crafting, _, types) = Setup.Value;
        var essence = crafting.All.Single(e => e.Id == essenceId);
        var rng = new Random(123);
        foreach (var type in types)
        {
            var expected = crafting.Guaranteed(essence, type);
            foreach (byte level in new byte[] { 1, 100 })
            {
                var input = ItemCatalog.Create(type) with { ItemLevel = level, Quality = 13 };
                var output = crafting.Apply(essence.Hash, input, rng);
                if (expected is null || expected.Domain != type.ModDomain) { Assert.Null(output); continue; }
                Assert.NotNull(output);
                Assert.Equal(ItemRarity.Rare, output.Rarity);
                Assert.True(output.Identified);
                Assert.Equal(input.ItemLevel, output.ItemLevel);
                Assert.Equal(input.Quality, output.Quality);
                Assert.Equal(input.Sockets, output.Sockets);
                Assert.Equal(input.Links, output.Links);
                Assert.Equal(input.Mods.Where(m => m.Section != ModSection.Explicit), output.Mods.Where(m => m.Section != ModSection.Explicit));
                var explicits = output.Mods.Where(m => m.Section == ModSection.Explicit).ToList();
                Assert.True(explicits.Count is >= 4 and <= 6, $"{essenceId} on {type.Id} ({type.Class}, domain {type.ModDomain}, level {level}, tags {string.Join(',', type.Tags)}): {explicits.Count} affixes");
                Assert.Single(explicits, m => m.Hash == expected.Hash);
                var mods = explicits.Select(m => catalog.Mods.ByHash(m.Hash)!).ToList();
                Assert.InRange(mods.Count(m => m.GenerationType == ItemRolls.Prefix), 0, 3);
                Assert.InRange(mods.Count(m => m.GenerationType == ItemRolls.Suffix), 0, 3);
                var families = mods.SelectMany(m => m.Families).ToList();
                Assert.Equal(families.Count, families.Distinct().Count());
                foreach (var rolled in explicits)
                {
                    var mod = catalog.Mods.ByHash(rolled.Hash)!;
                    Assert.Equal(mod.Stats.Count, rolled.Values.Count);
                    for (int i = 0; i < mod.Stats.Count; i++)
                        Assert.InRange(rolled.Values[i], Math.Min(mod.Stats[i].Min, mod.Stats[i].Max), Math.Max(mod.Stats[i].Min, mod.Stats[i].Max));
                    if (mod.Hash != expected.Hash)
                    {
                        Assert.False(mod.EssenceOnly);
                        Assert.InRange(mod.Level, 0, Math.Min(level, essence.ModLevelCap > 0 ? essence.ModLevelCap : level));
                        Assert.True(mod.WeightFor(type.Tags) > 0);
                    }
                }
                var reforged = crafting.Apply(essence.Hash, output, rng);
                if (essence.ReforgesRare) Assert.NotNull(reforged); else Assert.Null(reforged);
                Assert.Null(crafting.Apply(essence.Hash, input with { Rarity = ItemRarity.Magic }, rng));
                Assert.Null(crafting.Apply(essence.Hash, input with { Rarity = ItemRarity.Unique }, rng));
                Assert.Null(crafting.Apply(essence.Hash, input with { Corrupted = true }, rng));
                Assert.Null(crafting.Apply(essence.Hash, input with { Mirrored = true }, rng));
            }
        }
    }

    [Fact]
    public void Every_class_mapping_matches_the_raw_table_including_quivers_and_weapon_subclasses()
    {
        var (catalog, crafting, _, types) = Setup.Value;
        using var data = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Data(), "tables/Essences.json")));
        foreach (var essence in crafting.All)
        {
            var row = data.RootElement.EnumerateArray().Single(r => catalog.Row(r.GetProperty("BaseItemTypesKey").GetInt32())!.Hash == essence.Hash);
            foreach (var type in types)
            {
                string column = type.Class switch
                {
                    "Quiver" => "Display_Quiver_ModsKey", "Rune Dagger" => "Dagger_ModsKey",
                    "Warstaff" => "Staff_ModsKey", "Thrusting One Hand Sword" => "OneHandThrustingSword_ModsKey",
                    _ => type.Class.Replace(" ", "") + "_ModsKey",
                };
                var expected = row.TryGetProperty(column, out var key) && key.ValueKind == JsonValueKind.Number
                    ? catalog.Mods.Row(key.GetInt32()) : null;
                Assert.Equal(expected, crafting.Guaranteed(essence, type));
            }
        }
    }

    [Fact]
    public void The_table_has_all_craftable_essences_and_does_not_treat_a_remnant_as_item_currency()
    {
        var (catalog, crafting, _, _) = Setup.Value;
        Assert.Equal(105, crafting.All.Count);
        Assert.False(crafting.Contains(catalog.Find("CurrencyCorruptMonolith")!.Hash));
        Assert.Contains(crafting.All, e => e.Id.EndsWith("CurrencyEssenceFaridun1") && e.ReforgesRare && e.ModLevelCap == 15);
        Assert.All(crafting.All, e => Assert.Equal(24, e.ModRows.Count));
    }

    [Fact]
    public void Repeated_use_from_the_essence_tab_consumes_one_and_saves_the_exact_mod()
    {
        TestSaves.Ensure();
        var (catalog, crafting, _, types) = Setup.Value;
        string name = "Essence" + Guid.NewGuid().ToString("N");
        var items = PlayerInventories.For(name, catalog);
        var stash = AccountStash.For(name, catalog);
        var essence = crafting.All.First(e => e.Tier == 7);
        var type = types.Single(t => t.Class == "Helmet");
        PlayerInventories.Shape? Shape(Item i) => catalog.ByHash(i.TypeHash) is { } t ? new(t.Width, t.Height, t.Class, t.MaxStack) : null;
        var stack = stash.AddStacked(8, ItemCatalog.Create(catalog.ByHash(essence.Hash)!) with { StackSize = 3 }, Shape, out _)!.Added.Single();
        var target = items.Add(PlayerInventories.MainInventory, 0, 0, ItemCatalog.Create(type) with { ItemLevel = 85 });
        for (int n = 2; n >= 0; n--)
        {
            var changed = crafting.Apply(essence.Hash, items.Items(PlayerInventories.MainInventory).Single(i => i.Id == target.Id).Item, new Random(n))!;
            var used = ItemMoves.UseAcross(items, stash, new(8, 0, stack.Id),
                new(null, PlayerInventories.MainInventory, target.Id), _ => changed)!.Value;
            Assert.Equal(n, used.Left?.Item.StackSize ?? 0);
            Assert.Single(used.Changed.Item.Mods, m => m.Section == ModSection.Explicit && m.Hash == crafting.Guaranteed(essence, type)!.Hash);
        }
        Assert.Empty(stash.Inventory(8).Items);
        Assert.Equal(ItemRarity.Rare, CharacterSaves.Load(name)!.Inventories[PlayerInventories.MainInventory].Single(i => i.Id == target.Id).Item.Rarity);
    }

    [Fact]
    public void Fractures_survive_and_conflicting_fractures_or_metacrafts_are_refused()
    {
        var (catalog, crafting, rolls, types) = Setup.Value;
        var essence = crafting.All.First(e => e.Tier == 7);
        var type = types.Single(t => t.Class == "Wand");
        var forced = crafting.Guaranteed(essence, type)!;
        var rng = new Random(5);
        var input = ItemCatalog.Create(type) with { ItemLevel = 100, Rarity = ItemRarity.Rare, FracturedCount = 1,
            Mods = [forced.Roll(ModSection.Explicit, rng)] };
        Assert.Null(crafting.Apply(essence.Hash, input, rng));
        var fixedMod = rolls.Pool(type, 100).First(p => !p.Mod.Families.Intersect(forced.Families).Any()).Mod.Roll(ModSection.Explicit, rng);
        input = input with { Mods = [fixedMod] };
        var output = crafting.Apply(essence.Hash, input, rng)!;
        Assert.Equal(1, output.FracturedCount);
        Assert.Equal(fixedMod, output.Mods.First(m => m.Section == ModSection.Explicit));
        foreach (string id in new[] { "StrMasterItemGenerationCannotChangePrefixes", "DexMasterItemGenerationCannotChangeSuffixes",
            "IntMasterItemGenerationCannotRollAttackAffixes", "StrDexMasterItemGenerationCannotRollCasterAffixes" })
            Assert.Null(crafting.Apply(essence.Hash, input with { FracturedCount = 0,
                Mods = [catalog.Mods.ById(id)!.Roll(ModSection.Explicit, rng)] }, rng));
    }
}
