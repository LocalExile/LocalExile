using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Breach;

/// <summary>
/// Central Breach Atlas passive and Breach Scarab (<c>ScarabBreachNew1..6</c>) evaluator.
/// Reads once per instance from the area's composed stats (<see cref="MapModsOf"/> and <see cref="InstanceState.KeptAreaStats"/>)
/// plus the player's own <c>brequel_resource_gained_+%</c> and fruit-find stats when awarding rewards.
/// </summary>
public sealed record BreachAtlas
{
    public static readonly BreachAtlas None = new();

    public const string ContentKey = "Breach";
    public const string ScarabPrefix = "Metadata/Items/Scarabs/ScarabBreach";
    public const string HivebrainGlandId = "Metadata/Items/MapFragments/CurrencyBrequelBossKey";

    // Encounter roll & type controls
    public int AdditionalUnstableBreaches { get; init; }
    public bool ContainsBreachHive { get; init; }                  // ScarabBreachNew1 (Hive)
    public bool OnlyBreachHives { get; init; }                     // Dimensional Foothold keystone
    public bool OnlyUnstableBreaches { get; init; }                // Enemy at the Gates keystone
    public bool FirstBrequelEncounter { get; init; }
    public int ChanceFor1AdditionalBreach { get; init; }
    public int ChanceFor2AdditionalBreach { get; init; }
    public int ChanceFor3AdditionalBreach { get; init; }
    public int ChanceFor10AdditionalBreach { get; init; }
    public int HiveFortressChancePct { get; init; }                // Fortified Dominance
    public bool HiveIsAlwaysSkyFortress { get; init; }             // ScarabBreachNew4 (Marshal)
    public bool UnstableBreachSpawnsBoss { get; init; }            // ScarabBreachNew2 (Dreamer) & ScarabBreachNew4 (Marshal)
    public bool HasBoss { get; init; }
    public bool NoBosses { get; init; }
    public bool NoChests { get; init; }

    // Unstable Breach pacing & scaling
    public int SizePct { get; init; }
    public int TimePassedPct { get; init; }                        // Violent Skirmish (+20%) / Heavy Breach (-30%)
    public int AdditionalSecondsBeforeCollapse { get; init; }      // map_unstable_breach_additional_x_seconds_before_collapse
    public int EnrageAdditionalRareMonsters { get; init; }         // map_unstable_breach_enrage_x_additional_rare_monsters
    public int MonsterCountAndSpeedPctPerBreachOpened { get; init; } // ScarabBreachNew6 (Resonant Cascade)
    public int ArtVariation { get; init; }

    // Monster density, rarity, toughness & experience
    public int MonsterQuantityPct { get; init; }
    public int NumberOfMagicPacksPct { get; init; }
    public int NumberOfRarePacksPct { get; init; }
    public int AdditionalRareMonsters { get; init; }
    public int MonstersLifePct { get; init; }
    public int MonstersDamagePct { get; init; }
    public int MonsterGrantedExperiencePct { get; init; }
    public int MonsterUniqueItemDropChancePct { get; init; }
    public int BrequelMonsterRarityPct { get; init; }
    public bool FaridunEmpoweredMonsters { get; init; }

    // Chests
    public int AdditionalChests { get; init; }
    public int AdditionalLargeChests { get; init; }
    public int LargeChestChancePct { get; init; }
    public bool HasLargeChest { get; init; }
    public bool HandsAreSmall { get; init; }
    public int SpawnBossOnLargeChestPct { get; init; }

    // Hive & Fortress specific
    public int BreakableSpawnersPct { get; init; }                 // Breach Flammable Burrows
    public int BreakableSpawnersAoePct { get; init; }              // Adaptive Reaction
    public int HiveNumberOfSkillsPct { get; init; }                // Protracted Siege
    public int HiveSkillDamagePct { get; init; }                   // Ailith Skill Damage
    public int HivesAdditionalWaves { get; init; }                 // Protracted Siege
    public bool HiveChestsGrantHivebloodInsteadOfGold { get; init; }
    public bool BossHivebloodInsteadOfGold { get; init; }
    public int HivebloodAndWombgiftPerBrokenSpawnerPct { get; init; } // Adaptive Reaction
    public int HiveMonstersToughnessPerBrokenSpawnerPct { get; init; } // Adaptive Reaction
    public bool HiveSkillsAreAllEnrageMonsters { get; init; }      // ScarabBreachNew5 (Incensed Swarm)

