namespace GameServer.Modules.Settlers.MapOres;

/// <summary>
/// Composed area, Atlas passive, Kalguuran Scarab, and Prospecting Astrolabe stats for Settlers map ore encounters.
/// Every value is read from the instance's composed area stats so Atlas passives, Scarabs, Idols, and Astrolabes
/// combine identically to 3.29.3c retail.
/// </summary>
public sealed record MapOreStats
{
    public static readonly MapOreStats None = new();

    // ---- Spawn control (CoreLeagues row 27 + ExtraTerrainFeatures row 46) ----
    public bool Disabled { get; init; }
    public bool DisabledFromChance { get; init; }
    public int AdditionalChancePct { get; init; }
    public int ChancePlusPct { get; init; }
    public int SecondaryOreChancePct { get; init; }
    public int SecondaryOreChancePlusPct { get; init; }
    public int ForcedSpawns { get; init; }
    public int AdditionalOres { get; init; }
    public bool ProspectingAstrolabe { get; init; }

    // ---- Ore type weighting & quantity ----
    public int RarerOresFound { get; init; }
    public int OreFoundPlusPct { get; init; }
    public int CrimsonChancePlusPct { get; init; }
    public int OrichalcumChancePlusPct { get; init; }
    public int AmberChancePlusPct { get; init; }
    public int BismuthChancePlusPct { get; init; }
    public int VerisiumChancePlusPct { get; init; }

    // ---- Per-ore Atlas notables ----
    public int BismuthAdditionalModifierPctChance { get; init; }
    public int BismuthTornados { get; init; }
    public int CrimsonAdditionalPods { get; init; }
    public int OrichalcumBuffDurationPlusPct { get; init; }
    public int AmberTreeDamageTakenPlusPct { get; init; }
    public int VerisiumMaximumChannelPlusPct { get; init; }
    public int LostShipmentChancePct { get; init; }
    public int HourglassChancePct { get; init; }

    // ---- Kalguuran Scarabs (ScarabSettlers1..4) ----
    public int GuardedRichesRareChancePct { get; init; } // ScarabSettlers2: packs at least Magic + rare chance
    public bool OreFoundAsBarsInstead { get; init; }     // ScarabSettlers3: Refinement
    public bool EscalatingEncounters { get; init; }      // ScarabSettlers4: Enriching

    // ---- Refiner's Bargain keystone (PassiveSkills 3571) ----
    public bool SulphiteGrantsRandomOreInstead { get; init; }
    public int SulphiteOrePct { get; init; }
    public bool HarvestGrantsRandomCropsInstead { get; init; }
    public int HarvestCropPct { get; init; }
    public int PossessedEquipmentToDustPctChance { get; init; }

    // ---- Map quantity / rarity context ----
    public int MapQuantity { get; init; }
    public int MapRarity { get; init; }

