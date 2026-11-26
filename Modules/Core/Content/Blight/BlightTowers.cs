using System.Collections.Concurrent;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Blight;

/// <summary>
/// Full behaviour for all 30 buildable Blight towers:
/// - Cold: Chilling Mk I-III (multi-target chill), Freezebolt (piercing freeze), Glacial Cage (imprisonment, no damage).
/// - Lightning: Shock Nova Mk I-III (AoE shock), Lightning Storm (rapid multi-strike), Arc (chaining lightning + shock).
/// - Buff: Empowering Mk I-III (scales nearby towers), Imbuing (buffs nearby players via <see cref="ApplyPlayerImbuement"/>),
///   Smothering (weakens nearby enemies).
/// - Physical: Seismic Mk I-III (pulsing AoE physical + stun), Temporal (persistent action-speed slow aura),
///   Stone Gaze (periodic petrification).
/// - Minion: Summoning Mk I-III (3/5/7 allies), Scout (10 flying allies), Sentinel (1 tanky ally) — re-summons only after
///   all previous minions of that tower have died.
/// - Fire: Fireball Mk I-III (1/3/5 splash fireballs), Flamethrower (rapid stream), Meteor (long-range heavy AoE).
/// Respects lane immunity ("Ineffective against Blighted Monsters of the same type").
/// </summary>
public static class BlightTowers
{
    public const string SmotheringStatsKey = "blight_smothering";
    public const string TemporalStatsKey = "blight_temporal";
    public const string PetrifiedStatsKey = "blight_petrified";
    public const string GlacialCageStatsKey = "blight_glacial_cage";
    public const string StunStatsKey = "blight_seismic_stun";

    // BuffDefinitions rows from BlightTowerAuras (1392..1395: blight_tower_aura_buff_players*).
    static readonly ushort[] ImbuingBuffRows = [1392, 1393, 1394, 1395];

    // Summoned minion varieties (MonsterVarieties rows 7942..7946).
    const string MinionRank1 = "Metadata/Monsters/LeagueBlight/BlightTowerSummonedMinionRank1";
    const string MinionRank2 = "Metadata/Monsters/LeagueBlight/BlightTowerSummonedMinionRank2";
    const string MinionRank3 = "Metadata/Monsters/LeagueBlight/BlightTowerSummonedMinionRank3";
    const string MinionScout = "Metadata/Monsters/LeagueBlight/BlightTowerSummonedAirSupportRank4";
    const string MinionSentinel = "Metadata/Monsters/LeagueBlight/BlightTowerSummonedTankRank4";

    // Per-player Imbuing Tower aura state: empty outside active Blight Imbuing towers so FinalStatsBuilding is O(1).
    sealed record ImbuementState(int Tier, ushort BuffInstance, ushort BuffRow, uint SourceTowerId);
    static readonly ConcurrentDictionary<Player, ImbuementState> _imbuements = new();

    static readonly IReadOnlyDictionary<uint, int>[] SmotheringByTier =
    [
        new Dictionary<uint, int>
        {
            [Stat.BaseDamageTakenPlusPct] = 20,
            [Stat.BaseMovementVelocityPlusPct] = -15,
            [Stat.DamagePlusPct] = -15,
            [Stat.ChanceToBeFrozenShockedIgnitedPct] = 15,
        },
        new Dictionary<uint, int>
        {
            [Stat.BaseDamageTakenPlusPct] = 25,
            [Stat.BaseMovementVelocityPlusPct] = -20,
            [Stat.DamagePlusPct] = -20,
            [Stat.ChanceToBeFrozenShockedIgnitedPct] = 20,
        },
        new Dictionary<uint, int>
        {
            [Stat.BaseDamageTakenPlusPct] = 30,
            [Stat.BaseMovementVelocityPlusPct] = -25,
            [Stat.DamagePlusPct] = -25,
            [Stat.BaseResistAllElementsPct] = -15,
            [Stat.ChanceToBeFrozenShockedIgnitedPct] = 25,
        },
        new Dictionary<uint, int>
        {
            [Stat.BaseDamageTakenPlusPct] = 35,
            [Stat.BaseMovementVelocityPlusPct] = -30,
            [Stat.DamagePlusPct] = -30,
            [Stat.BaseResistAllElementsPct] = -20,
            [Stat.ActionSpeedMinusPct] = 10,
            [Stat.ChanceToBeFrozenShockedIgnitedPct] = 30,
        },
    ];

