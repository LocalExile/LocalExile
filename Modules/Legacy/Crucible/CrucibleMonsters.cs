using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Crucible;

/// <summary>
/// Crucible monster themes (<c>CrucibleTags</c> -> <c>Metadata/Monsters/LeagueCrucible/*</c>), their tiers, and the
/// pack pools for Forge of the Titans.
/// </summary>
public static class CrucibleMonsters
{
    public sealed record ThemePool(string Tag, IReadOnlyList<string> Normal, IReadOnlyList<string> Bosses);

    // CrucibleTags (0..5: Fire, Cold, Physical, Lightning, Chaos, Demon) to MonsterVarieties folders. A guess by name:
    // no table links the two; only CriticalSpeed's monsters carry a matching tag (demon).
    static readonly (string Tag, string Folder)[] ThemeFolders =
    [
        ("Fire", "Metadata/Monsters/LeagueCrucible/Fire/"),
        ("Cold", "Metadata/Monsters/LeagueCrucible/Cold/"),
        ("Physical", "Metadata/Monsters/LeagueCrucible/Physical/"),
        ("Lightning", "Metadata/Monsters/LeagueCrucible/Lightning/"),
        ("Chaos", "Metadata/Monsters/LeagueCrucible/Chaos/"),
        ("Demon", "Metadata/Monsters/LeagueCrucible/CriticalSpeed/"),
    ];

    public static IReadOnlyList<ThemePool> Pools { get; private set; } = [];
    public static void LoadGameData() => Pools = LoadPools();

    static IReadOnlyList<ThemePool> LoadPools()
    {
        var varieties = ClientTables.TableOrEmpty("MonsterVarieties")
            .Select(r => r.Text("Id") ?? "")
            .Where(id => id.StartsWith("Metadata/Monsters/LeagueCrucible/", StringComparison.Ordinal))
            .Where(id => !id.Contains("Daemon", StringComparison.Ordinal)
                && !id.Contains("Volatile", StringComparison.Ordinal)
                && !id.Contains("Pylon", StringComparison.Ordinal)
                && !id.Contains("/Minions/", StringComparison.Ordinal))
            .ToList();

        var result = new List<ThemePool>(ThemeFolders.Length);
        foreach (var (tag, folder) in ThemeFolders)
        {
            var matching = varieties.Where(id => id.StartsWith(folder, StringComparison.Ordinal)).ToList();
            var bosses = matching.Where(id => id.Contains("Boss", StringComparison.Ordinal)).ToList();
            var normal = matching.Where(id => !id.Contains("Boss", StringComparison.Ordinal)
                && !id.Contains("AtlasUber", StringComparison.Ordinal)).ToList();
            result.Add(new(tag, normal, bosses));
        }
        return result;
    }

    /// <summary>Picks a supported theme pool (by CrucibleTags index, or random when out of range).</summary>
    public static ThemePool ThemeFor(int tagIndex, Random random)
    {
        if (Pools.Count == 0) return new("Fire", [], []);
        if ((uint)tagIndex < (uint)Pools.Count && Pools[tagIndex].Normal.Count > 0) return Pools[tagIndex];
        var nonEmpty = Pools.Where(p => p.Normal.Count > 0).ToList();
        return nonEmpty.Count > 0 ? nonEmpty[random.Next(nonEmpty.Count)] : Pools[0];
    }

    /// <summary>The theme's monster of a tier: the folder's table order is the tier order, tiers 9 and 10 are its
    /// two bosses.</summary>
    public static MonsterCatalog.Variety? VarietyForTier(ThemePool theme, int tier)
    {
        string? id = tier > theme.Normal.Count
            ? theme.Bosses.Count == 0 ? null : theme.Bosses[Math.Min(tier - theme.Normal.Count, theme.Bosses.Count) - 1]
            : theme.Normal.Count == 0 ? null : theme.Normal[Math.Max(1, tier) - 1];
        return id is not null && Monsters.Find(id) is { Supported: true } found ? found : null;
    }

    /// <summary>A Crucible monster's own stats: the more life of <c>CrucibleLifeScalingPerLevel</c>, and the Forge's more
    /// damage and less damage taken ("more damage and damage reduction", GGG Q&amp;A).</summary>
    public static MonsterModSet ScalingMods(int areaLevel, int moreDamage = 0, int damageTaken = 0)
    {
        var stats = new Dictionary<uint, int>();
        if (CrucibleTables.Current.MoreLife(areaLevel) is int life and not 0) stats[CrucibleTables.LevelMoreLifeStat] = life;
        if (moreDamage != 0) stats[CrucibleTables.MonsterMoreDamageStat] = moreDamage;
        if (damageTaken != 0) stats[CrucibleTables.MonsterDamageTakenStat] = damageTaken;
        return new MonsterModSet([], [], new MonsterModEffects(stats));
    }

    /// <summary>One pack of Forge of the Titans: a row of <c>CrucibleEndgameMonsterPacks</c> by its weight, and the
    /// theme's monsters it counts. The theme's two bosses are Unique, the rest Normal.</summary>
    public static List<(MonsterCatalog.Variety Variety, MonsterRarity Rarity)> RollEndgamePack(
        CrucibleTables tables, ThemePool theme, Random random)
    {
        var result = new List<(MonsterCatalog.Variety, MonsterRarity)>();
        int roll = random.Next(Math.Max(1, tables.EndgamePacks.Sum(p => p.Weight)));
        foreach (var pack in tables.EndgamePacks)
        {
            if ((roll -= pack.Weight) >= 0) continue;
            for (int i = 0; i < pack.TierCounts.Length; i++)
                for (int n = 0; n < pack.TierCounts[i]; n++)
                    if (VarietyForTier(theme, i + 1) is { } variety)
                        result.Add((variety, i >= theme.Normal.Count ? MonsterRarity.Unique : MonsterRarity.Normal));
            break;
        }
        return result;
    }
}
