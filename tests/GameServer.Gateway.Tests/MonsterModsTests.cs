using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

/// <summary>Magic and rare monster mods (MonsterMods.cs, 14-monster-packs.md §5): the roll rules against the extracted
/// Mods.dat, the effects on the server's numbers, the hooks the hits use, persistence and the unsupported mods.</summary>
public class MonsterModsTests
{
    static string Data()
    {
        for (var dir = new DirectoryInfo(TestEnvironment.RuntimeRoot); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tables"))) return Path.Combine(dir.FullName, "data");
        throw new DirectoryNotFoundException("data/tables");
    }

    static readonly Lazy<(ModCatalog Mods, MonsterCatalog Monsters, MonsterModCatalog Rolls)> Setup = new(() =>
    {
        var mods = new ModCatalog(Data());
        return (mods, new MonsterCatalog(Data()), new MonsterModCatalog(mods, Data()) { RollUnsupported = false });
    });

    static ModCatalog Mods => Setup.Value.Mods;
    static MonsterModCatalog Rolls => Setup.Value.Rolls;
    static MonsterCatalog.Variety Variety(int row) => Setup.Value.Monsters.Find(row.ToString())!;
    static MonsterCatalog.Variety Corpse => Variety(1159);    // Hungry Corpse: undead, zombie, physical_affinity …
    static MonsterCatalog.Variety Crab => Variety(4515);      // Savage Crab: beast, not_dex, not_int …
    static MonsterCatalog.Variety Cannibal => Variety(2842);  // Cannibal: not_str, ranged …
    static ModCatalog.Mod Mod(string id) => Mods.ById(id)!;

    static List<ModCatalog.Mod> Affixes(MonsterModSet set) => [.. set.Affixes.Select(a => Mods.ByHash(a.Hash)!)];

    static MonsterModSet With(MonsterRarity rarity, int level, params string[] affixes) =>
        Rolls.Roll(Corpse, level, rarity, new Random(0), alone: true, count: affixes.Length, forced: [.. affixes.Select(Mod)]);

    // ---- The pool ----

    [Fact]
    public void The_pool_holds_monster_domain_affixes_up_to_the_level_with_a_weight_for_the_tags()
    {
        var tags = Rolls.TagsOf(Corpse, MonsterRarity.Rare, alone: false);
        var pool = Rolls.Pool(tags, 20);
        Assert.NotEmpty(pool);
        Assert.All(pool, p =>
        {
            Assert.Equal(MonsterModCatalog.Domain, p.Mod.Domain);
            Assert.Contains(p.Mod.GenerationType, new[] { 1, 2 });
            Assert.InRange(p.Mod.Level, 0, 20);
            Assert.Equal(p.Mod.WeightFor(tags), p.Weight);
            Assert.True(p.Weight > 0);
        });
        Assert.Contains(pool, p => p.Mod.Id == "MonsterModIncreasedLife" && p.Weight == 8000);
        // Level 24 mods (of the Hydra) open later.
        Assert.DoesNotContain(pool, p => p.Mod.Id == "MonsterModLifeRegeneration");
        Assert.Contains(Rolls.Pool(tags, 24), p => p.Mod.Id == "MonsterModLifeRegeneration");
    }

    // ---- Counts, families, weights ----

    // ---- Rarity's own mods and the rare name ----

    // ---- Effects ----

    // ---- The hooks the hits use ----

    static SpawnedMonster Monster(MonsterModSet mods, uint life = 1000, IReadOnlyList<HitAnimation>? clips = null) =>
        new("Corpse@60", new WalkEstimator(10, 10, 30, null, 100, 100), 60, 3, life)
        { Mods = mods, Life = life, Variety = Corpse, Level = 60, HitAnimations = clips ?? [] };

    // ---- Client stats and blob ----

    // ---- Persistence ----

    // ---- Unsupported mods ----

}