    // Boss duplication & rewards
    public int DuplicateBreachBossChancePct { get; init; }         // Fortified Dominance
    public int DuplicateXophPct { get; init; }
    public int DuplicateTulPct { get; init; }
    public int DuplicateEshPct { get; init; }
    public int DuplicateUulNetolPct { get; init; }
    public int DuplicateChayulaPct { get; init; }
    public int BossAdditionalWombgiftChancePct { get; init; }      // Fecund Colony
    public int FortressChestWombgiftChancePct { get; init; }       // Fecund Colony

    // Hiveblood & Wombgifts
    public int HivebloodQuantityPct { get; init; }
    public int WombgiftQuantityPct { get; init; }
    public int ProvisioningWombgiftChancePct { get; init; }
    public int LavishWombgiftChancePct { get; init; }
    public int AncientWombgiftChancePct { get; init; }
    public int GrowingWombgiftChancePct { get; init; }
    public int MysteriousWombgiftChancePct { get; init; }
    public int MapQuantity { get; init; }
    public int MapPackSize { get; init; }

    // Element weighting (legacy + Flaring Embers / Unstoppable Avalanche)
    public int XophWeightPct { get; init; }
    public int TulWeightPct { get; init; }
    public int EshWeightPct { get; init; }
    public int UulNetolWeightPct { get; init; }

