using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// Three tattoos of one tribe vendor into one random tattoo of that tribe (tattoos.md, "Vendor"). The client tables
// carry the tribe but not the recipe weights, so TattooCatalogue keeps the wiki's 60,000-vendoring sample weights.
public sealed class VendorTattooRecipeTests
{
    static InventoryItem Tattoo(uint id, string name, ItemCatalog catalog) =>
        new(id, 0, 0, TattooCatalogue.Create(catalog, name)! with { StackSize = 1 });

    static string? RewardName(Item reward, ItemCatalog catalog) => catalog.ByHash(reward.TypeHash)?.Name;

    [Fact]
    public void Three_same_tribe_tattoos_vendor_into_one_random_tattoo_of_that_tribe()
    {
        var catalog = new ItemCatalog("data");
        var recipes = new VendorRecipes(catalog);
        var arohongui = TattooCatalogue.Find("Tattoo of the Arohongui Shaman")!;
        Assert.Equal(5, arohongui.Tribe);
        var offered = new[]
        {
            Tattoo(1, "Tattoo of the Arohongui Moonwarden", catalog),
            Tattoo(2, "Tattoo of the Arohongui Scout", catalog),
            Tattoo(3, "Tattoo of the Arohongui Warrior", catalog),
        };
        var quote = recipes.Evaluate(offered, new Random(7));
        Assert.Contains("three tattoos of one tribe", quote.Recipes);
        string? name = RewardName(Assert.Single(quote.Rewards), catalog);
        Assert.NotNull(name);
        var entry = TattooCatalogue.Find(name!);
        Assert.NotNull(entry);
        Assert.Equal(arohongui.Tribe, entry!.Tribe);
        Assert.False(entry.DropDisabled);
        // A fourth unit in the same tribe is left over and sold normally; one recipe is still quoted once.
        var four = new[]
        {
            offered[0], offered[1], offered[2],
            Tattoo(4, "Tattoo of the Arohongui Warmonger", catalog),
        };
        var leftover = recipes.Evaluate(four, new Random(7));
        Assert.Single(leftover.Recipes, recipe => recipe == "three tattoos of one tribe");
        Assert.Equal(2, leftover.Rewards.Count);
        Assert.Single(leftover.Rewards, reward => RewardName(reward, catalog) == "Tattoo of the Arohongui Shaman"
            || TattooCatalogue.Find(RewardName(reward, catalog) ?? "")?.Tribe == arohongui.Tribe);
        // Loyalty and Makanga tattoos are tattoos of their tribe and can go in; the untagged Journey rows cannot.
        var special = new[]
        {
            Tattoo(5, "Loyalty Tattoo of Kaom", catalog),
            Tattoo(6, "Tattoo of the Ngamahu Makanga", catalog),
            Tattoo(7, "Tattoo of the Ngamahu Firewalker", catalog),
        };
        Assert.Equal(0, TattooCatalogue.Find("Loyalty Tattoo of Kaom")!.Tribe);
        Assert.Equal(0, TattooCatalogue.Find("Tattoo of the Ngamahu Makanga")!.Tribe);
        Assert.Contains("three tattoos of one tribe", recipes.Evaluate(special, new Random(7)).Recipes);
        Assert.Equal(11, TattooCatalogue.Find("Journey Tattoo of the Body")!.Tribe);
        var journey = new[]
        {
            Tattoo(8, "Journey Tattoo of the Body", catalog),
            Tattoo(9, "Journey Tattoo of the Mind", catalog),
            Tattoo(10, "Journey Tattoo of the Soul", catalog),
        };
        var untagged = recipes.Evaluate(journey, new Random(7));
        Assert.DoesNotContain("three tattoos of one tribe", untagged.Recipes);
        Assert.DoesNotContain(untagged.Rewards, reward => RewardName(reward, catalog)?.Contains("Tattoo") == true);
        // Three different tribes do not match the recipe either; they are sold normally and no tattoo comes back.
        var mixed = new[]
        {
            offered[0],
            Tattoo(11, "Tattoo of the Kitava Warrior", catalog),
            Tattoo(12, "Tattoo of the Ramako Scout", catalog),
        };
        var different = recipes.Evaluate(mixed, new Random(7));
        Assert.DoesNotContain("three tattoos of one tribe", different.Recipes);
        Assert.DoesNotContain(different.Rewards, reward => RewardName(reward, catalog)?.Contains("Tattoo") == true);
    }

    [Fact]
    public void The_vendor_pools_cover_each_tribe_with_the_wiki_weights()
    {
        var names = new HashSet<string>();
        for (int tribe = 0; tribe <= 9; tribe++)
        {
            var pool = TattooCatalogue.VendorPool(tribe);
            Assert.Equal(5, pool.Count);
            foreach (var (name, weight) in pool)
            {
                var entry = TattooCatalogue.Find(name);
                Assert.NotNull(entry);
                Assert.Equal(tribe, entry!.Tribe);
                Assert.False(entry.DropDisabled);
                Assert.True(names.Add(name), name);
                Assert.InRange(weight, 1, 4);
            }
            // The wiki's per-tribe distributions: 3:3:3:1:1, and Kitava 2:2:2:4:1.
            int[] expected = tribe == 1 ? [1, 2, 2, 2, 4] : [1, 1, 3, 3, 3];
            Assert.Equal(expected, pool.Select(entry => entry.Weight).OrderBy(weight => weight).ToArray());
        }
        Assert.Equal(11, TattooCatalogue.VendorPool(5).Sum(pool => pool.Weight));
        Assert.Equal(11, TattooCatalogue.VendorPool(1).Sum(pool => pool.Weight));
        // The weighted pick only returns pool members, and every member is reachable.
        var picked = Enumerable.Range(0, 1000)
            .Select(seed => TattooCatalogue.VendorTattoo(5, new Random(seed))!)
            .ToHashSet();
        Assert.Equal(TattooCatalogue.VendorPool(5).Select(pool => pool.Name).ToHashSet(), picked);
        Assert.Null(TattooCatalogue.VendorTattoo(11, new Random(1)));
    }
}
