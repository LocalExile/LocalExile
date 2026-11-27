using System.Collections.Concurrent;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Modules.Blight;

/// <summary>
/// Full 3.29.3c behaviour for all 30 buildable Blight towers, their exact Empowering (<c>_buff1..3</c>) scaling,
/// all 91 Ring Anoint stats (<c>BlightEnchantment*</c>), Cassia's Pride, Scarab of Invigoration (<c>Buff 2462</c>),
/// same-type <c>-99%</c> resistance (with Freezebolt/Shock Nova/Flamethrower full-damage anoint bypasses),
/// Meteor Burning Ground, Stone Gaze hit-breaking, and native cast visuals (<c>ActionStarted</c> + <c>AddBuff</c>).
/// </summary>
public static class BlightTowers
{
    public const string SmotheringStatsKey = "blight_smothering";
    public const string TemporalStatsKey = "blight_temporal";
    public const string PetrifiedStatsKey = "blight_petrified";
    public const string GlacialCageStatsKey = "blight_glacial_cage";
    public const string StunStatsKey = "blight_seismic_stun";
    public const string InvigorationStatsKey = "blight_invigoration";
    public const string BrittleStatsKey = "blight_brittle";
    public const string SapStatsKey = "blight_sap";
    public const string ScorchStatsKey = "blight_scorch";
    public const string MaledictionStatsKey = "blight_malediction";

    // BuffDefinitions rows from BlightTowerAuras & BuffDefinitions.sql (3.29.3c).
    public const ushort EmpoweringBuffRow = 1406;    // blight_tower_aura_buff_towers
    public const ushort TemporalBuffRow = 1429;      // blight_tower_temporal_debuff
    public const ushort PetrifyBuffRow = 613;        // petrify / stone gaze
    public const ushort InvigorationBuffRow = 2462;  // blight_monster_enhanced (Scarab of Invigoration)
    public const ushort BlightreachBuffRow = 2512;   // blight_touched (Fungal Horizons)
    static readonly ushort[] ImbuingBuffRows = [1392, 1393, 1394, 1395]; // blight_tower_aura_buff_players*

    // Summoned minion varieties (MonsterVarieties rows 7942..7946).
    const string MinionRank1 = "Metadata/Monsters/LeagueBlight/BlightTowerSummonedMinionRank1";
    const string MinionRank2 = "Metadata/Monsters/LeagueBlight/BlightTowerSummonedMinionRank2";
    const string MinionRank3 = "Metadata/Monsters/LeagueBlight/BlightTowerSummonedMinionRank3";
    const string MinionScout = "Metadata/Monsters/LeagueBlight/BlightTowerSummonedAirSupportRank4";
    const string MinionSentinel = "Metadata/Monsters/LeagueBlight/BlightTowerSummonedTankRank4";

    // Per-player Imbuing/Temporal Tower aura state: empty outside active Blight aura towers so FinalStatsBuilding is O(1).
    sealed record PlayerAuraState(int Tier, ushort BuffInstance, ushort BuffRow, uint SourceTowerId,
        int EffectPct, int ExtraDamagePct, int ExtraCritPct, bool GrantsOnslaught, bool GrantsStunImmunity, int TemporalActionMinusPct);
    static readonly ConcurrentDictionary<Player, PlayerAuraState> _playerAuras = new();

    static readonly IReadOnlyDictionary<uint, int> ImmobilizedStats =
        new Dictionary<uint, int> { [Stat.ActionSpeedMinusPct] = 100 };

    static readonly IReadOnlyDictionary<uint, int> InvigorationMonsterStats =
        new Dictionary<uint, int>
        {
            [Stat.DamagePlusPct] = 35,
            [Stat.BaseDamageTakenPlusPct] = -25,
            [Stat.BaseMovementVelocityPlusPct] = 15,
        };

    static readonly IReadOnlyDictionary<uint, int> BrittleDebuffStats =
        new Dictionary<uint, int> { [Stat.EnemyAdditionalCriticalStrikeChanceAgainstSelf] = 600 };

    static readonly IReadOnlyDictionary<uint, int> SapDebuffStats =
        new Dictionary<uint, int> { [Stat.DamagePlusPct] = -15 };

    static readonly IReadOnlyDictionary<uint, int> ScorchDebuffStats =
        new Dictionary<uint, int> { [Stat.BaseResistAllElementsPct] = -20 };

    static readonly IReadOnlyDictionary<uint, int> MaledictionDebuffStats =
        new Dictionary<uint, int> { [Stat.BaseDamageTakenPlusPct] = 10, [Stat.DamagePlusPct] = -10 };

    static readonly Func<uint, int> ZeroStats = static _ => 0;
    static readonly IReadOnlyDictionary<uint, int> EmptyRingStats = new Dictionary<uint, int>();

    /// <summary>Tower family for a BlightTowers Id.</summary>
    public static TowerFamily FamilyOf(string towerId)
    {
        if (towerId.StartsWith("Chilling", StringComparison.Ordinal)
            || towerId.StartsWith("Freezing", StringComparison.Ordinal)
            || towerId.StartsWith("IcePrison", StringComparison.Ordinal)) return TowerFamily.Cold;
        if (towerId.StartsWith("Shocking", StringComparison.Ordinal)
            || towerId.StartsWith("LightningStorm", StringComparison.Ordinal)
            || towerId.StartsWith("Arcing", StringComparison.Ordinal)) return TowerFamily.Lightning;
        if (towerId.StartsWith("Buff", StringComparison.Ordinal)
            || towerId.StartsWith("Weaken", StringComparison.Ordinal)) return TowerFamily.Buff;
        if (towerId.StartsWith("Stunning", StringComparison.Ordinal)
            || towerId.StartsWith("Temporal", StringComparison.Ordinal)
            || towerId.StartsWith("Petrification", StringComparison.Ordinal)) return TowerFamily.Physical;
        if (towerId.StartsWith("Minion", StringComparison.Ordinal)
            || towerId.StartsWith("FlyingMinion", StringComparison.Ordinal)
            || towerId.StartsWith("TankyMinion", StringComparison.Ordinal)) return TowerFamily.Minion;
        if (towerId.StartsWith("Flame", StringComparison.Ordinal)
            || towerId.StartsWith("Meteor", StringComparison.Ordinal)) return TowerFamily.Fire;
        return TowerFamily.None;
    }

    /// <summary>Empowering tier (1..3) of a BuffTower, or 0 for any other tower.</summary>
    public static int EmpoweringTierOf(string towerId) => towerId switch
    {
        "BuffTower1" => 1,
        "BuffTower2" => 2,
        "BuffTower3" => 3,
        _ => 0,
    };

    /// <summary>Whether a tower is an Empowering Tower or one of its tier-4 upgrades (Imbuing/Smothering) for Scarab of Invigoration.</summary>
    public static bool IsEmpoweringOrUpgrade(string towerId) =>
        EmpoweringTierOf(towerId) > 0 || towerId is "BuffPlayersTower" or "WeakenEnemiesTower";

    /// <summary>Tower tier 1..4 from BlightTowers.Tier ("I".."IV") or Id suffix (used by Templar Ingenuity salvaged chests).</summary>
    public static int TierNumberOf(BlightTables.Tower tower) => tower.Tier switch
    {
        "I" or "1" => 1,
        "II" or "2" => 2,
        "III" or "3" => 3,
        "IV" or "4" => 4,
        _ => tower.Id.EndsWith('1') ? 1 : tower.Id.EndsWith('2') ? 2 : tower.Id.EndsWith('3') ? 3 : 4,
    };

    /// <summary>Whether a tower is a minion-summoning tower and its summoned variety + base squad size.</summary>
    public static bool TryGetMinionSpec(string towerId, out string varietyId, out int count)
    {
        switch (towerId)
        {
            case "MinionTower1": varietyId = MinionRank1; count = 3; return true;
            case "MinionTower2": varietyId = MinionRank2; count = 5; return true;
            case "MinionTower3": varietyId = MinionRank3; count = 7; return true;
            case "FlyingMinionTower": varietyId = MinionScout; count = 10; return true;
            case "TankyMinionTower": varietyId = MinionSentinel; count = 1; return true;
            default: varietyId = ""; count = 0; return false;
        }
    }

    /// <summary>Whether a defence monster resists a tower family (same elemental/physical/minion theme: -99% tower damage &amp; CC immune).</summary>
    public static bool IsImmune(Defence d, int lane, TowerFamily towerFamily)
    {
        if (towerFamily is TowerFamily.None or TowerFamily.Buff) return false;
        int encType = d.LaneEncounterTypes.GetValueOrDefault(lane, -1);
        var laneFamily = BlightWaves.ImmuneFamilyOf(encType);
        return laneFamily != TowerFamily.None && laneFamily == towerFamily;
    }

    /// <summary>Whether a ring anoint lets this tower deal full (100%) damage to matching-theme immune lanes.</summary>
    public static bool BypassesImmunity(string towerId, IReadOnlyDictionary<uint, int> ringStats) => towerId switch
    {
        "FreezingTower" => ringStats.GetValueOrDefault(Stat.BlightFreezeboltTowerFullDamageColdEnemies) > 0,
        "ShockingTower1" or "ShockingTower2" or "ShockingTower3" => ringStats.GetValueOrDefault(Stat.BlightShocknovaTowerFullDamageLightningEnemies) > 0,
        "FlamethrowerTower" => ringStats.GetValueOrDefault(Stat.BlightFlamethrowerTowerFullDamageFireEnemies) > 0,
        _ => false,
    };

