using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ItemRollsTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly Lazy<(ItemCatalog Catalog, ItemRolls Rolls)> Setup = new(() =>
    {
        var catalog = new ItemCatalog(Data());
        return (catalog, new ItemRolls(catalog, Data()));
    });

    static ItemCatalog Catalog => Setup.Value.Catalog;
    static ItemRolls Rolls => Setup.Value.Rolls;
    static ItemCatalog.BaseType Wand => Catalog.Row(384)!;

    [Fact]
    public void A_wand_has_its_chain_tags()
    {
        Assert.Superset(new HashSet<string> { "default", "weapon", "onehand", "wand" }, new HashSet<string>(Wand.Tags));
    }

    [Fact]
    public void The_pool_holds_only_affixes_of_the_bases_domain_up_to_the_item_level()
    {
        var pool = Rolls.Pool(Wand, 10);
        Assert.NotEmpty(pool);
        Assert.All(pool, p =>
        {
            Assert.Equal(Wand.ModDomain, p.Mod.Domain);
            Assert.InRange(p.Mod.Level, 0, 10);
            Assert.Contains(p.Mod.GenerationType, new[] { ItemRolls.Prefix, ItemRolls.Suffix });
            Assert.True(p.Weight > 0);
        });
        // Higher item levels open higher tiers.
        Assert.True(Rolls.Pool(Wand, 80).Count > pool.Count);
    }

    [Fact]
    public void Flasks_roll_flask_affixes()
    {
        var flask = Catalog.Find("Metadata/Items/Flasks/FlaskLife1")!;
        var pool = Rolls.Pool(flask, 20);
        Assert.NotEmpty(pool);
        Assert.All(pool, p => Assert.Equal(flask.ModDomain, p.Mod.Domain));
        Assert.NotEqual(Wand.ModDomain, flask.ModDomain);
    }

    [Fact]
    public void Magic_items_get_one_or_two_affixes_at_most_one_of_each_kind()
    {
        var rng = new Random(1);
        for (int i = 0; i < 200; i++)
        {
            var item = Rolls.Roll(ItemCatalog.Create(Wand) with { ItemLevel = 60 }, ItemRarity.Magic, rng);
            var kinds = Explicits(item).Select(m => m.GenerationType).ToList();
            Assert.Equal(ItemRarity.Magic, item.Rarity);
            Assert.InRange(kinds.Count, 1, 2);
            Assert.Equal(kinds.Count, kinds.Distinct().Count());
            Assert.Empty(item.NameWords);
        }
    }

    [Fact]
    public void Rare_items_get_four_to_six_affixes_a_name_and_keep_their_implicit()
    {
        var rng = new Random(2);
        for (int i = 0; i < 200; i++)
        {
            var item = Rolls.Roll(ItemCatalog.Create(Wand) with { ItemLevel = 70 }, ItemRarity.Rare, rng);
            var mods = Explicits(item);
            Assert.InRange(mods.Count, 4, 6);
            Assert.True(mods.Count(m => m.GenerationType == ItemRolls.Prefix) <= 3);
            Assert.True(mods.Count(m => m.GenerationType == ItemRolls.Suffix) <= 3);
            Assert.All(mods, m => Assert.True(m.Level <= 70));
            // One mod per family.
            var families = mods.SelectMany(m => m.Families).ToList();
            Assert.Equal(families.Count, families.Distinct().Count());
            Assert.Single(item.Mods, m => m.Section == ModSection.Implicit);
            Assert.Equal(2, item.NameWords.Count);
        }
    }

    [Fact]
    public void Blessed_orb_rerolls_only_the_implicit_values_within_their_tier()
    {
        var rng = new Random(4);
        var item = Rolls.Roll(ItemCatalog.Create(Wand) with { ItemLevel = 70 }, ItemRarity.Rare, rng);
        var implicitBefore = item.Mods.Single(m => m.Section == ModSection.Implicit);
        var row = Catalog.Mods.ByHash(implicitBefore.Hash)!;
        var seen = new HashSet<int>();
        for (int i = 0; i < 50; i++)
        {
            var blessed = Rolls.Bless(item, rng)!;
            var after = blessed.Mods.Single(m => m.Section == ModSection.Implicit);
            Assert.Equal(implicitBefore.Hash, after.Hash);   // the same mod and tier
            Assert.Equal(item.Mods.Where(m => m.Section != ModSection.Implicit), blessed.Mods.Where(m => m.Section != ModSection.Implicit));
            Assert.InRange(after.Values[0], Math.Min(row.Stats[0].Min, row.Stats[0].Max), Math.Max(row.Stats[0].Min, row.Stats[0].Max));
            seen.Add(after.Values[0]);
        }
        Assert.True(seen.Count > 1);   // the values do change
        Assert.Null(Rolls.Bless(item with { Mods = [.. item.Mods.Where(m => m.Section != ModSection.Implicit)] }, rng));   // no implicit
    }

    [Fact]
    public void Rolled_values_stay_in_the_mods_ranges()
    {
        var item = Rolls.Roll(ItemCatalog.Create(Wand) with { ItemLevel = 70 }, ItemRarity.Rare, new Random(3));
        foreach (var m in item.Mods)
        {
            var row = Catalog.Mods.ByHash(m.Hash)!;
            Assert.Equal(row.Stats.Count, m.Values.Count);
            for (int s = 0; s < row.Stats.Count; s++)
                Assert.InRange(m.Values[s], Math.Min(row.Stats[s].Min, row.Stats[s].Max), Math.Max(row.Stats[s].Min, row.Stats[s].Max));
        }
    }

    [Fact]
    public void The_rare_name_survives_a_save()
    {
        var item = Rolls.Roll(ItemCatalog.Create(Wand) with { ItemLevel = 70 }, ItemRarity.Rare, new Random(4));
        var back = CharacterSaves.FromSaved(CharacterSaves.ToSaved(item, Catalog.Mods), Catalog)!;
        Assert.Equal(item.NameWords, back.NameWords);
        Assert.DoesNotContain("[", Rolls.NameOf(back.NameWords));
    }

    static List<ModCatalog.Mod> Explicits(Item item) =>
        [.. item.Mods.Where(m => m.Section == ModSection.Explicit).Select(m => Catalog.Mods.ByHash(m.Hash)!)];
}