    public static BreachAtlas From(Player player)
    {
        var mapMods = MapModsOf(player);
        var areaStats = player.Instance?.KeptAreaStats ?? AreaLeagueStats(player);
        int S(uint stat)
        {
            int fromMap = mapMods?.Get(stat) ?? 0;
            int fromArea = areaStats is not null && areaStats.TryGetValue(stat, out int v) ? v : 0;
            // KeptAreaStats already includes map/atlas/scarab stats when populated; avoid double-counting.
            return fromArea != 0 ? fromArea : fromMap;
        }
        return new BreachAtlas
        {
            AdditionalUnstableBreaches = Math.Max(0, S(Stat.MapContainsAdditionalUnstableBreaches)),
            ContainsBreachHive = S(Stat.MapContainsBreachHive) != 0,
            OnlyBreachHives = S(Stat.MapOnlyBreachHives) != 0,
            OnlyUnstableBreaches = S(Stat.MapOnlyUnstableBreaches) != 0,
            FirstBrequelEncounter = S(Stat.MapFirstBrequelEncounter) != 0,
            ChanceFor1AdditionalBreach = S(Stat.MapBreachPctChanceFor1AdditionalBreach),
            ChanceFor2AdditionalBreach = S(Stat.MapBreachPctChanceFor2AdditionalBreach),
            ChanceFor3AdditionalBreach = S(Stat.MapBreachPctChanceFor3AdditionalBreach),
            ChanceFor10AdditionalBreach = S(Stat.MapBreachPctChanceFor10AdditionalBreach),
            HiveFortressChancePct = S(Stat.MapBreachHiveFortressChancePlusPct),
            HiveIsAlwaysSkyFortress = S(Stat.MapBreachHiveIsAlwaysSkyFortress) != 0,
            UnstableBreachSpawnsBoss = S(Stat.MapUnstableBreachSpawnsBoss) != 0,
            HasBoss = S(Stat.MapBreachHasBoss) != 0,
            NoBosses = S(Stat.MapBreachesHaveNoBosses) != 0,
            NoChests = S(Stat.MapBreachesHaveNoChests) != 0,

            SizePct = S(Stat.MapBreachSizePlusPct),
            TimePassedPct = S(Stat.MapBreachTimePassedPlusPct),
            AdditionalSecondsBeforeCollapse = Math.Max(0, S(Stat.MapUnstableBreachAdditionalXSecondsBeforeCollapse)),
            EnrageAdditionalRareMonsters = Math.Max(0, S(Stat.MapUnstableBreachEnrageXAdditionalRareMonsters)),
            MonsterCountAndSpeedPctPerBreachOpened = Math.Max(0, S(Stat.MapBreachMonsterCountAndSpeedPlusPctPerBreachOpened)),
            ArtVariation = S(Stat.MapBreachArtVariation),

            MonsterQuantityPct = S(Stat.MapBreachMonsterQuantityPlusPct),
            NumberOfMagicPacksPct = S(Stat.MapBreachNumberOfMagicPacksPlusPct),
            NumberOfRarePacksPct = S(Stat.MapBreachNumberOfRarePacksPlusPct),
            AdditionalRareMonsters = Math.Max(0, S(Stat.MapBreachXAdditionalRareMonsters)),
            MonstersLifePct = S(Stat.MapBreachMonstersLifePlusPct),
            MonstersDamagePct = S(Stat.MapBreachMonstersDamagePlusPct),
            MonsterGrantedExperiencePct = S(Stat.MapBreachMonsterGrantedExperiencePlusPct),
            MonsterUniqueItemDropChancePct = S(Stat.MapBreachMonsterUniqueItemDropChancePlusPct),
            BrequelMonsterRarityPct = S(Stat.ItemFoundRarityPlusPctFromBrequelMonsters),
            FaridunEmpoweredMonsters = S(Stat.MapBreachFaridunEmpoweredMonsters) != 0,

            AdditionalChests = Math.Max(0, S(Stat.MapBreachesNumAdditionalChestsToSpawn)),
            AdditionalLargeChests = Math.Max(0, S(Stat.MapBreachesNumAdditionalLargeChestsToSpawn)),
            LargeChestChancePct = S(Stat.MapBreachesLargeChestChancePlusPct),
            HasLargeChest = S(Stat.MapBreachHasLargeChest) != 0,
            HandsAreSmall = S(Stat.MapBreachHandsAreSmall) != 0,
            SpawnBossOnLargeChestPct = S(Stat.MapBreachPctChanceToSpawnBreachlordOnOpeningLargeChest),

            BreakableSpawnersPct = S(Stat.MapBreachHiveNumberOfBreakableSpawnersPlusPct),
            BreakableSpawnersAoePct = S(Stat.MapBreachHiveBreakableSpawnersAreaOfEffectPlusPct),
            HiveNumberOfSkillsPct = S(Stat.MapBreachHiveNumberOfSkillsPlusPct),
            HiveSkillDamagePct = S(Stat.MapBreachHiveSkillDamagePlusPct),
            HivesAdditionalWaves = Math.Max(0, S(Stat.MapBreachHivesAdditionalWavesPlus)),
            HiveChestsGrantHivebloodInsteadOfGold = S(Stat.MapBreachHiveChestsGrantHivebloodInsteadOfGold) != 0,
            BossHivebloodInsteadOfGold = S(Stat.MapBreachBossHivebloodInsteadOfGold) != 0,
            HivebloodAndWombgiftPerBrokenSpawnerPct = Math.Max(0, S(Stat.MapBreachHivebloodAndWombgiftQuantityPerBrokenSpawnerPlusPct)),
            HiveMonstersToughnessPerBrokenSpawnerPct = Math.Max(0, S(Stat.MapBreachHiveMonstersToughnessPerBrokenSpawnerPlusPct)),
            HiveSkillsAreAllEnrageMonsters = S(Stat.MapBreachHiveSkillsAreAllEnrageMonsters) != 0,

            DuplicateBreachBossChancePct = Math.Max(0, S(Stat.MapChanceForAreaPctToDuplicateBreachBoss)),
            DuplicateXophPct = Math.Max(0, S(Stat.MapChanceForAreaPctToDuplicateBreachBossXoph)),
            DuplicateTulPct = Math.Max(0, S(Stat.MapChanceForAreaPctToDuplicateBreachBossTul)),
            DuplicateEshPct = Math.Max(0, S(Stat.MapChanceForAreaPctToDuplicateBreachBossEsh)),
            DuplicateUulNetolPct = Math.Max(0, S(Stat.MapChanceForAreaPctToDuplicateBreachBossUulNetol)),
            DuplicateChayulaPct = Math.Max(0, S(Stat.MapChanceForAreaPctToDuplicateBreachBossChayula)),
            BossAdditionalWombgiftChancePct = Math.Max(0, S(Stat.MapBreachBossAdditionalWombgiftChancePct)),
            FortressChestWombgiftChancePct = Math.Max(0, S(Stat.MapBreachFortressChestWombgiftChancePlusPct)),

            HivebloodQuantityPct = S(Stat.MapBreachHivebloodQuantityPlusPct),
            WombgiftQuantityPct = S(Stat.MapBreachWombgiftQuantityPlusPct),
            ProvisioningWombgiftChancePct = S(Stat.MapBreachProvisioningWombgiftChancePlusPct),
            LavishWombgiftChancePct = S(Stat.MapBreachLavishWombgiftChancePlusPct),
            AncientWombgiftChancePct = S(Stat.MapBreachAncientWombgiftChancePlusPct),
            GrowingWombgiftChancePct = S(Stat.MapBreachGrowingWombgiftChancePlusPct),
            MysteriousWombgiftChancePct = S(Stat.MapBreachMysteriousWombgiftChancePlusPct),
            MapQuantity = mapMods?.Quantity ?? 0,
            MapPackSize = mapMods?.PackSize ?? 0,

            XophWeightPct = S(Stat.MapBreachChanceToBeXophPlusPct),
            TulWeightPct = S(Stat.MapBreachChanceToBeTulPlusPct),
            EshWeightPct = S(Stat.MapBreachChanceToBeEshPlusPct),
            UulNetolWeightPct = S(Stat.MapBreachChanceToBeUulNetolPlusPct),
        };
    }