    /// <summary>Exact 3.29.3c Empowering Tower damage bonus percent (+50/100/150%, scaled by Empowering ring anoints).</summary>
    public static int EmpoweringDamagePct(int empowerTier, IReadOnlyDictionary<uint, int>? ringStats = null)
    {
        if (empowerTier <= 0) return 0;
        int basePct = empowerTier switch { 1 => 50, 2 => 100, _ => 150 };
        int extra = ringStats?.GetValueOrDefault(Stat.BlightEmpoweringTowerGrantDamagePlusPct) ?? 0;
        int effect = ringStats?.GetValueOrDefault(Stat.BlightEmpoweringTowerBuffEffectPlusPct) ?? 0;
        return (int)Math.Round((basePct + extra) * (1.0 + effect / 100.0));
    }

    /// <summary>Exact 3.29.3c Empowering Tower cast speed bonus percent (+20/30/40%, scaled by Empowering ring anoints).</summary>
    public static int EmpoweringCastSpeedPct(int empowerTier, IReadOnlyDictionary<uint, int>? ringStats = null)
    {
        if (empowerTier <= 0) return 0;
        int basePct = empowerTier switch { 1 => 20, 2 => 30, _ => 40 };
        int extra = ringStats?.GetValueOrDefault(Stat.BlightEmpoweringTowerGrantCastSpeedPlusPct) ?? 0;
        int effect = ringStats?.GetValueOrDefault(Stat.BlightEmpoweringTowerBuffEffectPlusPct) ?? 0;
        return (int)Math.Round((basePct + extra) * (1.0 + effect / 100.0));
    }

    /// <summary>Computes the highest Empowering Tower tier (0..3) covering a given cell among finished towers (honoring Empowering range anoints).</summary>
    public static int EmpowerTierAt(IReadOnlyList<(Spot Spot, BlightTables.Tower Tower)> finished, int x, int y, uint excludeId,
        IReadOnlyDictionary<uint, int>? ringStats = null)
    {
        int rangePct = (ringStats?.GetValueOrDefault(Stat.BlightTowerRangePlusPct) ?? 0)
            + (ringStats?.GetValueOrDefault(Stat.BlightEmpoweringTowerRangePlusPct) ?? 0);
        double rangeFactor = Math.Max(0.25, 1.0 + rangePct / 100.0);
        int best = 0;
        foreach (var (s, t) in finished)
        {
            if (s.Id == excludeId) continue;
            int tier = EmpoweringTierOf(t.Id);
            if (tier <= best) continue;
            double r = t.Radius * rangeFactor;
            double dx = s.X - x, dy = s.Y - y;
            if (dx * dx + dy * dy <= r * r) best = tier;
        }
        return best;
    }

    /// <summary>Effective tower range in cells, combining generic BlightTowerRangePlusPct, tower-specific ring anoint (handling
    /// the LightningStormTower row 9 stat quirk), and Empowering radius scaling.</summary>
    public static double EffectiveRadius(BlightTables.Tower tower, int empower, IReadOnlyDictionary<uint, int> ringStats)
    {
        int genericPct = ringStats.GetValueOrDefault(Stat.BlightTowerRangePlusPct);
        int specificPct = tower.Id switch
        {
            "ChillingTower1" or "ChillingTower2" or "ChillingTower3" => ringStats.GetValueOrDefault(Stat.BlightChillingTowerRangePlusPct),
            "FreezingTower" => ringStats.GetValueOrDefault(Stat.BlightFreezeboltTowerRangePlusPct),
            "IcePrisonTower" => ringStats.GetValueOrDefault(Stat.BlightGlacialcageTowerRangePlusPct)
                + ringStats.GetValueOrDefault(Stat.BlightGlacialcageTowerAreaOfEffectPlusPct),
            "ShockingTower1" or "ShockingTower2" or "ShockingTower3" => ringStats.GetValueOrDefault(Stat.BlightShockingTowerRangePlusPct),
            // GGG BlightTowers row 9 quirk: ring mod 21529 grants Stat 10596 (BlightLightningstormTowerRangePlusPct).
            "LightningStormTower" => ringStats.GetValueOrDefault(Stat.BlightLightningstormTowerRangePlusPct),
            "ArcingTower" => ringStats.GetValueOrDefault(Stat.BlightArcTowerRangePlusPct),
            "BuffTower1" or "BuffTower2" or "BuffTower3" => ringStats.GetValueOrDefault(Stat.BlightEmpoweringTowerRangePlusPct),
            "BuffPlayersTower" => ringStats.GetValueOrDefault(Stat.BlightImbuingTowerRangePlusPct),
            "WeakenEnemiesTower" => ringStats.GetValueOrDefault(Stat.BlightSmotheringTowerRangePlusPct),
            "StunningTower1" or "StunningTower2" or "StunningTower3" => ringStats.GetValueOrDefault(Stat.BlightSeismicTowerRangePlusPct)
                + ringStats.GetValueOrDefault(Stat.BlightSeismicTowerCascadeRangePlusPct) / 2,
            "TemporalTower" => ringStats.GetValueOrDefault(Stat.BlightTemporalTowerRangePlusPct),
            "PetrificationTower" => ringStats.GetValueOrDefault(Stat.BlightStonegazeTowerRangePlusPct),
            "MinionTower1" or "MinionTower2" or "MinionTower3" => ringStats.GetValueOrDefault(Stat.BlightSummoningTowerRangePlusPct),
            "FlyingMinionTower" => ringStats.GetValueOrDefault(Stat.BlightScoutTowerRangePlusPct),
            "TankyMinionTower" => ringStats.GetValueOrDefault(Stat.BlightSentinelTowerRangePlusPct),
            "FlameTower1" or "FlameTower2" or "FlameTower3" => ringStats.GetValueOrDefault(Stat.BlightFireballTowerRangePlusPct),
            "FlamethrowerTower" => ringStats.GetValueOrDefault(Stat.BlightFlamethrowerTowerRangePlusPct),
            "MeteorTower" => ringStats.GetValueOrDefault(Stat.BlightMeteorTowerRangePlusPct),
            _ => 0,
        };
        double empowerRadius = empower switch { 1 => 1.10, 2 => 1.20, 3 => 1.30, _ => 1.0 };
        return Math.Max(10.0, tower.Radius * Math.Max(0.25, 1.0 + (genericPct + specificPct) / 100.0) * empowerRadius);
    }

    /// <summary>Exact 3.29.3c base cast/cooldown interval in seconds (with _buff1..3 cooldown reductions on Glacial Cage,
    /// Stone Gaze, and Lightning Storm).</summary>
    public static double BaseInterval(string towerId, int empower, IReadOnlyDictionary<uint, int> ringStats)
    {
        double interval = towerId switch
        {
            "ChillingTower1" or "ChillingTower2" or "ChillingTower3" => 1.00,
            "FreezingTower" => 1.50,
            "IcePrisonTower" => empower switch { 1 => 4.50, 2 => 4.00, 3 => 3.50, _ => 5.00 },
            "ShockingTower1" or "ShockingTower2" or "ShockingTower3" => 1.00,
            "LightningStormTower" => empower switch { 1 => 0.34, 2 => 0.28, 3 => 0.22, _ => 0.40 },
            "ArcingTower" => 0.575,
            "StunningTower1" => 0.70,
            "StunningTower2" => 0.35,
            "StunningTower3" => 0.25,
            "PetrificationTower" => empower switch { 1 => 2.50, 2 => 2.00, 3 => 1.50, _ => 3.00 },
            "FlameTower1" or "FlameTower2" or "FlameTower3" => 1.00,
            "FlamethrowerTower" => 0.35,
            "MeteorTower" => 3.00,
            "MinionTower1" or "MinionTower2" or "MinionTower3" or "FlyingMinionTower" or "TankyMinionTower" => 3.00,
            _ => 1.00,
        };
        int speedPct = EmpoweringCastSpeedPct(empower, ringStats) + towerId switch
        {
            "IcePrisonTower" => ringStats.GetValueOrDefault(Stat.BlightGlacialcageTowerCooldownRecoveryPlusPct),
            "PetrificationTower" => ringStats.GetValueOrDefault(Stat.BlightStonegazeTowerCooldownRecoveryPlusPct),
            "LightningStormTower" => -ringStats.GetValueOrDefault(Stat.BlightLightningstormTowerDelayPlusPct),
            "FlameTower1" or "FlameTower2" or "FlameTower3" => ringStats.GetValueOrDefault(Stat.BlightFireballTowerCastSpeedPlusPct),
            "FlamethrowerTower" => ringStats.GetValueOrDefault(Stat.BlightFlamethrowerTowerCastSpeedPlusPct),
            _ => 0,
        };
        return Math.Max(0.10, interval / Math.Max(0.25, 1.0 + speedPct / 100.0));
    }

