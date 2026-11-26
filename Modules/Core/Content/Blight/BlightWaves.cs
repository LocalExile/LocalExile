using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Blight;

/// <summary>The six Blight tower families. Lanes of a matching elemental/physical/minion theme resist that tower family.</summary>
public enum TowerFamily { None, Cold, Lightning, Buff, Physical, Minion, Fire }

/// <summary>
/// Lane encounter types, monster pools, boss lanes (<c>Blightspawn</c>, <c>Blooming</c>, <c>Indigo Oil</c>),
/// and mathematically exact Blight boss life scaling (<c>MonsterMapBossDifficulty + BlightBossLifeScalingPerLevel</c>).
/// </summary>
public static class BlightWaves
{
    /// <summary>How many pump durability points a leaking boss costs (normal/magic/rare cost 1).</summary>
    public const int BossDurabilityLoss = 5;

    static readonly string[] FallbackVarieties =
    [
        "Metadata/Monsters/LeagueBlight/Cold/BlightColdWyvern",
        "Metadata/Monsters/LeagueBlight/Generic/Pitbull",
        "Metadata/Monsters/LeagueBlight/Cold/BlightWaterElemental",
        "Metadata/Monsters/LeagueBlight/Generic/Cockerel",
        "Metadata/Monsters/LeagueBlight/Generic/PitbullSlow",
        "Metadata/Monsters/LeagueBlight/Generic/GladiatorSword",
        "Metadata/Monsters/LeagueBlight/Generic/CockerelSlow",
        "Metadata/Monsters/LeagueBlight/Generic/GladiatorSwordShield",
        "Metadata/Monsters/LeagueBlight/Generic/CockerelBow",
    ];

    static readonly Dictionary<TowerFamily, string[]> FamilyVarieties = new()
    {
        [TowerFamily.Cold] =
        [
            "Metadata/Monsters/LeagueBlight/Cold/BlightWaterElemental",
            "Metadata/Monsters/LeagueBlight/Cold/BlightWaterElementalSponge",
            "Metadata/Monsters/LeagueBlight/Cold/BlightIceElemental",
            "Metadata/Monsters/LeagueBlight/Cold/BlightWaterSpiral",
            "Metadata/Monsters/LeagueBlight/Cold/BlightMossMonsterIce",
            "Metadata/Monsters/LeagueBlight/Cold/BlightColdWyvern",
            "Metadata/Monsters/LeagueBlight/Cold/BlightColdAlphaWolf",
        ],
        [TowerFamily.Lightning] =
        [
            "Metadata/Monsters/LeagueBlight/Lightning/BlightLightningSkeleton1",
            "Metadata/Monsters/LeagueBlight/Lightning/BlightLightningSkeleton2",
            "Metadata/Monsters/LeagueBlight/Lightning/BlightLightningSkeleton3",
            "Metadata/Monsters/LeagueBlight/Lightning/BlightLightningChargedSkeleton1",
            "Metadata/Monsters/LeagueBlight/Lightning/BlightLightningChargedSkeleton2",
            "Metadata/Monsters/LeagueBlight/Lightning/BlightLightningCasterSkeleton",
            "Metadata/Monsters/LeagueBlight/Lightning/BlightLightningCageSkeleton",
        ],
        [TowerFamily.Physical] =
        [
            "Metadata/Monsters/LeagueBlight/Physical/MinerHammerSlow",
            "Metadata/Monsters/LeagueBlight/Physical/MinerPickaxeSlow",
            "Metadata/Monsters/LeagueBlight/Physical/MinerHammer",
            "Metadata/Monsters/LeagueBlight/Physical/MinerPickaxe",
            "Metadata/Monsters/LeagueBlight/Physical/MinerLantern",
        ],
        [TowerFamily.Minion] =
        [
            "Metadata/Monsters/LeagueBlight/Minion/BlightMinionSpiderSlow_",
            "Metadata/Monsters/LeagueBlight/Minion/BlightMinionSpiderSlow2",
            "Metadata/Monsters/LeagueBlight/Minion/BlightMinionSpiderMixed1",
            "Metadata/Monsters/LeagueBlight/Minion/BlightMinionSpiderMixed2_",
            "Metadata/Monsters/LeagueBlight/Minion/BlightMinionSpiderMixed3",
            "Metadata/Monsters/LeagueBlight/Minion/BlightMinionSpiderFast1",
            "Metadata/Monsters/LeagueBlight/Minion/BlightMinionSpiderFast2",
        ],
        [TowerFamily.Fire] =
        [
            "Metadata/Monsters/LeagueBlight/Fire/BlightFireHellion",
            "Metadata/Monsters/LeagueBlight/Fire/BlightFireHellion2",
            "Metadata/Monsters/LeagueBlight/Fire/BlightFireHellionExplode_",
            "Metadata/Monsters/LeagueBlight/Fire/BlightFireHound",
            "Metadata/Monsters/LeagueBlight/Fire/BlightFireHarpy",
            "Metadata/Monsters/LeagueBlight/Fire/BlightFireElemental",
            "Metadata/Monsters/LeagueBlight/Fire/BlightFireMonster",
            "Metadata/Monsters/LeagueBlight/Fire/BlightFirePyromaniac",
        ],
        [TowerFamily.None] = FallbackVarieties,
    };

