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

    static readonly Lazy<IReadOnlyList<ThemePool>> PoolsLazy = new(LoadPools);
    public static IReadOnlyList<ThemePool> Pools => PoolsLazy.Value;

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
        var stats = new Dictionary<string, int>(StringComparer.Ordinal);
        if (CrucibleTables.Current.MoreLife(areaLevel) is int life and not 0) stats[CrucibleTables.LevelMoreLifeStat] = life;
        if (moreDamage != 0) stats[CrucibleTables.MonsterMoreDamageStat] = moreDamage;
        if (damageTaken != 0) stats[CrucibleTables.MonsterDamageTakenStat] = damageTaken;
        return new MonsterModSet([], [], new MonsterModEffects(stats));
    }

    /// <summary>
    /// Rolls a monster pack for one Forge of the Titans spawner node using <c>CrucibleEndgameMonsterPacks.dat</c>.
    /// </summary>
    public static List<(string VarietyId, MonsterRarity Rarity)> RollEndgamePack(
        CrucibleTables tables, ThemePool theme, bool uber, int moreUniquePercent, Random random)
    {
        var packs = tables.EndgamePacks;
        if (packs.Count == 0 || theme.Normal.Count == 0) return [];

        // Weighted pick across the 8 tiers; moreUniquePercent boosts boss tiers (Tier8/Tier9).
        int totalWeight = 0;
        var weights = new int[packs.Count];
        for (int i = 0; i < packs.Count; i++)
        {
            int w = Math.Max(1, packs[i].Weight);
            if (packs[i].IsBossTier && moreUniquePercent > 0)
                w = w * (100 + moreUniquePercent) / 100;
            if (uber && packs[i].IsBossTier)
                w = w * 3 / 2;
            weights[i] = w;
            totalWeight += w;
        }

        int roll = random.Next(totalWeight);
        var chosen = packs[^1];
        for (int i = 0; i < packs.Count; i++)
        {
            if (roll < weights[i]) { chosen = packs[i]; break; }
            roll -= weights[i];
        }

        var result = new List<(string, MonsterRarity)>();
        if (chosen.IsBossTier && theme.Bosses.Count > 0)
        {
            string boss = theme.Bosses[random.Next(theme.Bosses.Count)];
            result.Add((boss, MonsterRarity.Unique));
            // Boss packs get a small escort of normal/magic monsters.
            for (int k = 0; k < 2; k++)
                result.Add((theme.Normal[random.Next(theme.Normal.Count)], MonsterRarity.Magic));
            return result;
        }

        int count = Math.Max(2, chosen.MinPack + random.Next(2));
        // Higher tiers (lower row index in CrucibleEndgameMonsterPacks) spawn rarer monsters.
        MonsterRarity leadRarity = chosen.Row <= 3 ? MonsterRarity.Rare
            : chosen.Row <= 5 ? MonsterRarity.Magic
            : MonsterRarity.Normal;

        for (int k = 0; k < count; k++)
        {
            string variety = theme.Normal[random.Next(theme.Normal.Count)];
            var rarity = k == 0 ? leadRarity : (leadRarity == MonsterRarity.Rare && k == 1 ? MonsterRarity.Magic : MonsterRarity.Normal);
            result.Add((variety, rarity));
        }
        return result;
    }

    /// <summary>Finds a supported <see cref="MonsterCatalog.Variety"/> from a variety path list, falling back to any supported Crucible variety.</summary>
    public static MonsterCatalog.Variety? ResolveVariety(string preferredId, ThemePool fallbackTheme)
    {
        if (Monsters.Find(preferredId) is { Supported: true } direct) return direct;
        foreach (string id in fallbackTheme.Normal)
            if (Monsters.Find(id) is { Supported: true } found) return found;
        foreach (var pool in Pools)
            foreach (string id in pool.Normal)
                if (Monsters.Find(id) is { Supported: true } found) return found;
        return null;
    }
}