    static double TowerDamageBonusPct(string towerId, IReadOnlyDictionary<uint, int> ringStats) => towerId switch
    {
        "ChillingTower1" or "ChillingTower2" or "ChillingTower3" => ringStats.GetValueOrDefault(Stat.BlightChillingTowerDamagePlusPct),
        "FreezingTower" => ringStats.GetValueOrDefault(Stat.BlightFreezeboltTowerDamagePlusPct),
        "ShockingTower1" or "ShockingTower2" or "ShockingTower3" => ringStats.GetValueOrDefault(Stat.BlightShockingTowerDamagePlusPct),
        "LightningStormTower" => ringStats.GetValueOrDefault(Stat.BlightLightningstormTowerDamagePlusPct),
        "ArcingTower" => ringStats.GetValueOrDefault(Stat.BlightArcTowerDamagePlusPct) + ringStats.GetValueOrDefault(Stat.BlightTowerArcDamagePlusPct),
        "StunningTower1" or "StunningTower2" or "StunningTower3" => ringStats.GetValueOrDefault(Stat.BlightSeismicTowerDamagePlusPct),
        "FlameTower1" or "FlameTower2" or "FlameTower3" => ringStats.GetValueOrDefault(Stat.BlightFireballTowerDamagePlusPct),
        "FlamethrowerTower" => ringStats.GetValueOrDefault(Stat.BlightFlamethrowerTowerDamagePlusPct),
        "MeteorTower" => ringStats.GetValueOrDefault(Stat.BlightMeteorTowerDamagePlusPct),
        _ => 0,
    };

    /// <summary>Exact GrantedEffects BaseEffectiveness scalar per tower (calibrated to 3.29.3c MonsterVarieties / GrantedEffects).</summary>
    static double TowerSkillEffectiveness(string towerId) => towerId switch
    {
        "ChillingTower1" => 0.70,
        "ChillingTower2" => 1.25,
        "ChillingTower3" => 2.10,
        "FreezingTower" => 3.50,
        "ShockingTower1" => 0.85,
        "ShockingTower2" => 1.35,
        "ShockingTower3" => 1.90,
        "LightningStormTower" => 1.65,
        "ArcingTower" => 2.25,
        "StunningTower1" => 0.35,
        "StunningTower2" => 0.50,
        "StunningTower3" => 0.65,
        "FlameTower1" => 1.00,
        "FlameTower2" => 1.50,
        "FlameTower3" => 2.00,
        "FlamethrowerTower" => 1.30,
        "MeteorTower" => 5.50,
        _ => 1.00,
    };

    /// <summary>Aggregates all Blight Ring Anoint stats (<c>BlightEnchantment*</c>, dynamically loaded from all 91 recipes)
    /// from living players in the instance (zero-allocation empty dictionary when no player wears a ring anoint).</summary>
    public static IReadOnlyDictionary<uint, int> CollectRingAnointStats(IReadOnlyList<Player> members)
    {
        var ringIds = BlightTables.RingAnointStatIds;
        if (ringIds.Count == 0) return EmptyRingStats;
        Dictionary<uint, int>? merged = null;
        foreach (var member in members)
        {
            if (member.Closed || member.LeavingArea) continue;
            var ps = CharacterPlayerStats(member);
            foreach (var (key, value) in ps)
            {
                if (value == 0 || !ringIds.Contains(key)) continue;
                merged ??= new Dictionary<uint, int>();
                merged[key] = Math.Max(merged.GetValueOrDefault(key), value);
            }
        }
        return merged ?? EmptyRingStats;
    }

    /// <summary>GameEvents.FinalStatsBuilding: adds Imbuing &amp; Temporal Tower aura stats for players in range (O(1) no-op otherwise).</summary>
    public static void ApplyPlayerImbuement(Player player, Dictionary<uint, int> stats)
    {
        if (_playerAuras.IsEmpty || !_playerAuras.TryGetValue(player, out var state)) return;
        static void Add(Dictionary<uint, int> s, uint stat, int delta)
        {
            if (delta != 0) s[stat] = s.GetValueOrDefault(stat) + delta;
        }
        if (state.Tier >= 0)
        {
            double mult = Math.Max(0.25, 1.0 + state.EffectPct / 100.0);
            // Exact 3.29.3c BuffDefinitions 1392..1395: base +25% atk/cast, +30% MS; _buff1 +30% dmg; _buff2 +30% crit; _buff3 +600 (10%/s) life regen.
            Add(stats, Stat.AttackAndCastSpeedPlusPct, (int)Math.Round(25 * mult));
            Add(stats, Stat.BaseMovementVelocityPlusPct, (int)Math.Round(30 * mult));
            if (state.Tier >= 1 || state.ExtraDamagePct > 0)
                Add(stats, Stat.DamagePlusPct, (int)Math.Round(((state.Tier >= 1 ? 30 : 0) + state.ExtraDamagePct) * mult));
            if (state.Tier >= 2 || state.ExtraCritPct > 0)
                Add(stats, Stat.CriticalStrikeChancePlusPct, (int)Math.Round(((state.Tier >= 2 ? 30 : 0) + state.ExtraCritPct) * mult));
            if (state.Tier >= 3)
                Add(stats, Stat.LifeRegenerationRatePerMinutePct, (int)Math.Round(600 * mult));
            if (state.GrantsOnslaught)
            {
                Add(stats, Stat.AttackAndCastSpeedPlusPct, 20);
                Add(stats, Stat.BaseMovementVelocityPlusPct, 20);
            }
        }
        if (state.GrantsStunImmunity) Add(stats, Stat.BaseCannotBeStunned, 1);
        if (state.TemporalActionMinusPct != 0) Add(stats, Stat.ActionSpeedMinusPct, state.TemporalActionMinusPct);
    }

    /// <summary>Clears a player's Imbuing/Temporal Tower aura (when leaving an area or on encounter finish / unload).</summary>
    public static async Task ClearPlayerImbuement(Player player)
    {
        if (!_playerAuras.TryRemove(player, out var prev)) return;
        if (prev.BuffInstance != 0 && !player.Closed && !player.LeavingArea && player.Connection.PlayerSent)
            await SendWorldRecord(player, player.Id, InstanceEntities.RemoveBuff(player.Id, prev.BuffInstance), "0x01c2 remove Blight Imbuing aura");
    }

    /// <summary>Clears all cached Imbuing states on module unload.</summary>
    public static void ClearAllImbuements() => _playerAuras.Clear();