    static readonly Dictionary<TowerFamily, string[]> FamilyBosses = new()
    {
        [TowerFamily.Cold] =
        [
            "Metadata/Monsters/LeagueBlight/Cold/BlightMossMonsterIceBoss_",
            "Metadata/Monsters/LeagueBlight/Cold/BlightColdAlphaWolfBoss",
        ],
        [TowerFamily.Lightning] =
        [
            "Metadata/Monsters/LeagueBlight/Lightning/BlightLightningNecromancerBoss_",
        ],
        [TowerFamily.Physical] =
        [
            "Metadata/Monsters/LeagueBlight/Physical/BlightGolemBoss",
        ],
        [TowerFamily.Minion] =
        [
            "Metadata/Monsters/LeagueBlight/Minion/BlightMinionSpiderFastMother",
        ],
        [TowerFamily.Fire] =
        [
            "Metadata/Monsters/LeagueBlight/Fire/BlightFireHellionBoss",
            "Metadata/Monsters/LeagueBlight/Fire/BlightFireMonsterBoss",
            "Metadata/Monsters/LeagueBlight/Fire/BlightFirePyromaniacBoss",
        ],
        [TowerFamily.None] =
        [
            "Metadata/Monsters/LeagueBlight/Generic/BlightGenericZombie4Boss",
            "Metadata/Monsters/LeagueBlight/Generic/BlightGenericRhoaSkeletonBoss",
            "Metadata/Monsters/LeagueBlight/Generic/BlightGenericDemonModularBoss_",
            "Metadata/Monsters/LeagueBlight/Generic/GoatmanBossLeaper",
            "Metadata/Monsters/LeagueBlight/Generic/BullBoss",
        ],
    };

    /// <summary>The tower family that a BlightEncounterTypes row resists (-99% tower damage / control immunity), or None for generic lanes.</summary>
    public static TowerFamily ImmuneFamilyOf(int encounterTypeRow)
    {
        if (encounterTypeRow < 0 || encounterTypeRow >= BlightTables.EncounterTypes.Count) return TowerFamily.None;
        string id = BlightTables.EncounterTypes[encounterTypeRow].Id;
        if (id.StartsWith("Cold", StringComparison.Ordinal) || id == "BossOnlyCold") return TowerFamily.Cold;
        if (id.StartsWith("Lightning", StringComparison.Ordinal) || id == "BossOnlyLightning") return TowerFamily.Lightning;
        if (id.StartsWith("Physical", StringComparison.Ordinal) || id == "BossOnlyPhysical") return TowerFamily.Physical;
        if (id.StartsWith("Minion", StringComparison.Ordinal) || id == "BossOnlyMinion") return TowerFamily.Minion;
        if (id.StartsWith("Fire", StringComparison.Ordinal) || id == "BossOnlyFire") return TowerFamily.Fire;
        return TowerFamily.None;
    }

    /// <summary>Rolls an encounter type row per lane from weighted BlightEncounterTypes (converting Blightspawn boss-only lanes via __col4).</summary>
    public static Dictionary<int, int> PickLaneTypes(int laneCount, int bossOnlyLaneCount, Random rng, out HashSet<int> bossOnlyLanes)
    {
        var result = new Dictionary<int, int>();
        bossOnlyLanes = [];
        var weighted = BlightTables.EncounterTypes.Where(e => e.Weight > 0).ToList();
        if (weighted.Count == 0)
        {
            for (int i = 0; i < laneCount; i++) result[i] = 0;
            return result;
        }
        int totalWeight = weighted.Sum(e => e.Weight);
        for (int i = 0; i < laneCount; i++)
        {
            int roll = rng.Next(totalWeight);
            var picked = weighted[0];
            foreach (var e in weighted)
                if ((roll -= e.Weight) < 0) { picked = e; break; }
            result[i] = picked.Row;
        }
        // Atlas Notable Blightspawn (map_blight_has_X_boss_lanes): convert X lanes into their BossOnly* counterpart via __col4.
        if (bossOnlyLaneCount > 0 && laneCount > 0)
        {
            var candidates = Enumerable.Range(0, laneCount).ToList();
            for (int k = 0; k < Math.Min(bossOnlyLaneCount, laneCount) && candidates.Count > 0; k++)
            {
                int pick = rng.Next(candidates.Count);
                int lane = candidates[pick];
                candidates.RemoveAt(pick);
                bossOnlyLanes.Add(lane);
                int baseRow = result[lane];
                if (baseRow >= 0 && baseRow < BlightTables.EncounterTypes.Count
                    && BlightTables.EncounterTypes[baseRow].BossTypeRow is >= 0 and int bossRow)
                    result[lane] = bossRow;
            }
        }
        return result;
    }

