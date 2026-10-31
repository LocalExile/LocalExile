using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class TransmutationTests : IDisposable
{
    readonly string _folder;
    readonly ItemCatalog _catalog;
    readonly ItemRolls _rolls;

    public TransmutationTests()
    {
        _folder = TestSaves.Catalog(new(1, "test/Ordinary"), new(2, "test/BaseBlocked"),
            new(3, "test/ClassBlocked"), new(4, "test/EmptyPool"), new(5, "test/NoMods"),
            new(6, "test/PrefixOnly"), new(7, "test/ConflictingFamilies"));
        void Write(string file, object value) => File.WriteAllText(Path.Combine(_folder, file), JsonSerializer.Serialize(value));
        var bases = JsonNode.Parse(File.ReadAllText(Path.Combine(_folder, "tables/BaseItemTypes.json")))!.AsArray();
        for (int i = 0; i < bases.Count; i++)
        {
            bases[i]!["ItemClassesKey"] = i == 2 ? 1 : 0;
            bases[i]!["Unmodifiable"] = i == 1;
            bases[i]!["ModDomain"] = i == 3 ? 99 : 1;
            bases[i]!["TagsKeys"] = i == 5 ? new JsonArray(0, 1) : i == 6 ? new JsonArray(0, 2) : new JsonArray(0);
        }
        File.WriteAllText(Path.Combine(_folder, "tables/BaseItemTypes.json"), bases.ToJsonString());
        Write("tables/ItemClasses.json", new[]
        {
            new { Id = "Body Armour", ItemStance = (int?)null, Unmodifiable = false },
            new { Id = "Relic", ItemStance = (int?)null, Unmodifiable = true },
        });
        Write("item-types.json", new { types = Enumerable.Range(1, 7).Select(hash => new
        {
            type = bases[hash - 1]!["Id"]!.GetValue<string>(),
            components = hash == 5 ? new[] { "Base" } : ["Base", "Mods", "Sockets", "Quality"],
            read = hash == 5 ? 1 : 4,
        }) });
        Write("tables/Tags.json", new[] { new { Id = "default" }, new { Id = "prefix_only" }, new { Id = "conflict" } });
        Write("tables/Stats.json", new[] { new { Id = "base_maximum_life", IsLocal = false } });
        object Mod(int hash, int kind, int family, int weight, int level = 1, int domain = 1,
            bool essence = false) => new Dictionary<string, object?>
        {
            ["Id"] = $"TestMod{hash}", ["HASH16"] = hash, ["Name"] = $"Test Mod {hash}",
            ["Domain"] = domain, ["GenerationType"] = kind, ["Level"] = level,
            ["StatsKey1"] = 0, ["StatsKey2"] = null, ["Stat1Min"] = 10, ["Stat1Max"] = 20,
            ["Families"] = new[] { family }, ["IsEssenceOnlyModifier"] = essence,
            ["SpawnWeight_TagsKeys"] = hash == 103 ? new[] { 2, 0 } : kind == ItemRolls.Suffix ? [1, 0] : [0],
            ["SpawnWeight_Values"] = hash == 103 ? new[] { weight, 0 } : kind == ItemRolls.Suffix ? [0, weight] : [weight],
            ["GenerationWeight_TagsKeys"] = Array.Empty<int>(), ["GenerationWeight_Values"] = Array.Empty<int>(),
        };
        Write("tables/Mods.json", new[]
        {
            Mod(101, 1, 1, 100), Mod(102, 2, 2, 300), Mod(103, 2, 1, 100),
            Mod(104, 1, 3, 100, level: 10), Mod(105, 1, 4, 900, essence: true),
            Mod(106, 2, 5, 0), Mod(107, 1, 6, 100, domain: 2), Mod(108, 3, 7, 0),
        });
        _catalog = new ItemCatalog(_folder);
        _rolls = new ItemRolls(_catalog, _folder);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    Item Input(int hash = 1) => ItemCatalog.Create(_catalog.ByHash((uint)hash)!) with { ItemLevel = 1 };

    Item? Apply(Item item, Random? random = null) => CurrencyEffects.Transmutation(item, _rolls, random ?? new Random(1));

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Base_or_class_modification_restrictions_refuse_transmutation(int hash) => Assert.Null(Apply(Input(hash)));

    [Fact]
    public void An_unknown_base_is_refused_instead_of_creating_an_empty_magic_item() =>
        Assert.Null(Apply(Input() with { TypeHash = 0xdeadbeef }));

    [Fact]
    public void An_empty_eligible_pool_is_refused() => Assert.Null(Apply(Input(4)));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Direct_transmutation_refuses_corrupted_and_mirrored_items(bool corrupted, bool mirrored) =>
        Assert.Null(Apply(Input() with { Corrupted = corrupted, Mirrored = mirrored }));

    [Theory]
    [InlineData(ItemRarity.Magic)]
    [InlineData(ItemRarity.Rare)]
    [InlineData(ItemRarity.Unique)]
    public void Non_normal_items_are_refused(ItemRarity rarity) => Assert.Null(Apply(Input() with { Rarity = rarity }));

    [Fact]
    public void Both_the_item_and_its_base_must_support_mods()
    {
        Assert.Null(Apply(Input() with { Components = ["Base"] }));
        Assert.Null(Apply(Input(5) with { Components = ["Base", "Mods"] }));
    }

    [Fact]
    public void Ordinary_upgrades_match_the_coe_count_distribution_and_native_weights()
    {
        // At level 1 the independent fixture has prefix 101 (weight 100), suffix 102 (300).
        // CoE beta affixNumberGeneration[2] is [.5,.5]; among single-affix rolls the prefix therefore has 25% weight.
        var random = new Random(317);
        int singles = 0, prefixes = 0;
        for (int i = 0; i < 10000; i++)
        {
            var after = Apply(Input() with { Identified = false }, random)!;
            Assert.NotNull(after);
            Assert.Equal(ItemRarity.Magic, after.Rarity);
            Assert.True(after.Identified);
            Assert.Empty(after.NameWords);
            Assert.InRange(after.Mods.Count, 1, 2);
            Assert.All(after.Mods, m =>
            {
                Assert.Equal(ModSection.Explicit, m.Section);
                Assert.Contains(m.Hash, new ushort[] { 101, 102 });
                Assert.InRange(Assert.Single(m.Values), 10, 20);
            });
            if (after.Mods.Count == 1)
            {
                singles++;
                if (after.Mods[0].Hash == 101) prefixes++;
            }
            else
                Assert.Equal(new ushort[] { 101, 102 }, after.Mods.Select(m => m.Hash));
        }
        Assert.InRange(singles, 4800, 5200);
        Assert.InRange((double)prefixes / singles, .22, .28);
    }

    [Fact]
    public void Higher_item_levels_unlock_tiers_but_not_essence_zero_weight_or_wrong_domain_mods()
    {
        var random = new Random(5);
        var hashes = Enumerable.Range(0, 200).SelectMany(_ => Apply(Input() with { ItemLevel = 10 }, random)!.Mods)
            .Select(m => m.Hash).ToHashSet();
        Assert.Contains((ushort)104, hashes);
        Assert.DoesNotContain((ushort)105, hashes);
        Assert.DoesNotContain((ushort)106, hashes);
        Assert.DoesNotContain((ushort)107, hashes);
    }

    [Fact]
    public void A_prefix_only_pool_produces_one_valid_affix_instead_of_a_duplicate_or_refusal()
    {
        var random = new Random(6);
        for (int i = 0; i < 100; i++)
            Assert.Equal((ushort)101, Assert.Single(Apply(Input(6), random)!.Mods).Hash);
    }

    [Fact]
    public void Prefixes_and_suffixes_cannot_share_a_modifier_family()
    {
        var random = new Random(9);
        bool sawConflictingSuffix = false;
        for (int i = 0; i < 200; i++)
        {
            var mods = Apply(Input(7), random)!.Mods;
            sawConflictingSuffix |= mods.Any(m => m.Hash == 103);
            if (mods.Any(m => m.Hash == 103)) Assert.DoesNotContain(mods, m => m.Hash == 101);
        }
        Assert.True(sawConflictingSuffix);
    }

    [Fact]
    public void Upgrading_preserves_non_explicit_mods_quality_sockets_gems_links_and_charges()
    {
        var before = Input() with
        {
            Quality = 17, Charges = 23, Identified = false,
            Sockets = [new(SocketColour.Blue, new Item(42, ["Base"], 0))], Links = [1],
            Mods = [new(ModSection.Implicit, 108, [15]), new(ModSection.Enchant, 108, [19])],
        };
        var after = Apply(before)!;
        Assert.Equal(before, after with { Rarity = before.Rarity, Identified = before.Identified, Mods = before.Mods,
            NameWords = before.NameWords });
        Assert.Equal(before.Mods, after.Mods.Where(m => m.Section != ModSection.Explicit));
        Assert.InRange(after.Mods.Count(m => m.Section == ModSection.Explicit), 1, 2);
    }
}