    /// <summary>Steps all 30 tower types for one tick: Empowering buff visuals (1406), Scarab of Invigoration (2462),
    /// Imbuing/Smothering/Temporal auras, Meteor burning ground zones, timed controls, minion summoning, and damage towers.</summary>
    public static async Task StepTowers(Player player, Defence d, double now,
        Func<Player, IReadOnlyList<Player>> membersOf,
        Func<int, MonsterCatalog.Variety?> varietyOf,
        Func<Player, uint, Func<byte[]>, string, Task> update)
    {
        List<(Spot Spot, BlightTables.Tower Tower)> finished = [];
        List<(uint Id, int Lane, SpawnedMonster M, (int X, int Y) Pos)> living = [];
        lock (d)
        {
            foreach (var spot in d.Spots.Values)
                if (spot.Row > 0 && spot.Raising == 0 && spot.Row < BlightTables.Towers.Count)
                    finished.Add((spot, BlightTables.Towers[spot.Row]));
            foreach (var (id, info) in d.Monsters)
                if (!d.Resolved.Contains(id) && player.Area.Monsters.TryGetValue(id, out var m) && !m.Dead && m.Life > 0)
                    living.Add((id, info.Lane, m, m.Walk.Position(now)));
        }

        // 1. Expire timed control debuffs (Stone Gaze, Glacial Cage, Seismic stun, Brittle/Sap/Scorch/Malediction) and step Meteor burning ground.
        ExpireTimedControls(d, player, now);
        await StepBurningGrounds(player, d, living, now);

        var members = membersOf(player);
        var ringStats = CollectRingAnointStats(members);
        var areaStats = BlightEncounter.AreaStats(player);

        if (finished.Count == 0)
        {
            await UpdatePlayerAuras(player, [], [], finished, ringStats, members);
            await ClearZoneDebuffs(player, d, living, update);
            return;
        }

        // 2. Empowering buff visual (BuffDefinitions 1406) on covered towers.
        await UpdateEmpoweringVisuals(player, d, finished, ringStats, update);

        // 3. Continuous area auras: Imbuing (players), Temporal (players + enemies), Smothering (enemies), Invigoration (enemies).
        var imbuingTowers = finished.Where(x => x.Tower.Id == "BuffPlayersTower").ToList();
        var temporalTowers = finished.Where(x => x.Tower.Id == "TemporalTower").ToList();
        await UpdatePlayerAuras(player, imbuingTowers, temporalTowers, finished, ringStats, members);

        var smotheringTowers = finished.Where(x => x.Tower.Id == "WeakenEnemiesTower").ToList();
        bool invigoration = areaStats.GetValueOrDefault(Stat.MapBlightEmpoweringTowersAndUpgradesEnhanceEnemies) > 0;
        var invigorationTowers = invigoration ? finished.Where(x => IsEmpoweringOrUpgrade(x.Tower.Id)).ToList() : [];
        await UpdateEnemyZoneAuras(player, d, living, finished, smotheringTowers, temporalTowers, invigorationTowers, ringStats, update);

        // 4. Tower-wide damage multipliers: Sepia Oil, Cassia's Pride (+300% more from AtlasKeystoneCassiasPride or explicit stat),
        // Sturdy Construction (+% damage taken by Blight monsters), and Distinct Archetype Ring Anoint (Archetype >= 0 includes Fireball = 0!).
        double mapTowerMore = Math.Max(0.1, (100.0 + areaStats.GetValueOrDefault(Stat.BlightedMapTowerDamagePlusPctFinal)) / 100.0);
        int atlasMorePct = areaStats.GetValueOrDefault(Stat.MapBlightTowerDamagePlusPctFinalFromAtlasPassive)
            + areaStats.GetValueOrDefault(Stat.BlightTowerDamagePlusPctFinalFromAtlasPassive);
        if (atlasMorePct == 0 && areaStats.GetValueOrDefault(Stat.AtlasKeystoneCassiasPride) > 0) atlasMorePct = 300;
        double atlasTowerMore = Math.Max(0.1, (100.0 + atlasMorePct) / 100.0);
        double sturdyTakeMore = Math.Max(0.1, (100.0 + areaStats.GetValueOrDefault(Stat.MapBlightMonstersTakePlusPctDamage)) / 100.0);
        int distinctArchetypes = finished.Select(x => x.Tower.Archetype).Where(a => a >= 0).Distinct().Count();
        int perTypeIncPct = distinctArchetypes * ringStats.GetValueOrDefault(Stat.BlightTowerDamagePerTowerTypePlusPct);

        // 5. Active towers (minion summoning, control pulses, and all 17 damage towers).
        foreach (var (spot, tower) in finished)
        {
            if (EmpoweringTierOf(tower.Id) > 0 || tower.Id is "BuffPlayersTower" or "WeakenEnemiesTower" or "TemporalTower")
                continue;

            int empower = EmpowerTierAt(finished, spot.X, spot.Y, spot.Id, ringStats);

            // Minion towers: summon a full squad only when every previously summoned minion is dead.
            if (TryGetMinionSpec(tower.Id, out string minionVarietyId, out int baseMinionCount))
            {
                await StepMinionTower(player, d, spot, tower, minionVarietyId, baseMinionCount, empower, ringStats, atlasTowerMore * mapTowerMore, living, now, update);
                continue;
            }

            bool ready;
            lock (d) ready = now >= spot.NextFire;
            if (!ready) continue;

            var family = FamilyOf(tower.Id);
            double effectiveRadius = EffectiveRadius(tower, empower, ringStats);
            double interval = BaseInterval(tower.Id, empower, ringStats);
            bool bypasses = BypassesImmunity(tower.Id, ringStats);
            bool isPureControl = tower.Id is "IcePrisonTower" or "PetrificationTower";

            var inRange = new List<(uint Id, int Lane, SpawnedMonster M, (int X, int Y) Pos, double Dist, bool Immune)>();
            foreach (var enemy in living)
            {
                if (enemy.M.Dead || enemy.M.Life == 0) continue;
                bool immune = IsImmune(d, enemy.Lane, family);
                if (isPureControl && immune) continue;
                double dist = Dist(spot.X, spot.Y, enemy.Pos.X, enemy.Pos.Y);
                if (dist <= effectiveRadius) inRange.Add((enemy.Id, enemy.Lane, enemy.M, enemy.Pos, dist, immune && !bypasses));
            }
            if (inRange.Count == 0) continue;
            inRange.Sort((a, b) => a.Dist.CompareTo(b.Dist));

            lock (d) spot.NextFire = now + interval;

            // Emit native tower cast animation for the exact Empowering tier (0..3), triggering EmptyAction* child projectiles via 0x01ce.
            await SendTowerCast(player, d, spot, empower, inRange[0].Pos.X, inRange[0].Pos.Y, update);

            // Pure control towers (no damage): Glacial Cage & Stone Gaze.
            if (tower.Id == "IcePrisonTower")
            {
                double dur = 3.0 * Math.Max(0.25, 1.0 + ringStats.GetValueOrDefault(Stat.BlightGlacialcageTowerDurationPlusPct) / 100.0);
                int takenPct = ringStats.GetValueOrDefault(Stat.BlightGlacialcageTowerEnemyDamageTakenPlusPct);
                var cageStats = takenPct > 0
                    ? new Dictionary<uint, int> { [Stat.ActionSpeedMinusPct] = 100, [Stat.BaseDamageTakenPlusPct] = takenPct }
                    : ImmobilizedStats;
                foreach (var e in inRange)
                    ApplyTimedControl(d, e.Id, e.M, GlacialCageStatsKey, now + dur, cageStats);
                continue;
            }
            if (tower.Id == "PetrificationTower")
            {
                double dur = 5.0 * Math.Max(0.25, 1.0 + ringStats.GetValueOrDefault(Stat.BlightStonegazeTowerDurationPlusPct) / 100.0);
                int maxTargets = 10 + empower * 2;
                int hitsToBreak = 7 + empower;
                int takenPct = ringStats.GetValueOrDefault(Stat.BlightStonegazeTowerPetrifiedEnemiesTakeDamagePlusPct);
                var petrifyStats = takenPct > 0
                    ? new Dictionary<uint, int> { [Stat.ActionSpeedMinusPct] = 100, [Stat.BaseDamageTakenPlusPct] = takenPct }
                    : ImmobilizedStats;
                int count = 0;
                foreach (var e in inRange)
                {
                    if (count >= maxTargets) break;
                    if (e.M.CannotBeStunned || e.M.Effects.CannotBeStunned) continue;
                    ApplyTimedControl(d, e.Id, e.M, PetrifiedStatsKey, now + dur, petrifyStats);
                    lock (d) d.PetrifyHitsLeft[e.Id] = hitsToBreak;
                    count++;
                }
                continue;
            }

            // Damage towers (all 17 variants).
            var variety = varietyOf(spot.Row);
            int baseDmg = variety is not null && Monsters.BaseDamage(variety, d.Level) is { } dmg ? (dmg.Min + dmg.Max) / 2 : 0;
            if (baseDmg <= 0) continue;
            double incPct = EmpoweringDamagePct(empower, ringStats) + TowerDamageBonusPct(tower.Id, ringStats) + perTypeIncPct;
            double scaled = baseDmg * TowerSkillEffectiveness(tower.Id) * Math.Max(0.1, 1.0 + incPct / 100.0)
                * mapTowerMore * atlasTowerMore * sturdyTakeMore;
            int doubleDmgChance = empower > 0 ? ringStats.GetValueOrDefault(Stat.BlightEmpoweringTowerGrantPctChanceToDealDoubleDamage) : 0;
            if (doubleDmgChance > 0 && AreaRng(player).Next(100) < doubleDmgChance) scaled *= 2.0;
            int scaledDmg = Math.Max(1, (int)Math.Round(scaled));

            await FireDamageTower(player, d, spot, tower, inRange, living, scaledDmg, empower, bypasses, ringStats, now);
        }
    }

