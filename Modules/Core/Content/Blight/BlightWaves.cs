using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Blight;

/// <summary>The six Blight tower families. Lanes of a matching elemental/physical/minion theme are immune to that tower family.</summary>
public enum TowerFamily { None, Cold, Lightning, Buff, Physical, Minion, Fire }

/// <summary>
/// Lane encounter types, monster pools, boss selection and rarity rolling for a Blight pump defence.
/// Maps <see cref="BlightTables.EncounterTypes"/> to supported <c>Metadata/Monsters/LeagueBlight/…</c> varieties and bosses.
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

    /// <summary>The tower family that a BlightEncounterTypes row is immune to, or <see cref="TowerFamily.None"/> for generic lanes.</summary>
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

    /// <summary>Rolls an encounter type row per lane from weighted BlightEncounterTypes (mix of themed elemental/physical/minion and generic lanes).</summary>
    public static Dictionary<int, int> PickLaneTypes(int laneCount, Random rng)
    {
        var result = new Dictionary<int, int>();
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
            int picked = weighted[0].Row;
            foreach (var e in weighted)
            {
                if ((roll -= e.Weight) < 0) { picked = e.Row; break; }
            }
            result[i] = picked;
        }
        return result;
    }

    /// <summary>Picks which lanes spawn a Blight boss near the end of their wave (maps and blighted maps: 1 boss lane in normal maps, up to half in Blighted maps).</summary>
    public static HashSet<int> PickBossLanes(int laneCount, int areaLevel, bool blighted, Random rng)
    {
        var bossLanes = new HashSet<int>();
        if (laneCount <= 0 || areaLevel < 20) return bossLanes;
        int want = blighted ? Math.Max(2, laneCount / 3) : (areaLevel >= 68 ? 1 : (rng.Next(100) < 35 ? 1 : 0));
        want = Math.Min(want, laneCount);
        var candidates = Enumerable.Range(0, laneCount).ToList();
        for (int k = 0; k < want && candidates.Count > 0; k++)
        {
            int idx = rng.Next(candidates.Count);
            bossLanes.Add(candidates[idx]);
            candidates.RemoveAt(idx);
        }
        return bossLanes;
    }

    /// <summary>Decides rarity for the <paramref name="index"/>th enemy (1-based) on a lane.</summary>
    public static MonsterRarity RarityFor(int index, int totalPerLane, bool hasBoss, Random rng)
    {
        // A boss lane sends its Unique Blight Boss near the final quarter of the lane (index == totalPerLane when totalPerLane <= 3).
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
                foreach (string id in bosses)
                    if (Monsters.Find(id) is { Supported: true } boss) return boss;
            foreach (string id in FamilyBosses[TowerFamily.None])
                if (Monsters.Find(id) is { Supported: true } boss) return boss;
        }
        var pool = FamilyVarieties.TryGetValue(family, out var list) ? list : FallbackVarieties;
        for (int k = 0; k < pool.Length; k++)
            if (Monsters.Find(pool[(lane * 5 + index + k) % pool.Length]) is { Supported: true } variety) return variety;
        for (int k = 0; k < FallbackVarieties.Length; k++)
            if (Monsters.Find(FallbackVarieties[(lane * 5 + index + k) % FallbackVarieties.Length]) is { Supported: true } variety) return variety;
        return null;
    }

    /// <summary>Encounter stats applied to a spawned Blight Boss (BlightBossLifeScalingPerLevel).</summary>
    public static IReadOnlyDictionary<uint, int>? BossEncounterStats(int areaLevel)
    {
        int moreLife = BlightTables.BossMoreLifePct(areaLevel);
        return moreLife != 0 ? new Dictionary<uint, int> { [Stat.MaximumLifePlusPct] = moreLife } : null;
    }
}