    /// <summary>Picks which lanes spawn Blight bosses (honoring Blighted/Ravaged difficulty tier, Blooming, Indigo Oil, and Blightspawn).</summary>
    public static HashSet<int> PickBossLanes(int laneCount, int areaLevel, bool blighted, bool uberBlighted,
        int extraBosses, IReadOnlySet<int> bossOnlyLanes, Random rng)
    {
        var bossLanes = new HashSet<int>(bossOnlyLanes);
        if (laneCount <= 0 || areaLevel < 20) return bossLanes;
        int baseWant = uberBlighted ? Math.Max(4, laneCount / 2)
            : blighted ? Math.Max(2, laneCount / 3)
            : (areaLevel >= 68 ? 1 : (rng.Next(100) < 35 ? 1 : 0));
        int want = Math.Min(laneCount, baseWant + Math.Max(0, extraBosses));
        var candidates = Enumerable.Range(0, laneCount).Where(l => !bossLanes.Contains(l)).ToList();
        while (bossLanes.Count < want && candidates.Count > 0)
        {
            int idx = rng.Next(candidates.Count);
            bossLanes.Add(candidates[idx]);
            candidates.RemoveAt(idx);
        }
        return bossLanes;
    }

    /// <summary>Decides rarity for the <paramref name="index"/>th enemy (1-based) on a lane.</summary>
    public static MonsterRarity RarityFor(int index, int totalPerLane, bool hasBoss, bool bossOnlyLane, Random rng)
    {
        // Blightspawn boss-only lanes send Unique Blight Bosses throughout the lane (every wave beat, capped by caller).
        if (bossOnlyLane) return MonsterRarity.Unique;
        int bossIndex = Math.Max(1, totalPerLane <= 3 ? totalPerLane : (totalPerLane * 4) / 5);
        if (hasBoss && index == bossIndex) return MonsterRarity.Unique;
        int roll = rng.Next(100);
        if (roll < 8) return MonsterRarity.Rare;
        if (roll < 28) return MonsterRarity.Magic;
        return MonsterRarity.Normal;
    }

    /// <summary>Returns a supported MonsterVarieties entry for the lane's theme and rarity (boss varieties for Unique).</summary>
    public static MonsterCatalog.Variety? VarietyFor(int encounterTypeRow, int lane, int index, MonsterRarity rarity)
    {
        var family = ImmuneFamilyOf(encounterTypeRow);
        if (rarity == MonsterRarity.Unique)
        {
            if (FamilyBosses.TryGetValue(family, out var bosses))
                for (int k = 0; k < bosses.Length; k++)
                    if (Monsters.Find(bosses[(lane + index + k) % bosses.Length]) is { Supported: true } boss) return boss;
            var genericBosses = FamilyBosses[TowerFamily.None];
            for (int k = 0; k < genericBosses.Length; k++)
                if (Monsters.Find(genericBosses[(lane + index + k) % genericBosses.Length]) is { Supported: true } boss) return boss;
        }
        var pool = FamilyVarieties.TryGetValue(family, out var list) ? list : FallbackVarieties;
        for (int k = 0; k < pool.Length; k++)
            if (Monsters.Find(pool[(lane * 5 + index + k) % pool.Length]) is { Supported: true } variety) return variety;
        for (int k = 0; k < FallbackVarieties.Length; k++)
            if (Monsters.Find(FallbackVarieties[(lane * 5 + index + k) % FallbackVarieties.Length]) is { Supported: true } variety) return variety;
        return null;
    }

    /// <summary>
    /// Computes the exact maximum life for a spawned Blight Boss using the proven 3.29.3c formula:
    /// <c>BaseLife(level) * (1 + (MonsterMapBossDifficulty + BlightBossLifeScalingPerLevel) / 100) * (1 + mapBlightBossLife% / 100) * (1 + uberLife% / 100)</c>.
    /// Avoids applying negative <c>BlightBossLifeScalingPerLevel.MoreLife</c> values standalone!
    /// </summary>
    public static uint ComputeBossLife(MonsterCatalog.Variety variety, int areaLevel, IReadOnlyDictionary<uint, int> areaStats)
    {
        double baseLife = Math.Max(1, Monsters.BaseLife(variety, (byte)Math.Clamp(areaLevel, 1, 100)) ?? 100);
        int effectiveMorePct = BlightTables.BossEffectiveMoreLifePct(areaLevel);
        double scaled = baseLife * (1.0 + effectiveMorePct / 100.0);
        int bloomingBossLife = areaStats.GetValueOrDefault(Stat.MapBlightBossLifePlusPct);
        if (bloomingBossLife > 0) scaled *= 1.0 + bloomingBossLife / 100.0;
        // UberInfectedMap__ carries MapMonstersLifePlusPct = 200 (200% more monster life).
        if (areaStats.GetValueOrDefault(Stat.IsUberBlightedMap) > 0)
        {
            int uberLife = Math.Max(200, areaStats.GetValueOrDefault(Stat.MapMonstersLifePlusPct));
            scaled *= 1.0 + uberLife / 100.0;
        }
        return (uint)Math.Clamp(Math.Round(scaled), 1.0, int.MaxValue);
    }
}