    static readonly IReadOnlyDictionary<uint, int>[] TemporalByTier =
    [
        new Dictionary<uint, int> { [Stat.ActionSpeedMinusPct] = 30 },
        new Dictionary<uint, int> { [Stat.ActionSpeedMinusPct] = 35 },
        new Dictionary<uint, int> { [Stat.ActionSpeedMinusPct] = 42 },
        new Dictionary<uint, int> { [Stat.ActionSpeedMinusPct] = 50 },
    ];

    static readonly IReadOnlyDictionary<uint, int> ImmobilizedStats =
        new Dictionary<uint, int> { [Stat.ActionSpeedMinusPct] = 100 };

    static readonly Func<uint, int> ZeroStats = static _ => 0;

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

    /// <summary>Whether a tower is a minion-summoning tower and its summoned variety + squad size.</summary>
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

    /// <summary>Whether a defence monster is immune to a tower (same elemental/physical/minion family).</summary>
    public static bool IsImmune(Defence d, int lane, TowerFamily towerFamily)
    {
        if (towerFamily is TowerFamily.None or TowerFamily.Buff) return false;
        int encType = d.LaneEncounterTypes.GetValueOrDefault(lane, -1);
        var laneFamily = BlightWaves.ImmuneFamilyOf(encType);
        return laneFamily != TowerFamily.None && laneFamily == towerFamily;
    }

    /// <summary>Computes the highest Empowering Tower tier (0..3) covering a given cell among finished towers.</summary>
    public static int EmpowerTierAt(IReadOnlyList<(Spot Spot, BlightTables.Tower Tower)> finished, int x, int y, uint excludeId)
    {
        int best = 0;
        foreach (var (s, t) in finished)
        {
            if (s.Id == excludeId) continue;
            int tier = EmpoweringTierOf(t.Id);
            if (tier <= best) continue;
            double dx = s.X - x, dy = s.Y - y;
            if (dx * dx + dy * dy <= (double)t.Radius * t.Radius) best = tier;
        }
        return best;
    }

    static double DamageMultiplier(int empowerTier) => empowerTier switch { 1 => 1.25, 2 => 1.35, 3 => 1.50, _ => 1.0 };
    static double SpeedMultiplier(int empowerTier) => empowerTier switch { 1 => 1.20, 2 => 1.30, 3 => 1.40, _ => 1.0 };
    static double RadiusMultiplier(int empowerTier) => empowerTier switch { 1 => 1.15, 2 => 1.25, 3 => 1.35, _ => 1.0 };

    static double BaseInterval(string towerId) => towerId switch
    {
        "LightningStormTower" => 0.50,
        "FlamethrowerTower" => 0.35,
        "MeteorTower" => 2.50,
        "IcePrisonTower" => 5.00,
        "PetrificationTower" => 4.50,
        "MinionTower1" or "MinionTower2" or "MinionTower3" or "FlyingMinionTower" or "TankyMinionTower" => 3.00,
        _ => 1.00,
    };

    /// <summary>GameEvents.FinalStatsBuilding: adds Imbuing Tower stats for players currently in range (O(1) no-op otherwise).</summary>
    public static void ApplyPlayerImbuement(Player player, Dictionary<uint, int> stats)
    {
        if (_imbuements.IsEmpty || !_imbuements.TryGetValue(player, out var state)) return;
        static void Add(Dictionary<uint, int> s, uint stat, int delta) => s[stat] = s.GetValueOrDefault(stat) + delta;
        Add(stats, Stat.AttackAndCastSpeedPlusPct, 15 + state.Tier * 5);
        Add(stats, Stat.BaseMovementVelocityPlusPct, 15 + state.Tier * 5);
        if (state.Tier >= 1) Add(stats, Stat.DamagePlusPct, 25 + (state.Tier - 1) * 15);
        if (state.Tier >= 2) Add(stats, Stat.CriticalStrikeChancePlusPct, 50 + (state.Tier - 2) * 25);
        if (state.Tier >= 3) Add(stats, Stat.LifeRegenerationRatePerMinutePct, 180);
    }