    /// <summary>Claims Breach scarabs and Hivebrain Gland in <see cref="GameEvents.MapOpening"/> so the map device
    /// emits no unhandled-content warning.</summary>
    public static void OnMapOpening(MapOpening request)
    {
        if (request.Opening is null || request.Campaign) return;
        foreach (var slot in request.Slots)
        {
            if (slot.IsMap) continue;
            if (slot.BaseId.StartsWith(ScarabPrefix, StringComparison.Ordinal)
                || slot.BaseId == HivebrainGlandId)
            {
                request.HandledContent.Add(ContentKey);
                return;
            }
        }
    }

    /// <summary>Total boss duplicate chance (%) combining the generic Breach boss duplicate stat with the max
    /// element-specific boss duplicate stat.</summary>
    public int EffectiveBossDuplicateChancePct => Math.Clamp(
        DuplicateBreachBossChancePct + Math.Max(Math.Max(DuplicateXophPct, DuplicateTulPct),
            Math.Max(DuplicateEshPct, Math.Max(DuplicateUulNetolPct, DuplicateChayulaPct))) / 4, 0, 100);

    /// <summary>Speed &amp; density multiplier from <c>ScarabBreachNew6</c> (Resonant Cascade) for the N-th opened Breach
    /// (1-based <paramref name="breachesOpened"/>).</summary>
    public double ResonantCascadeFactor(int breachesOpened) =>
        1.0 + Math.Max(0, breachesOpened) * MonsterCountAndSpeedPctPerBreachOpened / 100.0;

    /// <summary>Expansion/collapse speed multiplier from <c>map_breach_time_passed_+%</c> (Violent Skirmish +20%,
    /// Heavy Breach -30%).</summary>
    public double TimeSpeedFactor => Math.Clamp(1.0 + TimePassedPct / 100.0, 0.35, 2.5);

    /// <summary>Effective max Breach radius factoring <c>map_breach_size_+%</c>.</summary>
    public float EffectiveMaxRadius => (float)Math.Clamp(BreachRulesets.FullRadius * (1.0 + SizePct / 100.0), 100.0, 240.0);

    /// <summary>Effective open duration before collapse (seconds): base 60 s scaled by time speed + additional seconds
    /// before collapse from Atlas passives.</summary>
    public double EffectiveCollapseSeconds =>
        Math.Max(15.0, BreachTables.OpenSeconds / TimeSpeedFactor + AdditionalSecondsBeforeCollapse);

    /// <summary>Scales a base Hiveblood reward by map/Atlas Hiveblood quantity, broken spawners (Adaptive Reaction),
    /// and player <c>brequel_resource_gained_+%</c>.</summary>
    public int ScaleHiveblood(Player player, int baseAmount, int brokenSpawners = 0, bool enragedBonus = false)
    {
        if (baseAmount <= 0) return 0;
        int playerBonus = CharacterPlayerStats(player).GetValueOrDefault(Stat.BrequelResourceGainedPlusPct);
        int spawnerBonus = brokenSpawners * HivebloodAndWombgiftPerBrokenSpawnerPct;
        int totalPct = HivebloodQuantityPct + playerBonus + spawnerBonus + (enragedBonus ? 30 : 0);
        return Math.Max(1, (int)Math.Round(baseAmount * Math.Max(0.25, 1.0 + totalPct / 100.0)));
    }