    public static MapOreStats From(IReadOnlyDictionary<uint, int> stats)
    {
        int S(uint id) => stats.GetValueOrDefault(id);
        return new()
        {
            Disabled = S(Stat.MapDisableSettlersOre) != 0 || S(Stat.MapNukeEverything) != 0
                || S(Stat.AtlasKeystoneNoExtraContent) != 0 || S(Stat.IsBlightedMap) != 0
                || (S(Stat.MapDisableLeagues) & (1 << LeagueChances.Settlers)) != 0,
            DisabledFromChance = S(Stat.MapDisableSettlersOreFromChance) != 0,
            AdditionalChancePct = S(Stat.MapSettlersOreAdditionalChancePct),
            ChancePlusPct = S(Stat.MapSettlersOreChancePlusPct),
            SecondaryOreChancePct = S(Stat.MapSettlersSecondaryOreChancePct),
            SecondaryOreChancePlusPct = S(Stat.MapVillageSecondaryOreChancePlusPct),
            ForcedSpawns = Math.Max(0, S(Stat.MapSpawnSettlersOre)) + Math.Max(0, S(Stat.MapReliquaryMustCompleteSettlersOre)),
            AdditionalOres = Math.Max(0, S(Stat.MapContainsXAdditionalVillageOres)),
            ProspectingAstrolabe = S(Stat.ProspectingAstrolabeEnabledMap) != 0,

            RarerOresFound = Math.Max(0, S(Stat.MapVillageRarerOresFound)),
            OreFoundPlusPct = S(Stat.MapSettlersOreFoundPlusPct),
            CrimsonChancePlusPct = S(Stat.MapVillageCrimsonOreChancePlusPct),
            OrichalcumChancePlusPct = S(Stat.MapVillageOrichalcumOreChancePlusPct),
            AmberChancePlusPct = S(Stat.MapVillageAmberOreChancePlusPct),
            BismuthChancePlusPct = S(Stat.MapVillageBismuthOreChancePlusPct),
            VerisiumChancePlusPct = S(Stat.MapVillageVerisiumOreChancePlusPct),

            BismuthAdditionalModifierPctChance = Math.Max(0, S(Stat.MapSettlersBismuthAdditionalModifierPctChance)),
            BismuthTornados = Math.Max(0, S(Stat.MapBismuthTornados)),
            CrimsonAdditionalPods = Math.Max(0, S(Stat.MapSettlersCrimsonXAdditionalPods)),
            OrichalcumBuffDurationPlusPct = S(Stat.MapSettlersOrichalcumBuffDurationPlusPct),
            AmberTreeDamageTakenPlusPct = S(Stat.MapSettlersAmberTreeDamageTakenPlusPct),
            VerisiumMaximumChannelPlusPct = Math.Max(0, S(Stat.MapSettlersVerisiumMaximumChannelPlusPct)),
            LostShipmentChancePct = Math.Clamp(S(Stat.MapSettlersPctChanceForLostShipmentInsteadOfOre), 0, 100),
            HourglassChancePct = Math.Clamp(S(Stat.MapSettlersPctChanceForOreToBeHourglassEncounter), 0, 100),

            GuardedRichesRareChancePct = Math.Max(0, S(Stat.MapAllVillagePacksMagicAndAllowRareChancePct)),
            OreFoundAsBarsInstead = S(Stat.MapVillageOreFoundAsBarsInstead) != 0,
            EscalatingEncounters = S(Stat.MapVillageEscalatingEncounters) != 0,

            SulphiteGrantsRandomOreInstead = S(Stat.MapSulphiteGrantsRandomOreInstead) != 0,
            SulphiteOrePct = S(Stat.MapSulphiteGrantsRandomOrePctOfSulphite) is > 0 and int sPct ? sPct : 10,
            HarvestGrantsRandomCropsInstead = S(Stat.MapHarvestMonstersGrantRandomCropsInstead) != 0,
            HarvestCropPct = S(Stat.MapHarvestMonstersGrantPctJuiceRandomCrop) is > 0 and int hPct ? hPct : 250,
            PossessedEquipmentToDustPctChance = Math.Clamp(S(Stat.MapPossessedMonsterEquipmentDropsConvertedToDustPctChance), 0, 100),

            MapQuantity = S(Stat.MapItemDropQuantityPlusPct) + S(Stat.MapImplicitItemDropQuantityPlusPct) + S(Stat.MapUnscaledItemDropQuantityPlusPct),
            MapRarity = S(Stat.MapItemDropRarityPlusPct),
        };
    }

    /// <summary>
    /// Picks which encounter occupies one ore slot: Lost Shipment (Lost in Transit), Thaumaturgic Dust Hourglass,
    /// Starfall Crater entrance (rare T16+ Verisium replacement), or one of the 5 ore types weighted by
    /// <c>VillageExports</c>, Atlas Infusion notables, and Fortune's Favour (<c>map_village_rarer_ores_found</c>).
    /// </summary>
    public MapOreSlotKind PickSlotKind(Random rng, int areaLevel, bool isMap, bool allowStarfallCrater)
    {
        if (isMap && LostShipmentChancePct > 0 && rng.Next(100) < LostShipmentChancePct)
            return MapOreSlotKind.LostShipment;

        // 3.29 Thaumaturgic Dust Hourglass: explicit stat 22427 or 8% base map chance when maps run Settlers.
        int hourglassChance = HourglassChancePct > 0 ? HourglassChancePct : (isMap ? 8 : 0);
        if (hourglassChance > 0 && rng.Next(100) < hourglassChance)
            return MapOreSlotKind.DustHourglass;

        var ore = PickOreType(rng, areaLevel);
        if (ore == MapOreSlotKind.Verisium && allowStarfallCrater && isMap && areaLevel >= 83)
        {
            // Rare Starfall Crater replacement (~5% of T16+ Verisium nodes; boosted by Fortune's Favour).
            int craterChance = RarerOresFound > 0 ? 10 : 5;
            if (rng.Next(100) < craterChance)
                return MapOreSlotKind.StarfallCrater;
        }
        return ore;
    }

