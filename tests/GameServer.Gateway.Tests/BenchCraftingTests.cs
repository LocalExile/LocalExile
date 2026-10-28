using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class BenchCraftingTests
{
    public static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "data/tables/CraftingBenchOptions.json"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("CraftingBenchOptions.json");
    }
    internal static readonly Lazy<(ItemCatalog Catalog, ItemRolls Rolls, BenchCrafting Bench, List<ItemCatalog.BaseType> Types)> Setup = new(() =>
    {
        string data = Data();
        var catalog = new ItemCatalog(data);
        var rolls = new ItemRolls(catalog, data);
        using var bases = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(data, "tables/BaseItemTypes.json")));
        var types = Enumerable.Range(0, bases.RootElement.GetArrayLength()).Select(i => catalog.Row(i)!)
            .Where(t => t.Supported && t.Components.Contains("Mods")).OrderBy(t => t.ModDomain is 1 or 2 ? 0 : 1)
            .GroupBy(t => t.ClassRow).Select(g => g.First()).ToList();
        return (catalog, rolls, new BenchCrafting(catalog, rolls, data), types);
    });

    public static IEnumerable<object[]> Recipes() => Setup.Value.Bench.All.Select(r => new object[] { r.Row });

    [Theory]
    [MemberData(nameof(Recipes))]
    public void Every_active_recipe_applies_its_exact_change_only_to_allowed_item_classes(int row)
    {
        var (catalog, rolls, bench, types) = Setup.Value;
        var recipe = bench.Find(row)!;
        int successful = 0;
        foreach (var type in types)
        {
            var rng = new Random(row + type.Row);
            var input = ItemCatalog.Create(type) with { ItemLevel = 100, Quality = 13 };
            int cap = CurrencyEffects.SocketCapacity(input, type);
            int count = recipe.Sockets > 0 ? Math.Min(1, cap) : cap;
            input = input with { Sockets = Enumerable.Repeat(new ItemSocket(SocketColour.White), count).ToArray(),
                Links = Enumerable.Repeat((byte)1, count).ToArray() };
            if (recipe.Action == 0)
                input = input with { Rarity = ItemRarity.Rare, Mods = [bench.All.First(r => r.Mod is not null).Mod!.Roll(ModSection.Explicit, rng)] };
            else if (recipe.Action == 1)
                input = input with { Mods = [bench.All.First(r => r.Enchant is not null).Enchant!.Roll(ModSection.Enchant, rng)] };
            else if (recipe.Action is 8 or 9)
                input = rolls.Roll(input, ItemRarity.Rare, rng, type.Class is "Jewel" or "AbyssJewel" ? (4, 2) : (6, 3));
            var output = bench.Apply(row, input, false, rng);
            bool allowed = recipe.Classes.Contains(type.ClassRow)
                && (recipe.Mod is null || type.ModDomain is 1 or 2)
                && (recipe.Sockets == 0 || recipe.Sockets <= cap)
                && (recipe.Links == 0 || recipe.Links <= cap)
                && recipe.Colours.Length <= cap
                && (recipe.Action is not (8 or 9) || input.Mods.Count(m => m.Section == ModSection.Explicit) >= (recipe.Action == 8 ? 3 : 1));
            Assert.True((output is not null) == allowed, $"recipe {row} ({recipe.Mod?.Id ?? recipe.Name}) on {type.Id}: expected {allowed}");
            if (output is null) continue;
            successful++;
            Assert.Equal(input.TypeHash, output.Item.TypeHash);
            Assert.Equal(input.ItemLevel, output.Item.ItemLevel);
            Assert.Equal(input.Quality, output.Item.Quality);
            Assert.Equal(recipe.Costs, output.Costs);
            if (recipe.Mod is { } mod)
            {
                var crafted = Assert.Single(output.Item.Mods, m => m.Section == ModSection.Explicit && m.Hash == mod.Hash);
                Assert.Equal(mod.Stats.Count, crafted.Values.Count);
                for (int i = 0; i < mod.Stats.Count; i++) Assert.InRange(crafted.Values[i], Math.Min(mod.Stats[i].Min, mod.Stats[i].Max), Math.Max(mod.Stats[i].Min, mod.Stats[i].Max));
                Assert.Equal(ItemRarity.Magic, output.Item.Rarity);
            }
            if (recipe.Enchant is { } enchant) Assert.Single(output.Item.Mods, m => m.Section == ModSection.Enchant && m.Hash == enchant.Hash);
            if (recipe.Sockets > 0) Assert.Equal(recipe.Sockets, output.Item.Sockets.Count);
            if (recipe.Links > 0) Assert.Equal(recipe.Links, output.Item.Links![0]);
            if (recipe.Colours.Length > 0) Assert.True(output.Item.Sockets.Count(s => s.Colour != SocketColour.White) >= recipe.Colours.Length);
            if (recipe.Action == 0) Assert.DoesNotContain(output.Item.Mods, bench.IsCrafted);
            if (recipe.Action == 1) Assert.DoesNotContain(output.Item.Mods, m => m.Section == ModSection.Enchant);
            Assert.Null(bench.Apply(row, input with { Mirrored = true }, false, rng));
            if (recipe.Sockets == 0 && recipe.Links == 0 && recipe.Colours.Length == 0)
                Assert.Null(bench.Apply(row, input with { Corrupted = true }, false, rng));
        }
        Assert.True(successful > 0, $"Recipe {row} has no compatible supported item class");
    }

    [Fact]
    public void The_raw_table_unlocks_every_active_recipe_and_costs_are_preserved()
    {
        var (catalog, _, bench, _) = Setup.Value;
        using var table = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Data(), "tables/CraftingBenchOptions.json")));
        int count = 0;
        foreach (var row in table.RootElement.EnumerateArray().Select((data, index) => (data, index)))
        {
            bool enabled = !row.data.GetProperty("IsDisabled").GetBoolean() && !row.data.GetProperty("IsAreaOption").GetBoolean() && !row.data.GetProperty("MapDevice").GetBoolean();
            Assert.Equal(enabled, bench.Find(row.index) is not null);
            if (!enabled) continue;
            count++;
            var recipe = bench.Find(row.index)!;
            Assert.Equal(row.data.GetProperty("AddMod").ValueKind == JsonValueKind.Number ? catalog.Mods.Row(row.data.GetProperty("AddMod").GetInt32()) : null, recipe.Mod);
            Assert.All(recipe.Unlocks, flag => Assert.Contains(flag, bench.UnlockFlags));
            Assert.Equal(row.data.GetProperty("Cost_Values").EnumerateArray().Select(v => v.GetInt32()), recipe.Costs.Select(c => c.Amount));
            using var categories = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Data(), "tables/CraftingItemClassCategories.json")));
            var expectedClasses = row.data.GetProperty("ItemClasses").EnumerateArray().Select(v => v.GetInt32()).ToHashSet();
            if (expectedClasses.Count == 0)
                foreach (var category in row.data.GetProperty("CraftingItemClassCategories").EnumerateArray())
                    expectedClasses.UnionWith(categories.RootElement[category.GetInt32()].GetProperty("ItemClasses").EnumerateArray().Select(v => v.GetInt32()));
            Assert.True(expectedClasses.SetEquals(recipe.Classes));
        }
        Assert.Equal(765, count);
        Assert.Equal(130, bench.UnlockFlags.Count());
    }

    [Fact]
    public void Multimod_affix_limits_and_protected_prefixes_are_respected()
    {
        var (catalog, rolls, bench, types) = Setup.Value;
        var type = types.Single(t => t.Class == "Wand");
        var rng = new Random(42);
        var item = ItemCatalog.Create(type) with { Rarity = ItemRarity.Rare, ItemLevel = 100 };
        var multi = bench.All.Single(r => r.Mod?.Id == "StrIntMasterItemGenerationCanHaveMultipleCraftedMods");
        item = bench.Apply(multi.Row, item, false, rng)!.Item;
        var first = bench.All.First(r => r.Mod?.GenerationType == 1 && r.Classes.Contains(type.ClassRow));
        item = bench.Apply(first.Row, item, false, rng)!.Item;
        var second = bench.All.First(r => r.Mod?.GenerationType == 1 && r.Classes.Contains(type.ClassRow) && !r.Mod.Families.Intersect(first.Mod!.Families).Any());
        item = bench.Apply(second.Row, item, false, rng)!.Item;
        Assert.Equal(3, item.Mods.Count(bench.IsCrafted));
        var fourth = bench.All.First(r => r.Mod?.GenerationType == 1 && r.Classes.Contains(type.ClassRow) &&
            !r.Mod.Families.Intersect(first.Mod!.Families.Concat(second.Mod!.Families)).Any());
        Assert.Null(bench.Apply(fourth.Row, item, false, rng));
        var full = rolls.Roll(ItemCatalog.Create(type) with { ItemLevel = 100 }, ItemRarity.Rare, rng, (6, 3));
        Assert.Null(bench.Apply(first.Row, full, false, rng));
        var protect = catalog.Mods.ById("StrMasterItemGenerationCannotChangePrefixes")!.Roll(ModSection.Explicit, rng);
        var prefixes = full.Mods.Where(m => m.Section == ModSection.Explicit && catalog.Mods.ByHash(m.Hash)!.GenerationType == 1).ToList();
        var suffixes = full.Mods.Where(m => m.Section == ModSection.Explicit && catalog.Mods.ByHash(m.Hash)!.GenerationType == 2).Take(2).ToList();
        full = full with { Mods = [.. prefixes, .. suffixes, protect] };
        var changed = bench.Apply(818, full, false, rng)!.Item;
        Assert.All(prefixes, p => Assert.Contains(p, changed.Mods));
        Assert.Null(bench.Apply(818, full with { Mods = [.. prefixes, protect] }, false, rng));
    }

    [Fact]
    public void Corrupted_socket_crafts_add_the_exact_vaal_cost_and_keep_socketed_gems_safe()
    {
        var (catalog, _, bench, types) = Setup.Value;
        var type = types.Single(t => t.Class == "Body Armour");
        var rng = new Random(1);
        var item = ItemCatalog.Create(type, [new(SocketColour.Red)], [1]) with { Corrupted = true, ItemLevel = 1 };
        var recipe = bench.All.Single(r => r.Sockets == 6);
        var output = bench.Apply(recipe.Row, item, false, rng)!;
        Assert.Equal(6, output.Item.Sockets.Count); // Bench socket counts bypass item level, never base capacity.
        Assert.Equal(1, output.Item.ItemLevel);
        Assert.True(output.Item.Corrupted);
        Assert.Contains(output.Costs, c => c.Currency == catalog.Find(CurrencyEffects.VaalOrb)!.Hash && c.Amount == recipe.Costs.Sum(x => x.Amount));
        var gem = ItemCatalog.Create(catalog.Find("Metadata/Items/Gems/SkillGemFireball")!);
        Assert.Null(bench.Apply(recipe.Row, item with { Sockets = [new(SocketColour.Red, gem)] }, false, rng));
        var links = bench.All.Single(r => r.Links == 6);
        item = output.Item with { Sockets = output.Item.Sockets.Select((s, i) => i == 0 ? s with { Gem = gem } : s).ToList() };
        var linked = bench.Apply(links.Row, item, false, rng)!;
        Assert.Equal(new byte[] { 6 }, linked.Item.Links);
        Assert.Equal(gem, linked.Item.Sockets[0].Gem);
    }
}
