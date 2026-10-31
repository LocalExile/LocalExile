using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class KingInMistsTests
{
    static readonly Lazy<string> DataFolder = new(() => BenchCraftingTests.Data());
    static readonly Lazy<MonsterCatalog> Monsters = new(() => new MonsterCatalog(DataFolder.Value));

    [Fact]
    public void Level_83_map_boss_scaling_matches_the_king_in_the_mists_life()
    {
        var boss = Monsters.Value.Find(KingInMists.Boss2);
        Assert.NotNull(boss);
        Assert.True(KingInMists.IsBoss2(boss));
        Assert.False(KingInMists.IsBoss3(boss));

        var scaling = Monsters.Value.MapBossScaling(83);
        Assert.Equal(20.73, scaling.Life, 2);
        Assert.Equal(1.15, scaling.Damage, 2);

        int baseLife = Monsters.Value.BaseLife(boss!, 83)!.Value;
        Assert.Equal(275_742, baseLife);
        long life = (long)Math.Round(MonsterCatalog.LifeWithRarity(baseLife, MonsterRarity.Unique) * scaling.Life);
        Assert.InRange(life, 45_614_000, 45_615_000);
    }

    [Fact]
    public void The_treant_is_a_separate_supported_form_with_its_own_health_pool()
    {
        var treant = Monsters.Value.Find(KingInMists.Boss3);
        Assert.NotNull(treant);
        Assert.True(KingInMists.IsBoss3(treant));
        Assert.NotEqual(KingInMists.Boss2, treant!.Id);
        Assert.True(treant.BossHealthBar);

        int baseLife = Monsters.Value.BaseLife(treant, 83)!.Value;
        long life = (long)Math.Round(MonsterCatalog.LifeWithRarity(baseLife, MonsterRarity.Unique) *
            Monsters.Value.MapBossScaling(83).Life);
        Assert.InRange(life, 26_608_000, 26_609_000);
    }

    [Fact]
    public void The_mystic_fetish_is_a_supported_stationary_shrine()
    {
        var fetish = Monsters.Value.Find(KingInMists.MysticFetish);
        Assert.NotNull(fetish);
        Assert.True(KingInMists.IsFetish(fetish));
        Assert.False(KingInMists.IsBoss(fetish));
        Assert.True(fetish!.Supported);
        Assert.Equal(0, fetish.MovementSpeed);
        Assert.Contains("StateMachine", fetish.ComponentsOrBase);
        Assert.NotNull(fetish.StateMachine);
        Assert.Contains("active", fetish.StateMachine!.Names!);
    }

    [Fact]
    public void The_humanoid_cannot_be_pushed_below_30_percent_before_the_maze()
    {
        var monster = new SpawnedMonster("The King in the Mists", new WalkEstimator(10, 10, 30, null, 300, 300), 135, 4, 1000)
        {
            Life = 1000, KingPhaseLocked = true,
        };
        var (life, starts) = Program.TakeDamageLocked(monster, 900);
        Assert.Equal(300u, life);
        Assert.False(starts);
        monster.Life = life;
        (life, _) = Program.TakeDamageLocked(monster, 500);
        Assert.Equal(300u, life);
    }

    [Fact]
    public void The_treant_emergence_grace_refuses_damage()
    {
        var monster = new SpawnedMonster("The King in the Mists", new WalkEstimator(10, 10, 30, null, 300, 300), 135, 4, 1000)
        {
            Life = 700, KingGrace = true,
        };
        var (life, starts) = Program.TakeDamageLocked(monster, 500);
        Assert.Equal(700u, life);
        Assert.False(starts);
    }

    [Fact]
    public void The_unique_drop_pool_always_gives_exactly_one_of_the_four_uniques()
    {
        var items = new ItemCatalog(DataFolder.Value);
        var uniques = new UniqueCatalog(items, DataFolder.Value);
        var drops = new ItemDrops(items, new ItemRolls(items), DataFolder.Value, uniques: uniques);
        var counts = new Dictionary<uint, int>();
        var extras = new HashSet<string>(StringComparer.Ordinal);
        var random = new Random(12345);
        int paired = 0;
        for (int i = 0; i < 1000; i++)
        {
            var rolled = drops.KingInMists(83, random);
            var unique = rolled.Where(d => d.Item.Rarity == ItemRarity.Unique).ToList();
            var primary = unique.Where(d => d.Type.Id != "Metadata/Items/Jewels/JewelStr").ToList();
            Assert.Single(primary);
            counts[primary[0].Item.TypeHash] = counts.GetValueOrDefault(primary[0].Item.TypeHash) + 1;
            bool light = primary[0].Type.Id == "Metadata/Items/Jewels/JewelPrismatic";
            int taken = unique.Count(d => d.Type.Id == "Metadata/Items/Jewels/JewelStr");
            Assert.Equal(light ? 1 : 0, taken);
            if (light) paired++;
            foreach (var drop in rolled.Where(d => d.Item.Rarity != ItemRarity.Unique)) extras.Add(drop.Type.Name);
        }
        Assert.Equal(4, counts.Count);
        Assert.All(counts.Values, count => Assert.True(count > 0));
        Assert.True(paired > 150);
        Assert.Contains("Hextoad Support", extras);
        Assert.Contains("Hexpass Support", extras);
        Assert.Contains("The Endless Darkness", extras);
    }

    [Fact]
    public void That_which_was_taken_rolls_four_distinct_charm_modifiers()
    {
        var items = new ItemCatalog(DataFolder.Value);
        var uniques = new UniqueCatalog(items, DataFolder.Value);
        var taken = uniques.Create("That Which Was Taken", new Random(7));
        Assert.NotNull(taken);
        Assert.Equal(ItemRarity.Unique, taken!.Rarity);
        Assert.NotEmpty(taken.NameWords);
        Assert.Equal("Metadata/Items/Jewels/JewelStr", items.ByHash(taken.TypeHash)!.Id);
        var explicitMods = taken.Mods.Where(m => m.Section == ModSection.Explicit).ToList();
        Assert.Equal(4, explicitMods.Count);
        Assert.All(explicitMods, m => Assert.Equal(35, items.Mods.ByHash(m.Hash)!.Domain));
        Assert.Equal(4, explicitMods.Select(m => items.Mods.ByHash(m.Hash)!.Families.FirstOrDefault()).Distinct().Count());
    }
}