    /// <summary>Picks one of the 5 ore types (Crimson Iron, Orichalcum, Petrified Amber, Bismuth, Verisium).</summary>
    public MapOreSlotKind PickOreType(Random rng, int areaLevel)
    {
        // Base weights from VillageExports.__col5: [200, 175, 133, 84, 52].
        // Fortune's Favour (map_village_rarer_ores_found) shifts weight toward rarer ores (Bismuth & Verisium)
        // and tilts each roll upward by rolling (1 + RarerOresFound) times and taking the rarest pick.
        int[] bonusPct =
        [
            CrimsonChancePlusPct,
            OrichalcumChancePlusPct,
            AmberChancePlusPct,
            BismuthChancePlusPct,
            VerisiumChancePlusPct,
        ];

        // Level gates match retail: Crimson/Orichalcum available anywhere; Amber lvl 18+; Bismuth lvl 34+; Verisium lvl 55+.
        int[] minLevel = [1, 1, 18, 34, 55];

        double[] weights = new double[5];
        double total = 0;
        for (int i = 0; i < 5; i++)
        {
            if (areaLevel < minLevel[i]) continue;
            double w = MapOreTables.BaseOreWeight((MapOreSlotKind)i) * Math.Max(0, 100 + bonusPct[i]) / 100.0;
            if (w <= 0) continue;
            weights[i] = w;
            total += w;
        }
        if (total <= 0) return MapOreSlotKind.CrimsonIron;

        int DrawOnce()
        {
            double roll = rng.NextDouble() * total;
            for (int i = 0; i < 5; i++)
            {
                if (weights[i] <= 0) continue;
                if (roll < weights[i]) return i;
                roll -= weights[i];
            }
            return 0;
        }

        int best = DrawOnce();
        for (int extra = 0; extra < RarerOresFound; extra++)
            best = Math.Max(best, DrawOnce());
        return (MapOreSlotKind)best;
    }

    /// <summary>
    /// Computes the ore or bar yield of one completed ore deposit, respecting:
    /// area level curve (<c>VillageBalancePerLevelShared.__col6</c>), rarity tier factor, map IIQ,
    /// <c>map_settlers_ore_found_+%</c>, Verisium channel multiplier, <c>Kalguuran Scarab of Enriching</c>
    /// (+15% per previously completed ore deposit in the area), Prospecting Astrolabe bonus (+25%),
    /// and <see cref="VillageResourceHooks.OreFoundMultiplier"/> (from <c>SettlersSettings</c>).
    /// </summary>
    public long ComputeOreYield(MapOreSlotKind kind, int areaLevel, int completedBefore, double encounterMultiplier = 1.0)
    {
        double baseAmount = MapOreTables.BaseOreAmount(areaLevel);

        // Rarer ores yield slightly tighter raw stacks matching retail ratios (Crimson ~1.0x .. Verisium ~0.65x before channel).
        double oreFactor = kind switch
        {
            MapOreSlotKind.CrimsonIron => 1.00,
            MapOreSlotKind.Orichalcum => 0.95,
            MapOreSlotKind.PetrifiedAmber => 0.90,
            MapOreSlotKind.Bismuth => 0.75,
            MapOreSlotKind.Verisium => 0.65,
            _ => 1.00,
        };

        // Enriching scarab: +15% increased Ore for each Ore Deposit previously completed in the area.
        int enrichingPct = EscalatingEncounters ? 15 * Math.Max(0, completedBefore) : 0;
        int astrolabePct = ProspectingAstrolabe ? 25 : 0;
        double quantityScale = 1.0 + Math.Max(0, MapQuantity) / 200.0;
        double oreFoundScale = Math.Max(0.1, (100.0 + OreFoundPlusPct + enrichingPct + astrolabePct) / 100.0);
        double settingsMult = VillageResourceHooks.OreFoundMultiplier;

        double raw = baseAmount * oreFactor * quantityScale * oreFoundScale * Math.Max(0.1, encounterMultiplier) * settingsMult;
        return Math.Max(1L, (long)Math.Round(raw));
    }

    /// <summary>Computes Thaumaturgic Dust granted by a completed Dust Hourglass encounter.</summary>
    public long ComputeHourglassDustYield(int areaLevel, int completedBefore)
    {
        double baseDust = MapOreTables.BaseDustScale(areaLevel);
        int enrichingPct = EscalatingEncounters ? 15 * Math.Max(0, completedBefore) : 0;
        int astrolabePct = ProspectingAstrolabe ? 25 : 0;
        double quantityScale = 1.0 + Math.Max(0, MapQuantity) / 200.0;
        double oreFoundScale = Math.Max(0.1, (100.0 + OreFoundPlusPct + enrichingPct + astrolabePct) / 100.0);
        double settingsMult = VillageResourceHooks.OreFoundMultiplier;
        return Math.Max(50L, (long)Math.Round(baseDust * quantityScale * oreFoundScale * settingsMult));
    }
}