    static async Task FireDamageTower(Player player, Defence d, Spot spot, BlightTables.Tower tower,
        List<(uint Id, int Lane, SpawnedMonster M, (int X, int Y) Pos, double Dist, bool Immune)> inRange,
        List<(uint Id, int Lane, SpawnedMonster M, (int X, int Y) Pos)> allLiving,
        int damage, int empower, bool bypasses, IReadOnlyDictionary<uint, int> ringStats, double now)
    {
        var rng = AreaRng(player);
        int EffectiveHit(int raw, bool immune) => immune ? Math.Max(1, raw / 100) : raw; // Exact -99% less vs matching lane.

        switch (tower.Id)
        {
            case "ChillingTower1":
            case "ChillingTower2":
            case "ChillingTower3":
            {
                int tier = tower.Id[^1] - '0';
                // Exact 3.29.3c targets: 9/12/15 base, _buff1..3 scales up to 22; chill 30/50/75% (capped 30% on standard monster ailment model, scaled by ring).
                int maxTargets = (tier switch { 1 => 9, 2 => 12, _ => 15 }) + empower * 2;
                int baseChill = tier switch { 1 => 30, 2 => 50, _ => 75 };
                int chillPct = Math.Clamp((int)Math.Round(baseChill * (1.0 + ringStats.GetValueOrDefault(Stat.BlightChillingTowerChillEffectPlusPct) / 100.0)), 1, 75);
                double chillDur = (4.0 + empower) * Math.Max(0.25, 1.0 + ringStats.GetValueOrDefault(Stat.BlightChillingTowerDurationPlusPct) / 100.0);
                int freezeMs = ringStats.GetValueOrDefault(Stat.BlightChillingTowerFreezeForMs);
                for (int i = 0; i < Math.Min(maxTargets, inRange.Count); i++)
                {
                    var t = inRange[i];
                    if (!t.Immune)
                    {
                        ApplyTowerAilment(t.M, MonsterElementalAilments.Kind.Chill, chillDur, Math.Min(30, chillPct), now);
                        if (freezeMs > 0) t.M.Ailments.Freeze(freezeMs / 1000.0, now, ZeroStats, s => t.M.Effects[s]);
                    }
                    await HitBlightMonster(player, d, t.Id, t.M, EffectiveHit(damage, t.Immune), DamageType.Cold, now, tower.Name);
                }
                break;
            }
            case "FreezingTower":
            {
                // Base 2 proj -> _buff1..3 gains Nova + 3/4/5 proj + ring extra proj; always pierces & freezes; optional Brittle anoint.
                int projectiles = 2 + empower + ringStats.GetValueOrDefault(Stat.BlightFreezeboltTowerProjectilesPlus);
                bool nova = empower >= 1;
                int brittleChance = ringStats.GetValueOrDefault(Stat.BlightFreezeboltTowerChanceToBrittlePct);
                int maxHits = Math.Max(4, projectiles * 2);
                int hits = 0;
                var primary = inRange[0];
                double dx = primary.Pos.X - spot.X, dy = primary.Pos.Y - spot.Y;
                double len = Math.Max(1e-6, Math.Sqrt(dx * dx + dy * dy));
                double ux = dx / len, uy = dy / len;
                foreach (var t in inRange)
                {
                    if (!nova && t.Id != primary.Id)
                    {
                        double rx = t.Pos.X - spot.X, ry = t.Pos.Y - spot.Y;
                        double proj = rx * ux + ry * uy;
                        double perp = Math.Abs(rx * uy - ry * ux);
                        if (proj < 0 || perp > 10.0) continue;
                    }
                    if (!t.Immune)
                    {
                        t.M.Ailments.Freeze(2.0 * (1.0 + empower * 0.15), now, ZeroStats, s => t.M.Effects[s]);
                        if (brittleChance > 0 && rng.Next(100) < brittleChance)
                            ApplyTimedControl(d, t.Id, t.M, BrittleStatsKey, now + 4.0, BrittleDebuffStats);
                    }
                    await HitBlightMonster(player, d, t.Id, t.M, EffectiveHit(damage, t.Immune), DamageType.Cold, now, tower.Name);
                    if (++hits >= maxHits) break;
                }
                break;
            }
            case "ShockingTower1":
            case "ShockingTower2":
            case "ShockingTower3":
            {
                int tier = tower.Id[^1] - '0';
                int shockChance = tier * 10 + (empower > 0 ? 100 : 0);
                int baseShock = empower > 0 ? 20 + empower * 5 : 15 + tier * 5;
                int shockEffect = (tier * 10) + empower * 10 + ringStats.GetValueOrDefault(Stat.BlightShocknovaTowerShockEffectPlusPct);
                int shockPct = Math.Clamp((int)Math.Round(baseShock * (1.0 + shockEffect / 100.0)), 1, 50);
                int repeats = 1 + ringStats.GetValueOrDefault(Stat.BlightShocknovaTowerShockAdditionalRepeats);
                for (int r = 0; r < repeats; r++)
                {
                    foreach (var t in inRange)
                    {
                        if (t.M.Dead || t.M.Life == 0) continue;
                        if (!t.Immune && (shockChance >= 100 || rng.Next(100) < shockChance))
                            ApplyTowerAilment(t.M, MonsterElementalAilments.Kind.Shock, 4.0, shockPct, now);
                        await HitBlightMonster(player, d, t.Id, t.M, EffectiveHit(damage, t.Immune), DamageType.Lightning, now, tower.Name);
                    }
                }
                break;
            }
            case "LightningStormTower":
            {
                bool onEnemies = ringStats.GetValueOrDefault(Stat.BlightLightningstormTowerStormsOnEnemies) > 0;
                int strikes = (onEnemies ? 5 : 3) + empower;
                int shockPct = Math.Min(50, 20 + empower * 5);
                for (int i = 0; i < Math.Min(strikes, inRange.Count); i++)
                {
                    var t = inRange[i];
                    if (!t.Immune) ApplyTowerAilment(t.M, MonsterElementalAilments.Kind.Shock, 3.0, shockPct, now);
                    await HitBlightMonster(player, d, t.Id, t.M, EffectiveHit(damage, t.Immune), DamageType.Lightning, now, tower.Name);
                }
                break;
            }
            case "ArcingTower":
            {
                // Exact 3.29.3c chains: 6/8/10/12 (empower 0..3) + ring extra chains, repeats, 10% shock + optional Sap anoint.
                int chains = 6 + empower * 2 + ringStats.GetValueOrDefault(Stat.BlightArcTowerAdditionalChains);
                int repeats = 1 + ringStats.GetValueOrDefault(Stat.BlightArcTowerAdditionalRepeats);
                int sapChance = ringStats.GetValueOrDefault(Stat.BlightArcTowerChanceToSapPct);
                int shockPct = Math.Min(50, 20 + empower * 5);
                for (int rep = 0; rep < repeats; rep++)
                {
                    var hitIds = new HashSet<uint>();
                    var current = inRange[0];
                    for (int chain = 0; chain <= chains; chain++)
                    {
                        if (current.M.Dead || current.M.Life == 0) break;
                        hitIds.Add(current.Id);
                        if (!current.Immune)
                        {
                            if (rng.Next(100) < 10 + empower * 10)
                                ApplyTowerAilment(current.M, MonsterElementalAilments.Kind.Shock, 3.5, shockPct, now);
                            if (sapChance > 0 && rng.Next(100) < sapChance)
                                ApplyTimedControl(d, current.Id, current.M, SapStatsKey, now + 4.0, SapDebuffStats);
                        }
                        await HitBlightMonster(player, d, current.Id, current.M, EffectiveHit(damage, current.Immune), DamageType.Lightning, now, tower.Name);
                        (uint Id, int Lane, SpawnedMonster M, (int X, int Y) Pos, double Dist, bool Immune)? next = null;
                        double bestChain = 28.0;
                        foreach (var cand in allLiving)
                        {
                            if (hitIds.Contains(cand.Id) || cand.M.Dead || cand.M.Life == 0) continue;
                            double cd = Dist(current.Pos.X, current.Pos.Y, cand.Pos.X, cand.Pos.Y);
                            if (cd <= bestChain)
                            {
                                bestChain = cd;
                                bool imm = IsImmune(d, cand.Lane, TowerFamily.Lightning) && !bypasses;
                                next = (cand.Id, cand.Lane, cand.M, cand.Pos, cd, imm);
                            }
                        }
                        if (next is null) break;
                        current = next.Value;
                    }
                }
                break;
            }
            case "StunningTower1":
            case "StunningTower2":
            case "StunningTower3":
            {
                // Exact 3.29.3c cascades 3/4/5/6 + ring extra cascades, always stuns with +50/100/150% empower stun duration.
                int cascades = 3 + empower + ringStats.GetValueOrDefault(Stat.BlightSeismicTowerAdditionalCascadesPlus);
                int stunDurPct = empower * 50 + ringStats.GetValueOrDefault(Stat.BlightSeismicTowerStunDurationPlusPct);
                double stunSec = 0.75 * Math.Max(0.25, 1.0 + stunDurPct / 100.0);
                int maxTargets = cascades * 3;
                for (int i = 0; i < Math.Min(maxTargets, inRange.Count); i++)
                {
                    var t = inRange[i];
                    if (!t.Immune && !t.M.CannotBeStunned && !t.M.Effects.CannotBeStunned)
                        ApplyTimedControl(d, t.Id, t.M, StunStatsKey, now + stunSec, ImmobilizedStats);
                    await HitBlightMonster(player, d, t.Id, t.M, EffectiveHit(damage, t.Immune), DamageType.Physical, now, tower.Name);
                }
                break;
            }
            case "FlameTower1":
            case "FlameTower2":
            case "FlameTower3":
            {
                int tier = tower.Id[^1] - '0';
                int projectiles = (tier switch { 1 => 1, 2 => 3, _ => 5 })
                    + ringStats.GetValueOrDefault(Stat.BlightFireballTowerAdditionalProjectilesPlus)
                    + ringStats.GetValueOrDefault(Stat.BlightTowerFireballAdditionalProjectile);
                bool nova = ringStats.GetValueOrDefault(Stat.BlightFireballTowerProjectilesNova) > 0;
                double splashRadius = 12.0 * (1.0 + empower * 0.20);
                int volleyTargets = Math.Min(nova ? projectiles * 2 : projectiles, inRange.Count);
                for (int i = 0; i < volleyTargets; i++)
                {
                    var center = inRange[i];
                    foreach (var enemy in allLiving)
                    {
                        if (enemy.M.Dead || enemy.M.Life == 0) continue;
                        if (Dist(center.Pos.X, center.Pos.Y, enemy.Pos.X, enemy.Pos.Y) <= splashRadius)
                        {
                            bool imm = IsImmune(d, enemy.Lane, TowerFamily.Fire) && !bypasses;
                            await HitBlightMonster(player, d, enemy.Id, enemy.M, EffectiveHit(damage, imm), DamageType.Fire, now, tower.Name);
                        }
                    }
                }
                break;
            }
            case "FlamethrowerTower":
            {
                int scorchChance = ringStats.GetValueOrDefault(Stat.BlightFlamethrowerTowerChanceToScorchPct);
                for (int i = 0; i < Math.Min(5, inRange.Count); i++)
                {
                    var t = inRange[i];
                    if (!t.Immune && scorchChance > 0 && rng.Next(100) < scorchChance)
                        ApplyTimedControl(d, t.Id, t.M, ScorchStatsKey, now + 4.0, ScorchDebuffStats);
                    await HitBlightMonster(player, d, t.Id, t.M, EffectiveHit(damage, t.Immune), DamageType.Fire, now, tower.Name);
                }
                break;
            }
            case "MeteorTower":
            {
                // Exact 3.29.3c Meteor: +200/300/400/500% AoE scaling, optional +1 meteor, always-stun, & Burning Ground (damages fire-immune!).
                int meteors = 1 + ringStats.GetValueOrDefault(Stat.BlightMeteorTowerAdditionalMeteorPlus);
                bool alwaysStun = ringStats.GetValueOrDefault(Stat.BlightMeteorTowerAlwaysStun) > 0;
                int burningMs = ringStats.GetValueOrDefault(Stat.BlightMeteorTowerCreatesBurningGroundMs);
                double radius = 18.0 * (1.0 + empower * 0.25);
                for (int mIdx = 0; mIdx < Math.Min(meteors, inRange.Count); mIdx++)
                {
                    var impact = inRange[mIdx];
                    foreach (var enemy in allLiving)
                    {
                        if (enemy.M.Dead || enemy.M.Life == 0) continue;
                        if (Dist(impact.Pos.X, impact.Pos.Y, enemy.Pos.X, enemy.Pos.Y) <= radius)
                        {
                            bool imm = IsImmune(d, enemy.Lane, TowerFamily.Fire) && !bypasses;
                            if (!imm && alwaysStun && !enemy.M.CannotBeStunned && !enemy.M.Effects.CannotBeStunned)
                                ApplyTimedControl(d, enemy.Id, enemy.M, StunStatsKey, now + 1.0, ImmobilizedStats);
                            await HitBlightMonster(player, d, enemy.Id, enemy.M, EffectiveHit(damage, imm), DamageType.Fire, now, tower.Name);
                        }
                    }
                    if (burningMs > 0)
                    {
                        int dps = Math.Max(1, damage / 3);
                        lock (d)
                            d.BurningGrounds.Add(new(impact.Pos.X, impact.Pos.Y, now + burningMs / 1000.0, dps, now + 0.5));
                    }
                }
                break;
            }
            default:
                await HitBlightMonster(player, d, inRange[0].Id, inRange[0].M, EffectiveHit(damage, inRange[0].Immune), DamageType.Physical, now, tower.Name);
                break;
        }
    }

