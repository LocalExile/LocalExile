using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class BenchCraftingTests
{
    public static string Data()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
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
            var output = bench.Apply(row, input, rng);
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
            Assert.Null(bench.Apply(row, input with { Mirrored = true }, rng));
            if (recipe.Sockets == 0 && recipe.Links == 0 && recipe.Colours.Length == 0)
                Assert.Null(bench.Apply(row, input with { Corrupted = true }, rng));
        }
        Assert.True(successful > 0, $"Recipe {row} has no compatible supported item class");
    }

    [Fact]
    public void An_ordinary_craft_replaces_the_existing_crafted_modifier_for_the_recipe_price_only()
    {
        var (_, _, bench, types) = Setup.Value;
        var type = types.First(t => t.Class == "Body Armour");
        var recipes = bench.All.Where(r => r.Mod is not null && r.Classes.Contains(type.ClassRow)
            && !r.Mod.Stats.Any(s => s.Id.Contains("crafted_mod"))).ToList();
        var first = recipes.First(r => r.Mod!.GenerationType == 1);
        var other = recipes.First(r => r.Mod!.GenerationType == 2);
        var rng = new Random(5);
        var item = bench.Apply(first.Row, ItemCatalog.Create(type) with { ItemLevel = 100, Rarity = ItemRarity.Rare }, rng)!.Item;

        foreach (var recipe in new[] { first, other })
        {
            var result = bench.Apply(recipe.Row, item, rng);
            Assert.NotNull(result);
            Assert.Equal(recipe.Mod!.Hash, Assert.Single(result.Item.Mods, bench.IsCrafted).Hash);
            Assert.Equal(recipe.Costs, result.Costs);
        }
    }

    [Fact]
    public void A_magic_ring_recrafts_and_replaces_its_crafted_life()
    {
        var (catalog, _, bench, _) = Setup.Value;
        var rng = new Random(3);
        ModCatalog.Mod Mod(string id) => catalog.Mods.ById(id)!;
        var ring = ItemCatalog.Create(catalog.Find("Metadata/Items/Rings/Ring1")!) with
        {
            ItemLevel = 24, Rarity = ItemRarity.Magic, Identified = true,
            Mods = [Mod("AddedPhysicalDamageImplicitRing1").Roll(ModSection.Implicit, rng), Mod("ItemFoundRarityIncrease1").Roll(ModSection.Explicit, rng)],
        };
        var life = bench.All.First(r => r.Mod?.Id == "EinharMasterIncreasedLife3");
        var fire = bench.All.First(r => r.Mod?.Id == "EinharMasterAddedFireDamage2");
        ring = bench.Apply(life.Row, ring, rng)!.Item;

        foreach (var recipe in new[] { life, fire })
        {
            var result = bench.Apply(recipe.Row, ring, rng);
            Assert.NotNull(result);
            Assert.Equal(recipe.Mod!.Hash, Assert.Single(result.Item.Mods, bench.IsCrafted).Hash);
            Assert.Contains(result.Item.Mods, m => m.Hash == Mod("ItemFoundRarityIncrease1").Hash);
            Assert.Equal(recipe.Costs, result.Costs);
        }
    }

    [Fact]
    public void With_multimod_replace_only_swaps_the_conflicting_craft()
    {
        var (_, _, bench, types) = Setup.Value;
        var type = types.First(t => t.Class == "Body Armour");
        var recipes = bench.All.Where(r => r.Mod is not null && r.Classes.Contains(type.ClassRow)).ToList();
        var multimod = recipes.First(r => r.Mod!.Stats.Any(s => s.Id == "item_generation_can_have_multiple_crafted_mods"));
        var prefix = recipes.First(r => r.Mod!.GenerationType == 1);
        var rng = new Random(7);
        var item = ItemCatalog.Create(type) with { ItemLevel = 100, Rarity = ItemRarity.Rare };
        item = bench.Apply(multimod.Row, item, rng)!.Item;
        item = bench.Apply(prefix.Row, item, rng)!.Item;
        var keep = item.Mods.Single(m => m.Hash == multimod.Mod!.Hash);

        // The same prefix again replaces only itself; the multimod stays.
        var result = bench.Apply(prefix.Row, item, rng);
        Assert.NotNull(result);
        Assert.Contains(keep, result.Item.Mods);
        Assert.Equal(2, result.Item.Mods.Count(bench.IsCrafted));
        Assert.Equal(prefix.Costs, result.Costs);
    }

}
