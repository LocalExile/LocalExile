using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ItemCrafterTests
{
    [Fact]
    public void Build_makes_the_composed_item_and_refuses_what_does_not_exist()
    {
        var items = new ItemCatalog("data");
        var rolls = new ItemRolls(items);
        var ring = items.All.First(t => t.Name == "Iron Ring");
        var words = rolls.Words.Where(w => w.List is 1 or 2).GroupBy(w => w.List).Select(g => g.First().Key).ToList();
        string json = $$"""
            {"base":"{{ring.Id}}","rarity":"rare","itemLevel":84,"states":["corrupted","shaper","Shaper"],"fractured":1,
             "name":[{{words[0]}},{{words[1]}}],
             "mods":[{"id":"{{ring.Implicits![0].Id}}","values":[3]},{"id":"IncreasedLife7","values":[114]},
                     {"id":"FireResist1","section":"explicit"}]}
            """;
        var item = ItemCrafter.Build(items, rolls, json, 10, new Random(3), out string? error);
        Assert.Null(error);
        Assert.NotNull(item);
        Assert.Equal(ring.Hash, item.TypeHash);
        Assert.Equal((ItemRarity.Rare, (byte)84, true, ItemInfluence.Shaper, (byte)1), (item.Rarity, item.ItemLevel, item.Corrupted, item.Influences, item.FracturedCount));
        Assert.Equal(words, item.NameWords);
        Assert.Equal([ModSection.Implicit, ModSection.Explicit, ModSection.Explicit], item.Mods.Select(m => m.Section));
        // The implicit adds physical damage, a minimum and a maximum: the missing maximum is rolled.
        Assert.Equal((3, ring.Implicits[0].Stats.Count), (item.Mods[0].Values[0], item.Mods[0].Values.Count));
        Assert.Equal([114], item.Mods[1].Values);
        var fire = items.Mods.ById("FireResist1")!;
        Assert.InRange(Assert.Single(item.Mods[2].Values), fire.RangeAt(0).Min, fire.RangeAt(0).Max);

        var armour = items.All.First(t => t.Class == "Body Armour" && ItemCrafter.Craftable(items, t));
        var linked = ItemCrafter.Build(items, rolls, $$"""{"base":"{{armour.Id}}","sockets":"RRG B"}""", 70, new Random(1), out _)!;
        Assert.Equal([SocketColour.Red, SocketColour.Red, SocketColour.Green, SocketColour.Blue], linked.Sockets.Select(s => s.Colour));
        Assert.Equal([3, 1], linked.Links!);
        Assert.Equal((ItemRarity.Normal, (byte)70), (linked.Rarity, linked.ItemLevel));
        Assert.Empty(ItemCrafter.Build(items, rolls, $$"""{"base":"{{armour.Id}}","sockets":""}""", 70, new Random(1), out _)!.Sockets);

        foreach (var (bad, reason) in new[]
        {
            ("""{"base":"No Such Ring"}""", "No item type"),
            ($$"""{"base":"{{ring.Id}}","mods":[{"id":"NoSuchMod"}]}""", "No mod"),
            ($$"""{"base":"{{ring.Id}}","states":["clear"]}""", "no state"),
            ($$"""{"base":"{{ring.Id}}","rarity":"legendary"}""", "no rarity"),
            ("not json", "not an item"),
        })
        {
            Assert.Null(ItemCrafter.Build(items, rolls, bad, 10, new Random(1), out error));
            Assert.StartsWith(reason, error);
        }
    }

    [Fact]
    public void Catalog_lists_a_bases_pools_and_the_descriptions_of_their_stats()
    {
        var descriptions = StatDescriptions.Parse("""
            description
            	1 base_maximum_life
            	1
            		# "{0:+d} to maximum Life"
            description
            	2 attack_minimum_added_physical_damage attack_maximum_added_physical_damage
            	2
            		1|# # "Adds {0} to {1} [Physical] Damage to Attacks" reminderstring ReminderTextX canonical_line
            		#|-1 # "Removes [Physical|Physical Damage] {0}" negate 1
            no_description level
            """.Split('\n'));
        var life = descriptions.For("base_maximum_life")!;
        Assert.Equal(["#"], Assert.Single(life.Variants).Conditions);
        var added = descriptions.For("attack_maximum_added_physical_damage")!;
        Assert.Same(added, descriptions.For("attack_minimum_added_physical_damage"));
        Assert.Equal(("Adds {0} to {1} Physical Damage to Attacks", 0), (added.Variants[0].Text, added.Variants[0].Handlers.Count));
        Assert.Equal(["#|-1", "#"], added.Variants[1].Conditions);
        Assert.Equal(("Removes Physical Damage {0}", ("negate", 1)), (added.Variants[1].Text, Assert.Single(added.Variants[1].Handlers)));

        var items = new ItemCatalog("data");
        var rolls = new ItemRolls(items);
        var lines = ItemCrafter.Catalog(items, rolls, new BenchCrafting(items, rolls), descriptions).ToList();
        var pools = lines.Where(l => (string?)l["type"] == "pool").ToDictionary(l => (int)l["id"]!, l => l["mods"]!.AsArray().Select(m => (string)m!).ToList());
        var ring = lines.Single(l => (string?)l["type"] == "base" && (string?)l["name"] == "Iron Ring");
        Assert.Contains("IncreasedLife7", pools[(int)ring["pool"]!]);
        Assert.DoesNotContain(pools[(int)ring["pool"]!], id => pools[(int)ring["influence"]!["shaper"]!].Contains(id));
        Assert.Contains(lines, l => (string?)l["type"] == "mod" && (string?)l["id"] == "IncreasedLife7" && (int)l["level"]! == 44);
        var desc = Assert.Single(lines, l => (string?)l["type"] == "desc" && l["stats"]!.AsArray().Select(s => (string)s!).SequenceEqual(["base_maximum_life"]));
        Assert.Equal("{0:+d} to maximum Life", (string?)desc["variants"]![0]!["text"]);
        Assert.Contains(lines, l => (string?)l["type"] == "word" && (int)l["list"]! == 1);
        Assert.DoesNotContain(lines, l => (string?)l["type"] == "base" && items.Find((string)l["id"]!) is { } t && items.Gem(t) is not null);

        // A cluster jewel is its enchantments, which no implicit list or pool names: the base has the ones a tool starts
        // from, and what they can be changed to. A theme opens its own affix pool, where its notables are.
        Assert.DoesNotContain(ring, p => p.Key is "enchants" or "cluster");
        var large = lines.Single(l => (string?)l["type"] == "base" && (string?)l["name"] == "Large Cluster Jewel");
        Assert.Equal("""[{"id":"JewelExpansionPassiveNodes","values":[1,8],"ranges":[[1,17],[8,12]]},{"id":"JewelExpansionJewelNodesLarge2___","values":[2],"ranges":[[2,2]]}]""",
            large["enchants"]!.ToJsonString());
        var cluster = large["cluster"]!;
        Assert.Equal("""{"mod":"JewelExpansionPassiveNodes","min":8,"max":12}""", cluster["passives"]!.ToJsonString());
        Assert.Equal("""[{"count":1,"mod":"JewelExpansionJewelNodesLarge1"},{"count":2,"mod":"JewelExpansionJewelNodesLarge2___"}]""", cluster["sockets"]!.ToJsonString());
        var themes = cluster["themes"]!.AsArray();
        Assert.Equal(Enumerable.Range(1, 17), themes.Select(t => (int)t!["value"]!));
        var fire = themes.Single(t => (string?)t!["name"] == "Fire Damage")!;
        var grants = Assert.Single(fire["stats"]!.AsArray())!;
        Assert.Equal((13, "affliction_fire_damage", "fire_damage_+%", 12),
            ((int)fire["value"]!, (string)fire["tag"]!, (string)grants["id"]!, (int)grants["value"]!));
        Assert.Contains("AfflictionNotablePrismaticHeart__", pools[(int)fire["pool"]!]);
        Assert.DoesNotContain("AfflictionNotablePrismaticHeart__", pools[(int)large["pool"]!]);
        Assert.Contains(lines, l => (string?)l["type"] == "mod" && (string?)l["id"] == "AfflictionNotablePrismaticHeart__");
        // The medium themes come after the large ones, the small ones last; a small jewel has no socket enchantment.
        var small = lines.Single(l => (string?)l["type"] == "base" && (string?)l["name"] == "Small Cluster Jewel");
        Assert.Equal("""[{"id":"JewelExpansionPassiveNodes","values":[39,2],"ranges":[[39,55],[2,3]]}]""", small["enchants"]!.ToJsonString());
        Assert.Empty(small["cluster"]!["sockets"]!.AsArray());
        // Rows the game no longer gives a new jewel are not offered.
        var medium = lines.Single(l => (string?)l["type"] == "base" && (string?)l["name"] == "Medium Cluster Jewel")["cluster"]!["themes"]!.AsArray();
        Assert.Equal(19, medium.Count);
        Assert.DoesNotContain(medium, t => ((string)t!["tag"]!).StartsWith("old_do_not_use", StringComparison.Ordinal));
    }
}