    static async Task HitBlightMonster(Player player, Defence d, uint id, SpawnedMonster m, int damage, DamageType damageType,
        double now, string source, bool countsAsHit = true)
    {
        if (m.Dead || m.Life == 0 || damage <= 0) return;
        // Stone Gaze petrification breaks after 7/8/9/10 hits (DoTs like Meteor Burning Ground do NOT break petrification!).
        if (countsAsHit)
        {
            bool brokePetrify = false;
            lock (d)
            {
                if (d.PetrifyHitsLeft.TryGetValue(id, out int left))
                {
                    if (--left <= 0) { d.PetrifyHitsLeft.Remove(id); d.TimedControls.Remove((id, PetrifiedStatsKey)); brokePetrify = true; }
                    else d.PetrifyHitsLeft[id] = left;
                }
            }
            if (brokePetrify) m.RemoveTemporaryStats(PetrifiedStatsKey);
        }
        // Mitigate through the monster's matching resistance and increased damage taken (Scorch, Smothering, Shock, Malediction, Stone Gaze, Glacial Cage).
        var (mitigated, _, _) = MonsterHitDamage(m, DamagePacket.Of(damageType, damage, damage), critical: false, attack: false, now, default);
        if (mitigated <= 0) return;
        await HitMonster(player, id, m, mitigated, source, log: false);
    }

    static async Task StepBurningGrounds(Player player, Defence d,
        List<(uint Id, int Lane, SpawnedMonster M, (int X, int Y) Pos)> living, double now)
    {
        List<(int X, int Y, int Dps)> active = [];
        lock (d)
        {
            for (int i = d.BurningGrounds.Count - 1; i >= 0; i--)
            {
                var bg = d.BurningGrounds[i];
                if (now >= bg.Until) { d.BurningGrounds.RemoveAt(i); continue; }
                if (now >= bg.NextTick)
                {
                    d.BurningGrounds[i] = bg with { NextTick = now + 0.5 };
                    active.Add((bg.X, bg.Y, Math.Max(1, bg.Dps / 2)));
                }
            }
        }
        foreach (var (gx, gy, tickDmg) in active)
        {
            foreach (var enemy in living)
            {
                if (enemy.M.Dead || enemy.M.Life == 0) continue;
                // Burning Ground is Fire DoT (bypasses Fire-lane tower immunity and does not break Stone Gaze petrification!).
                if (Dist(gx, gy, enemy.Pos.X, enemy.Pos.Y) <= 18.0)
                    await HitBlightMonster(player, d, enemy.Id, enemy.M, tickDmg, DamageType.Fire, now, "Meteor Burning Ground", countsAsHit: false);
            }
        }
    }

    static async Task SendTowerCast(Player player, Defence d, Spot spot, int empower, int targetX, int targetY,
        Func<Player, uint, Func<byte[]>, string, Task> update)
    {
        int tier = Math.Clamp(empower, 0, 3);
        ushort primarySkillId, triggeredSkillId = 0, seq;
        lock (d)
        {
            if (spot.EmptyActionSkillId != 0 && spot.TriggeredSkillIds.Length > tier && spot.TriggeredSkillIds[tier] != 0)
            {
                // EmptyAction* towers (Fireball Mk I..III, Freezebolt): start parent clip (0x01cd) and trigger exact _buff0..3 child projectile (0x01ce, triggered: true).
                primarySkillId = spot.EmptyActionSkillId;
                triggeredSkillId = spot.TriggeredSkillIds[tier];
            }
            else
            {
                primarySkillId = spot.SkillIds.Length > tier ? spot.SkillIds[tier]
                    : spot.SkillIds.Length > 0 ? spot.SkillIds[0] : (ushort)0;
            }
            if (primarySkillId == 0) return;
            seq = spot.ActionSeq++;
            if (seq == 0 || seq == 0xffff) seq = spot.ActionSeq = 1;
        }
        await update(player, spot.Id,
            () => InstanceProtocol.ActionStarted(spot.Id, spot.X, spot.Y, targetX, targetY, primarySkillId, seq, 0x0400),
            $"Blight tower 0x{spot.Id:x} cast skill 0x{primarySkillId:x4} (empower {tier})");
        if (triggeredSkillId != 0)
            await update(player, spot.Id,
                () => InstanceProtocol.InstantAction(spot.Id, triggeredSkillId, targetX, targetY, triggered: true),
                $"0x01ce Blight tower 0x{spot.Id:x} triggered skill 0x{triggeredSkillId:x4} (empower {tier})");
    }

    static void ApplyTowerAilment(SpawnedMonster m, MonsterElementalAilments.Kind kind, double seconds, int magnitude, double now) =>
        m.Ailments.WithoutDamage(kind, seconds, now, ZeroStats, s => m.Effects[s], area: 1, stated: magnitude);

    static async Task StepMinionTower(Player player, Defence d, Spot spot, BlightTables.Tower tower,
        string minionVarietyId, int baseCount, int empower, IReadOnlyDictionary<uint, int> ringStats, double moreDamageFactor,
        List<(uint Id, int Lane, SpawnedMonster M, (int X, int Y) Pos)> living, double now,
        Func<Player, uint, Func<byte[]>, string, Task> update)
    {
        bool isScout = tower.Id == "FlyingMinionTower";
        bool isSentinel = tower.Id == "TankyMinionTower";
        int extraCount = isScout ? ringStats.GetValueOrDefault(Stat.BlightScoutTowerAdditionalMinionsPlus)
            : isSentinel ? 0
            : empower + ringStats.GetValueOrDefault(Stat.BlightSummoningTowerMinionsSummonedPlus);
        int count = Math.Max(1, baseCount + extraCount);

        bool needSpawn = false;
        List<uint> aliveMinions = [];
        lock (d)
        {
            for (int i = spot.Minions.Count - 1; i >= 0; i--)
            {
                uint mid = spot.Minions[i];
                if (!player.Area.Monsters.TryGetValue(mid, out var mm) || mm.Dead || mm.Life == 0)
                    spot.Minions.RemoveAt(i);
                else aliveMinions.Add(mid);
            }
            // "Can only summon Minions when previous Minions are dead."
            if (spot.Minions.Count == 0 && now >= spot.NextFire)
            {
                spot.NextFire = now + BaseInterval(tower.Id, empower, ringStats);
                needSpawn = true;
            }
        }

        // Scout Malediction ring anoint (12347) & Sentinel life leech ring anoint (12348) while minions are alive.
        if (aliveMinions.Count > 0)
        {
            if (isScout && ringStats.GetValueOrDefault(Stat.BlightScoutTowerMinionsInflictMalediction) > 0)
            {
                foreach (uint mid in aliveMinions)
                {
                    if (!player.Area.Monsters.TryGetValue(mid, out var mm)) continue;
                    var mp = mm.Walk.Position(now);
                    foreach (var enemy in living)
                        if (Dist(mp.X, mp.Y, enemy.Pos.X, enemy.Pos.Y) <= 22.0)
                            ApplyTimedControl(d, enemy.Id, enemy.M, MaledictionStatsKey, now + 4.0, MaledictionDebuffStats);
                }
            }
            if (isSentinel && ringStats.GetValueOrDefault(Stat.BlightSentinelTowerMinionsLifeLeechPct) > 0)
            {
                foreach (uint mid in aliveMinions)
                    if (player.Area.Monsters.TryGetValue(mid, out var mm) && mm.Life > 0 && mm.Life < mm.MaxLife)
                        mm.Life = Math.Min(mm.MaxLife, mm.Life + Math.Max(1u, mm.MaxLife / 20));
            }
        }

        if (!needSpawn) return;
        if (Monsters.Find(minionVarietyId) is not { Supported: true } variety) return;

        await SendTowerCast(player, d, spot, empower, spot.X, spot.Y, update);

        // Empower + Ring Anoint minion life/MS/damage scaling + Cassia's Pride (+300% more).
        int lifePct = isSentinel ? empower * 40 + ringStats.GetValueOrDefault(Stat.BlightSentinelTowerMinionLifePlusPct)
            : isScout ? ringStats.GetValueOrDefault(Stat.BlightScoutTowerMinionLifePlusPct)
            : empower * 20 + ringStats.GetValueOrDefault(Stat.BlightSummoningTowerMinionLifePlusPct);
        int msPct = (isSentinel || isScout ? (empower > 0 ? 10 + empower * 10 : 0) : empower * 10)
            + (isSentinel ? ringStats.GetValueOrDefault(Stat.BlightSentinelTowerMinionMovementSpeedPlusPct)
                : isScout ? ringStats.GetValueOrDefault(Stat.BlightScoutTowerMinionMovementSpeedPlusPct)
                : ringStats.GetValueOrDefault(Stat.BlightSummoningTowerMinionMovementSpeedPlusPct));
        int dmgPct = EmpoweringDamagePct(empower, ringStats)
            + (isSentinel ? ringStats.GetValueOrDefault(Stat.BlightSentinelTowerMinionDamagePlusPct)
                : isScout ? ringStats.GetValueOrDefault(Stat.BlightScoutTowerMinionDamagePlusPct)
                : ringStats.GetValueOrDefault(Stat.BlightSummoningTowerMinionDamagePlusPct))
            + (int)Math.Round((moreDamageFactor - 1.0) * 100.0);

        uint baseLife = (uint)Math.Max(1, Monsters.BaseLife(variety, d.Level) ?? 100);
        uint scaledLife = (uint)Math.Clamp(Math.Round(baseLife * Math.Max(0.25, 1.0 + lifePct / 100.0)), 1, int.MaxValue);
        var minionTempStats = new Dictionary<uint, int>();
        if (msPct != 0) minionTempStats[Stat.BaseMovementVelocityPlusPct] = msPct;
        if (dmgPct != 0) minionTempStats[Stat.DamagePlusPct] = dmgPct;

        // BlightTowerSummonedHelicopter (Scout Tower minion) defaults to visible=0 (DisableRendering in .ot); set visible=1
        // so Scout minions render cleanly both on current main and alongside PR #1618 S5 MonsterStates.DefaultVisibleStates.
        Func<MonsterObject, MonsterObject>? minionShape = variety.StateMachine?.IndexOf("visible") >= 0
            ? m => m with { StateMachine = m.StateMachine?.Set("visible", 1) }
            : null;
        var spawned = new List<uint>(count);
        for (int k = 0; k < count; k++)
        {
            double angle = k * (Math.Tau / Math.Max(1, count));
            var cell = player.Layout.Near((spot.X + (int)Math.Round(Math.Cos(angle) * 4), spot.Y + (int)Math.Round(Math.Sin(angle) * 4)));
            uint id = await SpawnMonster(player, variety, d.Level, MonsterRarity.Normal, cell.X, cell.Y,
                friendly: true, log: false, alone: false, instanceOwned: true, ownedMaximumLife: scaledLife,
                shape: minionShape,
                owned: (_, m) =>
                {
                    m.AlliedFighter = true;
                    m.Aggro = true;
                    m.NoRewards = true;
                    if (minionTempStats.Count > 0) m.SetTemporaryStats("blight_tower_minion", minionTempStats);
                });
            if (id != 0) spawned.Add(id);
        }
        if (spawned.Count == 0) return;
        bool active;
        lock (d)
        {
            active = d.Phase is Phase.Building or Phase.Running && d.Spots.ContainsKey(spot.Id);
            if (active) spot.Minions.AddRange(spawned);
        }
        if (!active)
        {
            foreach (uint mid in spawned)
                if (player.Area.Monsters.TryGetValue(mid, out var mm))
                    await RemoveAlliedMinion(player, mid, mm);
        }
    }

