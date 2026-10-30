using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class EssenceCraftingTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
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

}