    /// <summary>Clears a player's Imbuing Tower aura (when leaving an area or on encounter finish / unload).</summary>
    public static async Task ClearPlayerImbuement(Player player)
    {
        if (!_imbuements.TryRemove(player, out var prev)) return;
        if (!player.Closed && !player.LeavingArea && player.Connection.PlayerSent)
            await SendWorldRecord(player, player.Id, InstanceEntities.RemoveBuff(player.Id, prev.BuffInstance), "0x02bc remove Blight Imbuing aura");
    }

    /// <summary>Clears all cached Imbuing states on module unload.</summary>
    public static void ClearAllImbuements() => _imbuements.Clear();

    /// <summary>Steps all 30 tower types for one tick: auras (Empowering/Imbuing/Smothering/Temporal), timed controls
    /// (Glacial Cage/Stone Gaze/Seismic stun expiry), minion summoning (Summoning/Scout/Sentinel) and damage/ailment towers.</summary>
    public static async Task StepTowers(Player player, Defence d, double now,
        Func<Player, IReadOnlyList<Player>> membersOf,
        Func<int, MonsterCatalog.Variety?> varietyOf)
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

        // 1. Expire timed control debuffs (Stone Gaze petrification, Glacial Cage, Seismic stun).
        ExpireTimedControls(d, player, now);

        if (finished.Count == 0)
        {
            await UpdateImbuingAuras(player, [], finished, membersOf);
            ClearZoneDebuffs(d, living);
            return;
        }

        // 2. Continuous area auras: Imbuing (players), Smothering (enemies), Temporal (enemies).
        var imbuingTowers = finished.Where(x => x.Tower.Id == "BuffPlayersTower").ToList();
        await UpdateImbuingAuras(player, imbuingTowers, finished, membersOf);

        var smotheringTowers = finished.Where(x => x.Tower.Id == "WeakenEnemiesTower").ToList();
        var temporalTowers = finished.Where(x => x.Tower.Id == "TemporalTower").ToList();
        UpdateEnemyZoneAuras(d, living, finished, smotheringTowers, temporalTowers);

        // 3. Active towers (minion summoning, control pulses, and all 17 damage towers).
        var areaStats = AreaLeagueStats(player);
        double mapTowerDamagePct = 100.0 + areaStats.GetValueOrDefault(Stat.BlightedMapTowerDamagePlusPctFinal);
        double mapTowerDamageFactor = Math.Max(0.1, mapTowerDamagePct / 100.0);