    /// <summary>Removes all living summoned minions owned by a tower spot (called on tower upgrade or defence end).</summary>
    public static async Task DismissSpotMinions(Player player, Defence d, Spot spot)
    {
        List<uint> ids;
        lock (d)
        {
            if (spot.Minions.Count == 0) return;
            ids = [.. spot.Minions];
            spot.Minions.Clear();
        }
        foreach (uint mid in ids)
            if (player.Area.Monsters.TryGetValue(mid, out var mm))
                await RemoveAlliedMinion(player, mid, mm);
    }

    /// <summary>Cleans up all minions, player Imbuing buffs, burning grounds, and enemy tower debuffs when a defence ends.</summary>
    public static async Task CleanupDefence(Player player, Defence d, Func<Player, IReadOnlyList<Player>> membersOf)
    {
        List<Spot> spots;
        List<uint> monsterIds;
        lock (d)
        {
            spots = [.. d.Spots.Values];
            monsterIds = [.. d.Monsters.Keys];
            d.TimedControls.Clear();
            d.PetrifyHitsLeft.Clear();
            d.BurningGrounds.Clear();
            d.InvigoratedBuffInstances.Clear();
        }
        foreach (var spot in spots) await DismissSpotMinions(player, d, spot);
        foreach (var member in membersOf(player)) await ClearPlayerImbuement(member);
        foreach (uint id in monsterIds)
        {
            if (!player.Area.Monsters.TryGetValue(id, out var m)) continue;
            m.RemoveTemporaryStats(SmotheringStatsKey);
            m.RemoveTemporaryStats(TemporalStatsKey);
            m.RemoveTemporaryStats(PetrifiedStatsKey);
            m.RemoveTemporaryStats(GlacialCageStatsKey);
            m.RemoveTemporaryStats(StunStatsKey);
            m.RemoveTemporaryStats(InvigorationStatsKey);
            m.RemoveTemporaryStats(BrittleStatsKey);
            m.RemoveTemporaryStats(SapStatsKey);
            m.RemoveTemporaryStats(ScorchStatsKey);
            m.RemoveTemporaryStats(MaledictionStatsKey);
        }
    }

    static async Task RemoveAlliedMinion(Player player, uint id, SpawnedMonster m)
    {
        using (ProjectileMutation(player))
        {
            if (!player.Area.Monsters.ContainsKey(id)) return;
            m.Dead = true;
            m.Aggro = false;
            m.Life = 0;
            m.ActionGeneration++;
            player.Area.Monsters.Remove(id);
        }
        player.Instance?.World.MonsterOrigins.TryRemove(id, out _);
        await SendWorldRemoval(player, id, $"Blight tower minion 0x{id:x} dismissed");
    }

    static async Task UpdateEmpoweringVisuals(Player player, Defence d,
        List<(Spot Spot, BlightTables.Tower Tower)> finished,
        IReadOnlyDictionary<uint, int> ringStats,
        Func<Player, uint, Func<byte[]>, string, Task> update)
    {
        foreach (var (spot, tower) in finished)
        {
            if (EmpoweringTierOf(tower.Id) > 0) continue;
            int tier = EmpowerTierAt(finished, spot.X, spot.Y, spot.Id, ringStats);
            ushort prevInst = 0;
            ushort newInst = 0;
            bool needRemove = false, needAdd = false;
            lock (d)
            {
                if (tier == spot.EmpowerBuffTier) continue;
                if (spot.EmpowerBuffInstance != 0)
                {
                    prevInst = spot.EmpowerBuffInstance;
                    spot.EmpowerBuffInstance = 0;
                    needRemove = true;
                }
                spot.EmpowerBuffTier = tier;
                if (tier > 0)
                {
                    newInst = ++d.NextTowerBuffInstance;
                    if (newInst == 0) newInst = ++d.NextTowerBuffInstance;
                    spot.EmpowerBuffInstance = newInst;
                    needAdd = true;
                }
            }
            if (needRemove)
                await update(player, spot.Id, () => InstanceEntities.RemoveBuff(spot.Id, prevInst), $"0x01c2 remove Empowering aura on tower 0x{spot.Id:x}");
            if (needAdd)
                await update(player, spot.Id, () => InstanceEntities.AddBuff(spot.Id, newInst, EmpoweringBuffRow), $"0x01c1 add Empowering aura (tier {tier}) on tower 0x{spot.Id:x}");
        }
    }

    static async Task UpdatePlayerAuras(Player player,
        List<(Spot Spot, BlightTables.Tower Tower)> imbuingTowers,
        List<(Spot Spot, BlightTables.Tower Tower)> temporalTowers,
        List<(Spot Spot, BlightTables.Tower Tower)> finished,
        IReadOnlyDictionary<uint, int> ringStats,
        IReadOnlyList<Player> members)
    {
        double now = AreaTime(player);
        int imbueEffect = ringStats.GetValueOrDefault(Stat.BlightImbuingTowerBuffEffectPlusPct);
        int imbueExtraDmg = ringStats.GetValueOrDefault(Stat.BlightImbuingTowerGrantDamagePlusPct);
        int imbueExtraCrit = ringStats.GetValueOrDefault(Stat.BlightImbuingTowerGrantCriticalStrikePlusPct);
        bool imbueOnslaught = ringStats.GetValueOrDefault(Stat.BlightImbuingTowerGrantsOnslaught) > 0;
        bool tempStunImmune = ringStats.GetValueOrDefault(Stat.BlightTemporalTowerGrantsStunImmunity) > 0;
        int tempSelfActionMinus = ringStats.GetValueOrDefault(Stat.BlightTemporalTowerGrantYouActionSpeedMinusPct);

        foreach (var member in members)
        {
            int bestImbue = -1;
            uint sourceId = 0;
            bool inTemporal = false;
            if (member.PlayerLife > 0)
            {
                var pos = member.Walk.Position(now);
                foreach (var (s, t) in imbuingTowers)
                {
                    int empower = EmpowerTierAt(finished, s.X, s.Y, s.Id, ringStats);
                    if (Dist(s.X, s.Y, pos.X, pos.Y) <= EffectiveRadius(t, empower, ringStats) && empower > bestImbue)
                    {
                        bestImbue = empower;
                        sourceId = s.Id;
                    }
                }
                if (tempStunImmune || tempSelfActionMinus != 0)
                    foreach (var (s, t) in temporalTowers)
                    {
                        int empower = EmpowerTierAt(finished, s.X, s.Y, s.Id, ringStats);
                        if (Dist(s.X, s.Y, pos.X, pos.Y) <= EffectiveRadius(t, empower, ringStats))
                        {
                            inTemporal = true;
                            break;
                        }
                    }
            }

            if (bestImbue < 0 && !inTemporal)
            {
                await ClearPlayerImbuement(member);
                continue;
            }

            ushort row = bestImbue >= 0 ? ImbuingBuffRows[Math.Clamp(bestImbue, 0, ImbuingBuffRows.Length - 1)] : (ushort)0;
            bool grantStun = inTemporal && tempStunImmune;
            int actionMinus = inTemporal ? tempSelfActionMinus : 0;
            if (_playerAuras.TryGetValue(member, out var cur)
                && cur.Tier == bestImbue && cur.BuffRow == row && cur.EffectPct == imbueEffect
                && cur.ExtraDamagePct == imbueExtraDmg && cur.ExtraCritPct == imbueExtraCrit
                && cur.GrantsOnslaught == imbueOnslaught && cur.GrantsStunImmunity == grantStun
                && cur.TemporalActionMinusPct == actionMinus)
                continue;

            ushort inst = cur?.BuffInstance ?? 0;
            if (cur is not null && cur.BuffRow != row)
            {
                if (cur.BuffInstance != 0)
                    await SendWorldRecord(member, member.Id, InstanceEntities.RemoveBuff(member.Id, cur.BuffInstance), "0x01c2 refresh Blight Imbuing aura");
                inst = 0;
            }
            if (row != 0 && inst == 0)
            {
                inst = member.NextBuff++;
                if (inst == 0) inst = member.NextBuff++;
                await SendWorldRecord(member, member.Id, InstanceEntities.AddBuff(member.Id, inst, row, sourceId), $"0x01c1 add Blight Imbuing aura (tier {bestImbue})");
            }
            _playerAuras[member] = new PlayerAuraState(bestImbue, inst, row, sourceId, imbueEffect, imbueExtraDmg, imbueExtraCrit, imbueOnslaught, grantStun, actionMinus);
        }
    }