    /// <summary>Multiplier applied to Wombgift drop chances from Atlas quantity + Adaptive Reaction + fortress chest bonus.</summary>
    public double WombgiftChanceFactor(int brokenSpawners = 0, bool fortressChest = false, bool enragedBonus = false)
    {
        int totalPct = WombgiftQuantityPct
            + brokenSpawners * HivebloodAndWombgiftPerBrokenSpawnerPct
            + (fortressChest ? FortressChestWombgiftChancePct : 0)
            + (enragedBonus ? 25 : 0);
        return Math.Max(0.25, 1.0 + totalPct / 100.0);
    }

    /// <summary>Rolls a Wombgift BaseItemType Id weighted by Atlas Wombgift-type chance stats and player fruit-find stats.</summary>
    public string RollWombgiftBaseId(Player player, Random rng)
    {
        var ps = CharacterPlayerStats(player);
        int P(uint stat) => ps.GetValueOrDefault(stat);
        double wProvisioning = Math.Max(5, 36.0 * (1.0 + (ProvisioningWombgiftChancePct + P(Stat.BrequelEquipmentFruitFoundChancePlusPct)) / 100.0));
        double wLavish = Math.Max(5, 32.0 * (1.0 + (LavishWombgiftChancePct + P(Stat.BrequelCurrencyFruitFoundChancePlusPct)) / 100.0));
        double wAncient = Math.Max(3, 18.0 * (1.0 + (AncientWombgiftChancePct + P(Stat.BrequelUniqueFruitFoundChancePlusPct)) / 100.0));
        double wMysterious = Math.Max(3, 14.0 * (1.0 + (MysteriousWombgiftChancePct + P(Stat.BrequelMiscFruitFoundChancePlusPct)) / 100.0));
        double total = wProvisioning + wLavish + wAncient + wMysterious;
        double roll = rng.NextDouble() * total;
        if ((roll -= wProvisioning) < 0) return BreachTables.ProvisioningWombgift;
        if ((roll -= wLavish) < 0) return BreachTables.LavishWombgift;
        if ((roll -= wAncient) < 0) return BreachTables.AncientWombgift;
        return BreachTables.MysteriousWombgift;
    }

    /// <summary>Rolls pack rarity honoring <c>map_breach_number_of_magic_packs_+%</c> and <c>map_breach_number_of_rare_packs_+%</c>.</summary>
    public MonsterRarity RollPackRarity(Random rng)
    {
        double baseRare = 1.0 - BreachTables.NormalShare - BreachTables.MagicShare;
        double rareChance = Math.Clamp(baseRare * Math.Max(0.2, 1.0 + NumberOfRarePacksPct / 100.0), 0.01, 0.35);
        double magicChance = Math.Clamp(BreachTables.MagicShare * Math.Max(0.2, 1.0 + NumberOfMagicPacksPct / 100.0), 0.05, 0.55);
        double u = rng.NextDouble();
        if (u < rareChance) return MonsterRarity.Rare;
        if (u < rareChance + magicChance) return MonsterRarity.Magic;
        return MonsterRarity.Normal;
    }

    /// <summary>Builds extra monster mods for Breach / Hive monsters (life scaling, Atlas monster life/damage,
    /// Adaptive Reaction toughness per broken spawner, Incensed Swarm / Resonant Cascade enrage).</summary>
    public MonsterModSet? BuildMonsterMods(int level, bool isBoss, int brokenSpawners = 0, int breachesOpened = 0, bool enraged = false)
    {
        int lifeScale = isBoss ? BreachTables.BossLifeScale(level) : BreachTables.LifeScale(level);
        int extraLife = lifeScale + MonstersLifePct + brokenSpawners * HiveMonstersToughnessPerBrokenSpawnerPct + (enraged ? 35 : 0);
        int extraDmg = MonstersDamagePct + brokenSpawners * (HiveMonstersToughnessPerBrokenSpawnerPct / 2) + (enraged ? 25 : 0);
        int extraSpeed = Math.Max(0, breachesOpened) * MonsterCountAndSpeedPctPerBreachOpened + (enraged ? 20 : 0);
        if (extraLife == 0 && extraDmg == 0 && extraSpeed == 0) return null;
        var dict = new Dictionary<uint, int>();
        if (extraLife != 0) dict[MonsterModCatalog.EncounterLifeStat] = extraLife;
        if (extraDmg != 0) dict[Stat.DamagePlusPct] = extraDmg;
        if (extraSpeed != 0) dict[Stat.AttackAndCastSpeedPlusPct] = extraSpeed;
        return new MonsterModSet([], [], new MonsterModEffects(dict));
    }
}