        foreach (var (spot, tower) in finished)
        {
            if (EmpoweringTierOf(tower.Id) > 0 || tower.Id is "BuffPlayersTower" or "WeakenEnemiesTower" or "TemporalTower")
                continue;

            int empower = EmpowerTierAt(finished, spot.X, spot.Y, spot.Id);

            // Minion towers: summon a full squad only when every previously summoned minion is dead.
            if (TryGetMinionSpec(tower.Id, out string minionVarietyId, out int minionCount))
            {
                await StepMinionTower(player, d, spot, tower, minionVarietyId, minionCount, empower, now);
                continue;
            }

            bool ready;
            lock (d) ready = now >= spot.NextFire;
            if (!ready) continue;

            var family = FamilyOf(tower.Id);
            double effectiveRadius = tower.Radius * RadiusMultiplier(empower);
            double interval = BaseInterval(tower.Id) / SpeedMultiplier(empower);

            // Gather eligible targets in range (excluding enemies immune to this tower family).
            var inRange = new List<(uint Id, int Lane, SpawnedMonster M, (int X, int Y) Pos, double Dist)>();
            foreach (var enemy in living)
            {
                if (enemy.M.Dead || enemy.M.Life == 0 || IsImmune(d, enemy.Lane, family)) continue;
                double dist = Dist(spot.X, spot.Y, enemy.Pos.X, enemy.Pos.Y);
                if (dist <= effectiveRadius) inRange.Add((enemy.Id, enemy.Lane, enemy.M, enemy.Pos, dist));
            }
            if (inRange.Count == 0) continue;
            inRange.Sort((a, b) => a.Dist.CompareTo(b.Dist));

            lock (d) spot.NextFire = now + interval;

            // Pure control towers (no damage): Glacial Cage & Stone Gaze.
            if (tower.Id == "IcePrisonTower")
            {
                double dur = 4.0 * (1.0 + empower * 0.15);
                foreach (var e in inRange)
                    ApplyTimedControl(d, e.Id, e.M, GlacialCageStatsKey, now + dur);
                continue;
            }
            if (tower.Id == "PetrificationTower")
            {
                double dur = 3.0 * (1.0 + empower * 0.15);
                foreach (var e in inRange)
                {
                    if (e.M.CannotBeStunned || e.M.Effects.CannotBeStunned) continue;
                    ApplyTimedControl(d, e.Id, e.M, PetrifiedStatsKey, now + dur);
                }
                continue;
            }

            // Damage towers (all 17 variants).
            var variety = varietyOf(spot.Row);
            int baseDmg = variety is not null && Monsters.BaseDamage(variety, d.Level) is { } dmg ? (dmg.Min + dmg.Max) / 2 : 0;
            if (baseDmg <= 0) continue;
            int scaledDmg = Math.Max(1, (int)Math.Round(baseDmg * DamageMultiplier(empower) * mapTowerDamageFactor));

            await FireDamageTower(player, d, spot, tower, inRange, living, scaledDmg, empower, now);
        }
    }

    static async Task FireDamageTower(Player player, Defence d, Spot spot, BlightTables.Tower tower,
        List<(uint Id, int Lane, SpawnedMonster M, (int X, int Y) Pos, double Dist)> inRange,
        List<(uint Id, int Lane, SpawnedMonster M, (int X, int Y) Pos)> allLiving,
        int damage, int empower, double now)
    {
        switch (tower.Id)
        {
            case "ChillingTower1":
            case "ChillingTower2":
            case "ChillingTower3":
            {
                int tier = tower.Id[^1] - '0';
                int maxTargets = tier switch { 1 => 1, 2 => 3, _ => 5 };
                int chillPct = Math.Min(30, 15 + tier * 5 + empower * 3);
                for (int i = 0; i < Math.Min(maxTargets, inRange.Count); i++)
                {
                    var t = inRange[i];
                    ApplyTowerAilment(t.M, MonsterElementalAilments.Kind.Chill, 4.0, chillPct, now);
                    await HitMonster(player, t.Id, t.M, damage, tower.Name, log: false);
                }
                break;
            }
            case "FreezingTower":
            {
                // Piercing beam along the direction of the nearest enemy, freezing up to 4 enemies.
                int hitDmg = (int)Math.Round(damage * 2.2);
                var primary = inRange[0];
                double dx = primary.Pos.X - spot.X, dy = primary.Pos.Y - spot.Y;
                double len = Math.Max(1e-6, Math.Sqrt(dx * dx + dy * dy));
                double ux = dx / len, uy = dy / len;
                int hits = 0;
                foreach (var t in inRange)
                {
                    double rx = t.Pos.X - spot.X, ry = t.Pos.Y - spot.Y;
                    double proj = rx * ux + ry * uy;
                    double perp = Math.Abs(rx * uy - ry * ux);
                    if (t.Id != primary.Id && (proj < 0 || perp > 8.0)) continue;
                    t.M.Ailments.Freeze(2.0 * (1.0 + empower * 0.15), now, ZeroStats, s => t.M.Effects[s]);
                    await HitMonster(player, t.Id, t.M, hitDmg, tower.Name, log: false);
                    if (++hits >= 4) break;
                }
                break;
            }
            case "ShockingTower1":
            case "ShockingTower2":
            case "ShockingTower3":
            {
                int tier = tower.Id[^1] - '0';
                int shockPct = Math.Min(50, 10 + tier * 5 + empower * 5);
                foreach (var t in inRange)
                {
                    ApplyTowerAilment(t.M, MonsterElementalAilments.Kind.Shock, 4.0, shockPct, now);
                    await HitMonster(player, t.Id, t.M, damage, tower.Name, log: false);
                }
                break;
            }
            case "LightningStormTower":
            {
                int hitDmg = (int)Math.Round(damage * 1.4);
                int shockPct = Math.Min(50, 20 + empower * 5);
                for (int i = 0; i < Math.Min(3, inRange.Count); i++)
                {
                    var t = inRange[i];
                    ApplyTowerAilment(t.M, MonsterElementalAilments.Kind.Shock, 3.0, shockPct, now);
                    await HitMonster(player, t.Id, t.M, hitDmg, tower.Name, log: false);
                }
                break;
            }
            case "ArcingTower":
            {
                // Chains from the closest target to up to 4 additional enemies within 25 cells.
                int shockPct = Math.Min(50, 20 + empower * 5);
                var hitIds = new HashSet<uint>();
                var current = inRange[0];
                for (int chain = 0; chain < 5; chain++)
                {
                    hitIds.Add(current.Id);
                    ApplyTowerAilment(current.M, MonsterElementalAilments.Kind.Shock, 3.5, shockPct, now);
                    await HitMonster(player, current.Id, current.M, damage, tower.Name, log: false);
                    // Pick nearest unhit enemy within 25 cells (not immune to Lightning).
                    (uint Id, int Lane, SpawnedMonster M, (int X, int Y) Pos, double Dist)? next = null;
                    double bestChain = 25.0;
                    foreach (var cand in allLiving)
                    {
                        if (hitIds.Contains(cand.Id) || cand.M.Dead || cand.M.Life == 0 || IsImmune(d, cand.Lane, TowerFamily.Lightning)) continue;
                        double cd = Dist(current.Pos.X, current.Pos.Y, cand.Pos.X, cand.Pos.Y);
                        if (cd <= bestChain) { bestChain = cd; next = (cand.Id, cand.Lane, cand.M, cand.Pos, cd); }
                    }
                    if (next is null) break;
                    current = next.Value;
                }
                break;
            }
            case "StunningTower1":
            case "StunningTower2":
            case "StunningTower3":
            {
                int tier = tower.Id[^1] - '0';
                double stunSec = (0.7 + tier * 0.15) * (1.0 + empower * 0.15);
                foreach (var t in inRange)
                {
                    if (!t.M.CannotBeStunned && !t.M.Effects.CannotBeStunned)
                        ApplyTimedControl(d, t.Id, t.M, StunStatsKey, now + stunSec);
                    await HitMonster(player, t.Id, t.M, damage, tower.Name, log: false);
                }
                break;
            }
            case "FlameTower1":
            case "FlameTower2":
            case "FlameTower3":
            {
                int tier = tower.Id[^1] - '0';
                int projectiles = tier switch { 1 => 1, 2 => 3, _ => 5 };
                int volleyTargets = Math.Min(projectiles, inRange.Count);
                for (int i = 0; i < volleyTargets; i++)
                {
                    var center = inRange[i];
                    foreach (var enemy in allLiving)
                    {
                        if (enemy.M.Dead || enemy.M.Life == 0 || IsImmune(d, enemy.Lane, TowerFamily.Fire)) continue;
                        if (Dist(center.Pos.X, center.Pos.Y, enemy.Pos.X, enemy.Pos.Y) <= 12.0)
                            await HitMonster(player, enemy.Id, enemy.M, damage, tower.Name, log: false);
                    }
                }
                break;
            }
            case "FlamethrowerTower":
            {
                int hitDmg = (int)Math.Round(damage * 1.3);
                for (int i = 0; i < Math.Min(4, inRange.Count); i++)
                    await HitMonster(player, inRange[i].Id, inRange[i].M, hitDmg, tower.Name, log: false);
                break;
            }
            case "MeteorTower":
            {
                var impact = inRange[0];
                int meteorDmg = (int)Math.Round(damage * 3.0);
                foreach (var enemy in allLiving)
                {
                    if (enemy.M.Dead || enemy.M.Life == 0 || IsImmune(d, enemy.Lane, TowerFamily.Fire)) continue;
                    if (Dist(impact.Pos.X, impact.Pos.Y, enemy.Pos.X, enemy.Pos.Y) <= 18.0)
                        await HitMonster(player, enemy.Id, enemy.M, meteorDmg, tower.Name, log: false);
                }
                break;
            }
            default:
                await HitMonster(player, inRange[0].Id, inRange[0].M, damage, tower.Name, log: false);
                break;
        }
    }

    static void ApplyTowerAilment(SpawnedMonster m, MonsterElementalAilments.Kind kind, double seconds, int magnitude, double now) =>
        m.Ailments.WithoutDamage(kind, seconds, now, ZeroStats, s => m.Effects[s], area: 1, stated: magnitude);

    static async Task StepMinionTower(Player player, Defence d, Spot spot, BlightTables.Tower tower,
        string minionVarietyId, int count, int empower, double now)
    {
        bool needSpawn = false;
        lock (d)
        {
            // Prune dead/missing minions of this tower.
            for (int i = spot.Minions.Count - 1; i >= 0; i--)
            {
                uint mid = spot.Minions[i];
                if (!player.Area.Monsters.TryGetValue(mid, out var mm) || mm.Dead || mm.Life == 0)
                    spot.Minions.RemoveAt(i);
            }
            // "Can only summon Minions when previous Minions are dead."
            if (spot.Minions.Count == 0 && now >= spot.NextFire)
            {
                spot.NextFire = now + BaseInterval(tower.Id) / SpeedMultiplier(empower);
                needSpawn = true;
            }
        }
        if (!needSpawn) return;
        if (Monsters.Find(minionVarietyId) is not { Supported: true } variety) return;

        var spawned = new List<uint>(count);
        for (int k = 0; k < count; k++)
        {
            double angle = k * (Math.Tau / Math.Max(1, count));
            var cell = player.Layout.Near((spot.X + (int)Math.Round(Math.Cos(angle) * 4), spot.Y + (int)Math.Round(Math.Sin(angle) * 4)));
            uint id = await SpawnMonster(player, variety, d.Level, MonsterRarity.Normal, cell.X, cell.Y,
                friendly: true, log: false, alone: false, instanceOwned: true,
                owned: (_, m) => { m.AlliedFighter = true; m.Aggro = true; m.NoRewards = true; });
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

    /// <summary>Cleans up all minions, player Imbuing buffs, and enemy tower debuffs when a defence ends.</summary>
    public static async Task CleanupDefence(Player player, Defence d, Func<Player, IReadOnlyList<Player>> membersOf)
    {
        List<Spot> spots;
        List<uint> monsterIds;
        lock (d)
        {
            spots = [.. d.Spots.Values];
            monsterIds = [.. d.Monsters.Keys];
            d.TimedControls.Clear();
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

    static async Task UpdateImbuingAuras(Player player,
        List<(Spot Spot, BlightTables.Tower Tower)> imbuingTowers,
        List<(Spot Spot, BlightTables.Tower Tower)> finished,
        Func<Player, IReadOnlyList<Player>> membersOf)
    {
        var members = membersOf(player);
        double now = AreaTime(player);
        foreach (var member in members)
        {
            int bestTier = -1;
            uint sourceId = 0;
            if (imbuingTowers.Count > 0 && member.PlayerLife > 0)
            {
                var pos = member.Walk.Position(now);
                foreach (var (s, t) in imbuingTowers)
                {
                    int empower = EmpowerTierAt(finished, s.X, s.Y, s.Id);
                    double r = t.Radius * RadiusMultiplier(empower);
                    if (Dist(s.X, s.Y, pos.X, pos.Y) <= r && empower > bestTier)
                    {
                        bestTier = empower;
                        sourceId = s.Id;
                    }
                }
            }

            if (bestTier < 0)
            {
                await ClearPlayerImbuement(member);
                continue;
            }

            ushort row = ImbuingBuffRows[Math.Clamp(bestTier, 0, ImbuingBuffRows.Length - 1)];
            if (_imbuements.TryGetValue(member, out var current) && current.Tier == bestTier && current.BuffRow == row)
                continue;

            if (current is not null)
                await SendWorldRecord(member, member.Id, InstanceEntities.RemoveBuff(member.Id, current.BuffInstance), "0x02bc refresh Blight Imbuing aura");
            ushort inst = member.NextBuff++;
            if (inst == 0) inst = member.NextBuff++;
            _imbuements[member] = new ImbuementState(bestTier, inst, row, sourceId);
            await SendWorldRecord(member, member.Id, InstanceEntities.AddBuff(member.Id, inst, row, sourceId), $"0x02bc add Blight Imbuing aura (tier {bestTier})");
        }
    }

    static void UpdateEnemyZoneAuras(Defence d,
        List<(uint Id, int Lane, SpawnedMonster M, (int X, int Y) Pos)> living,
        List<(Spot Spot, BlightTables.Tower Tower)> finished,
        List<(Spot Spot, BlightTables.Tower Tower)> smotheringTowers,
        List<(Spot Spot, BlightTables.Tower Tower)> temporalTowers)
    {
        foreach (var (id, lane, m, pos) in living)
        {
            // Smothering Tower aura (unless monster is unaffected by weakening tower).
            int bestSmother = -1;
            if (smotheringTowers.Count > 0 && m.Effects[Stat.UnaffectedByWeakeningTower] == 0)
            {
                foreach (var (s, t) in smotheringTowers)
                {
                    int empower = EmpowerTierAt(finished, s.X, s.Y, s.Id);
                    if (Dist(s.X, s.Y, pos.X, pos.Y) <= t.Radius * RadiusMultiplier(empower) && empower > bestSmother)
                        bestSmother = empower;
                }
            }
            if (bestSmother >= 0) m.SetTemporaryStats(SmotheringStatsKey, SmotheringByTier[Math.Clamp(bestSmother, 0, SmotheringByTier.Length - 1)]);
            else m.RemoveTemporaryStats(SmotheringStatsKey);

            // Temporal Tower aura (ineffective against Physical-type lanes).
            int bestTemporal = -1;
            if (temporalTowers.Count > 0 && !IsImmune(d, lane, TowerFamily.Physical))
            {
                foreach (var (s, t) in temporalTowers)
                {
                    int empower = EmpowerTierAt(finished, s.X, s.Y, s.Id);
                    if (Dist(s.X, s.Y, pos.X, pos.Y) <= t.Radius * RadiusMultiplier(empower) && empower > bestTemporal)
                        bestTemporal = empower;
                }
            }
            if (bestTemporal >= 0) m.SetTemporaryStats(TemporalStatsKey, TemporalByTier[Math.Clamp(bestTemporal, 0, TemporalByTier.Length - 1)]);
            else m.RemoveTemporaryStats(TemporalStatsKey);
        }
    }

    static void ClearZoneDebuffs(Defence d, List<(uint Id, int Lane, SpawnedMonster M, (int X, int Y) Pos)> living)
    {
        foreach (var (_, _, m, _) in living)
        {
            m.RemoveTemporaryStats(SmotheringStatsKey);
            m.RemoveTemporaryStats(TemporalStatsKey);
        }
    }

    static void ApplyTimedControl(Defence d, uint monsterId, SpawnedMonster m, string statsKey, double until)
    {
        lock (d)
        {
            var key = (monsterId, statsKey);
            if (!d.TimedControls.TryGetValue(key, out double cur) || until > cur)
                d.TimedControls[key] = until;
        }
        m.SetTemporaryStats(statsKey, ImmobilizedStats);
    }

    static void ExpireTimedControls(Defence d, Player player, double now)
    {
        List<(uint Id, string Key)> expired = [];
        lock (d)
        {
            foreach (var (k, until) in d.TimedControls)
                if (now >= until) expired.Add(k);
            foreach (var k in expired) d.TimedControls.Remove(k);
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