    static async Task UpdateEnemyZoneAuras(Player player, Defence d,
        List<(uint Id, int Lane, SpawnedMonster M, (int X, int Y) Pos)> living,
        List<(Spot Spot, BlightTables.Tower Tower)> finished,
        List<(Spot Spot, BlightTables.Tower Tower)> smotheringTowers,
        List<(Spot Spot, BlightTables.Tower Tower)> temporalTowers,
        List<(Spot Spot, BlightTables.Tower Tower)> invigorationTowers,
        IReadOnlyDictionary<uint, int> ringStats,
        Func<Player, uint, Func<byte[]>, string, Task> update)
    {
        double smothMult = Math.Max(0.25, 1.0 + ringStats.GetValueOrDefault(Stat.BlightSmotheringTowerBuffEffectPlusPct) / 100.0);
        int smothExtraDmg = ringStats.GetValueOrDefault(Stat.BlightSmotheringTowerGrantDamagePlusPct);
        int smothExtraMs = ringStats.GetValueOrDefault(Stat.BlightSmotheringTowerGrantMovementSpeedPlusPct);
        int smothAilment = ringStats.GetValueOrDefault(Stat.BlightSmotheringTowerFreezeShockIgnitePct);
        double tempMult = Math.Max(0.25, 1.0 + ringStats.GetValueOrDefault(Stat.BlightTemporalTowerBuffEffectPlusPct) / 100.0);

        foreach (var (id, lane, m, pos) in living)
        {
            // Smothering Tower aura (exact 3.29.3c BuffDefinitions 1396..1399: +25% dmg taken; _buff1 -20% dmg; _buff2 -20% all res; _buff3 15% action speed slow).
            int bestSmother = -1;
            if (smotheringTowers.Count > 0 && m.Effects[Stat.UnaffectedByWeakeningTower] == 0)
            {
                foreach (var (s, t) in smotheringTowers)
                {
                    int empower = EmpowerTierAt(finished, s.X, s.Y, s.Id, ringStats);
                    if (Dist(s.X, s.Y, pos.X, pos.Y) <= EffectiveRadius(t, empower, ringStats) && empower > bestSmother)
                        bestSmother = empower;
                }
            }
            if (bestSmother >= 0)
            {
                var sm = new Dictionary<uint, int> { [Stat.BaseDamageTakenPlusPct] = (int)Math.Round(25 * smothMult) };
                int dmgRed = (bestSmother >= 1 ? -20 : 0) + smothExtraDmg;
                if (dmgRed != 0) sm[Stat.DamagePlusPct] = (int)Math.Round(dmgRed * smothMult);
                if (smothExtraMs != 0) sm[Stat.BaseMovementVelocityPlusPct] = (int)Math.Round(smothExtraMs * smothMult);
                if (bestSmother >= 2) sm[Stat.BaseResistAllElementsPct] = (int)Math.Round(-20 * smothMult);
                if (bestSmother >= 3) sm[Stat.ActionSpeedMinusPct] = (int)Math.Round(15 * smothMult);
                if (smothAilment > 0) sm[Stat.ChanceToBeFrozenShockedIgnitedPct] = smothAilment;
                m.SetTemporaryStats(SmotheringStatsKey, sm);
            }
            else m.RemoveTemporaryStats(SmotheringStatsKey);

            // Temporal Tower aura (ineffective against Physical-type lanes or unaffected_by_temporal_tower; capped below 100% so enemies crawl rather than deadlock).
            int bestTemporal = -1;
            if (temporalTowers.Count > 0 && !IsImmune(d, lane, TowerFamily.Physical) && m.Effects[Stat.UnaffectedByTemporalTower] == 0)
            {
                foreach (var (s, t) in temporalTowers)
                {
                    int empower = EmpowerTierAt(finished, s.X, s.Y, s.Id, ringStats);
                    if (Dist(s.X, s.Y, pos.X, pos.Y) <= EffectiveRadius(t, empower, ringStats) && empower > bestTemporal)
                        bestTemporal = empower;
                }
            }
            if (bestTemporal >= 0)
            {
                int baseSlow = bestTemporal switch { 0 => 35, 1 => 45, 2 => 55, _ => 65 };
                int slow = Math.Clamp((int)Math.Round(baseSlow * tempMult), 10, 85);
                m.SetTemporaryStats(TemporalStatsKey, new Dictionary<uint, int> { [Stat.ActionSpeedMinusPct] = slow });
            }
            else m.RemoveTemporaryStats(TemporalStatsKey);

            // Blight Scarab of Invigoration: Empowering towers & upgrades enhance enemies in range (Buff 2462) and boost their drops.
            if (invigorationTowers.Count > 0)
            {
                bool covered = false;
                foreach (var (s, t) in invigorationTowers)
                {
                    int empower = EmpowerTierAt(finished, s.X, s.Y, s.Id, ringStats);
                    if (Dist(s.X, s.Y, pos.X, pos.Y) <= EffectiveRadius(t, empower, ringStats))
                    {
                        covered = true;
                        break;
                    }
                }
                if (covered)
                {
                    m.SetTemporaryStats(InvigorationStatsKey, InvigorationMonsterStats);
                    ushort addedInst = 0;
                    lock (d)
                    {
                        d.EnhancedMonsters.Add(id);
                        if (!d.InvigoratedBuffInstances.ContainsKey(id))
                        {
                            addedInst = ++d.NextTowerBuffInstance;
                            if (addedInst == 0) addedInst = ++d.NextTowerBuffInstance;
                            d.InvigoratedBuffInstances[id] = addedInst;
                        }
                    }
                    if (addedInst != 0)
                        await update(player, id, () => InstanceEntities.AddBuff(id, addedInst, InvigorationBuffRow),
                            $"0x01c1 Blight Invigoration buff 2462 on monster 0x{id:x}");
                }
            }
        }
    }

    static async Task ClearZoneDebuffs(Player player, Defence d, List<(uint Id, int Lane, SpawnedMonster M, (int X, int Y) Pos)> living,
        Func<Player, uint, Func<byte[]>, string, Task> update)
    {
        foreach (var (id, _, m, _) in living)
        {
            m.RemoveTemporaryStats(SmotheringStatsKey);
            m.RemoveTemporaryStats(TemporalStatsKey);
            m.RemoveTemporaryStats(InvigorationStatsKey);
            ushort prev = 0;
            lock (d)
                if (d.InvigoratedBuffInstances.Remove(id, out prev)) { }
            if (prev != 0)
                await update(player, id, () => InstanceEntities.RemoveBuff(id, prev), $"0x01c2 remove Invigoration on 0x{id:x}");
        }
    }

    static void ApplyTimedControl(Defence d, uint monsterId, SpawnedMonster m, string statsKey, double until, IReadOnlyDictionary<uint, int> stats)
    {
        lock (d)
        {
            var key = (monsterId, statsKey);
            if (!d.TimedControls.TryGetValue(key, out double cur) || until > cur)
                d.TimedControls[key] = until;
        }
        m.SetTemporaryStats(statsKey, stats);
    }

    static void ExpireTimedControls(Defence d, Player player, double now)
    {
        List<(uint Id, string Key)> expired = [];
        lock (d)
        {
            foreach (var (k, until) in d.TimedControls)
                if (now >= until) expired.Add(k);
            foreach (var k in expired)
            {
                d.TimedControls.Remove(k);
                if (k.Key == PetrifiedStatsKey) d.PetrifyHitsLeft.Remove(k.Id);
            }
        }
        foreach (var (id, key) in expired)
            if (player.Area.Monsters.TryGetValue(id, out var m))
                m.RemoveTemporaryStats(key);
    }

    static double Dist(int ax, int ay, int bx, int by)
    {
        double dx = ax - bx, dy = ay - by;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
