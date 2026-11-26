using System.Collections.Concurrent;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Modules.Blight;

/// <summary>
/// Blight Unique item mechanics and the Prismatic Oil keystone:
/// - <b>Breathstealer</b> (<c>create_blighted_spore_on_killing_rare_enemy</c>): killing a Rare monster spawns a 10-second
///   Blighted Spore carrying a weighted random aura from <c>BlightedSporeAuras</c> (radius 60: damage, crit, speed, life regen,
///   resistances, physical aura, energy shield, cannot die, or enemy slow).
/// - <b>Sporeguard</b> (<c>fungal_ground_while_stationary_radius</c>, <c>local_display_trigger_level_x_create_fungal_ground_on_kill</c>,
///   <c>enemies_killed_on_fungal_ground_explode_for_10%_chaos_damage_%_chance</c>): creates Fungal Ground on kill / while stationary
///   (enemies on Fungal Ground deal 10% less damage; player takes 10% less damage) and explodes enemies killed on Fungal Ground for
///   10% of their maximum life as Chaos damage.
/// - <b>Worship the Blightheart</b> (<c>create_fungal_ground_instead_of_consecrated_ground</c>): converts player Consecrated Ground
///   creation / standing bonuses to Fungal Ground.
/// </summary>
public static class BlightSpores
{
    public const double SporeDurationSeconds = 10.0;
    public const double FungalGroundDurationSeconds = 5.0;
    public const int FungalGroundKillRadius = 22;
    public const int FungalExplosionRadius = 22;
    public const string SporeSlowKey = "blight_spore_enemy_slow";
    public const string FungalGroundEnemyKey = "blight_fungal_ground_enemy";

    public sealed record ActiveSpore(int X, int Y, double Until, BlightTables.SporeAura Aura);
    public sealed record FungalPatch(int X, int Y, int Radius, double Until);

    static readonly ConcurrentDictionary<InstanceState, List<ActiveSpore>> Spores = new();
    static readonly ConcurrentDictionary<InstanceState, List<FungalPatch>> FungalPatches = new();
    // Per-player cached active spore/fungal aura contribution (empty outside active spores/fungal ground -> O(1) FinalStatsBuilding).
    sealed record PlayerSporeState(IReadOnlyDictionary<uint, int> Stats, bool OnFungalGround);
    static readonly ConcurrentDictionary<Player, PlayerSporeState> PlayerSporeStates = new();

    static readonly IReadOnlyDictionary<uint, int> EnemySlowStats =
        new Dictionary<uint, int> { [Stat.ActionSpeedMinusPct] = 25, [Stat.BaseMovementVelocityPlusPct] = -25 };

    static readonly IReadOnlyDictionary<uint, int> FungalEnemyDebuffStats =
        new Dictionary<uint, int> { [Stat.DamagePlusPct] = -10 };

    /// <summary>Rolls one weighted random aura from <c>BlightedSporeAuras</c> (rows 0..8).</summary>
    public static BlightTables.SporeAura? RollSporeAura(Random rng)
    {
        var auras = BlightTables.SporeAuras;
        if (auras.Count == 0) return null;
        int total = auras.Sum(a => Math.Max(1, a.Weight));
        int roll = rng.Next(total);
        foreach (var aura in auras)
            if ((roll -= Math.Max(1, aura.Weight)) < 0) return aura;
        return auras[^1];
    }

    /// <summary>Stat bonuses granted to players standing inside a <c>BlightedSporeAuras</c> spore (mapped from BuffDefinitions rows).</summary>
    public static IReadOnlyDictionary<uint, int> SporeStatsFor(BlightTables.SporeAura aura)
    {
        var s = new Dictionary<uint, int>();
        if (aura.AffectsEnemies) return s;
        // BuffStatValues from BlightedSporeAuras (rows 0..8: speed, dmg, crit, regen, phys, resists, ES, cannot_die).
        int v0 = aura.StatValues.Count > 0 ? aura.StatValues[0] : 25;
        int v1 = aura.StatValues.Count > 1 ? aura.StatValues[1] : v0;
        switch (aura.Row)
        {
            case 0: // Speed aura
                s[Stat.AttackAndCastSpeedPlusPct] = Math.Max(15, v0);
                s[Stat.BaseMovementVelocityPlusPct] = Math.Max(15, v1);
                break;
            case 1: // Damage aura
                s[Stat.DamagePlusPct] = Math.Max(30, v0);
                break;
            case 2: // Crit aura
                s[Stat.CriticalStrikeChancePlusPct] = Math.Max(100, v0);
                break;
            case 3: // Life regeneration aura
                s[Stat.LifeRegenerationRatePerMinutePct] = Math.Max(300, v0);
                break;
            case 4: // Physical / armour aura
                s[Stat.PhysicalDamagePlusPct] = Math.Max(30, v0);
                break;
            case 5: // Elemental resistances aura
                s[Stat.BaseResistAllElementsPct] = Math.Max(25, v0);
                break;
            case 6: // Energy shield aura
                s[Stat.MaximumEnergyShieldPlusPct] = Math.Max(25, v0);
                break;
            case 7: // Cannot die aura
                s[Stat.CannotDie] = 1;
                break;
            default:
                s[Stat.DamagePlusPct] = Math.Max(20, v0);
                break;
        }
        return s;
    }

    /// <summary>Returns whether cell <paramref name="at"/> is currently covered by any active Fungal Ground patch in <paramref name="instance"/>.</summary>
    public static bool IsOnFungalGround(InstanceState? instance, (int X, int Y) at, double now)
    {
        if (instance is null || !FungalPatches.TryGetValue(instance, out var list)) return false;
        lock (list)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (now >= list[i].Until) { list.RemoveAt(i); continue; }
                double dx = list[i].X - at.X, dy = list[i].Y - at.Y;
                if (dx * dx + dy * dy <= (double)list[i].Radius * list[i].Radius) return true;
            }
        }
        return false;
    }

    /// <summary>Spawns a Blighted Spore at <paramref name="at"/> for 10 seconds (called on Rare kill with Breathstealer or directly by tests).</summary>
    public static ActiveSpore? SpawnSpore(InstanceState instance, (int X, int Y) at, double now, Random rng)
    {
        if (RollSporeAura(rng) is not { } aura) return null;
        var spore = new ActiveSpore(at.X, at.Y, now + SporeDurationSeconds, aura);
        var list = Spores.GetOrAdd(instance, _ => []);
        lock (list)
        {
            list.RemoveAll(s => now >= s.Until);
            list.Add(spore);
        }
        return spore;
    }

    /// <summary>Creates a Fungal Ground patch at <paramref name="at"/> for <paramref name="duration"/> seconds.</summary>
    public static FungalPatch CreateFungalGround(InstanceState instance, (int X, int Y) at, int radius, double now,
        double duration = FungalGroundDurationSeconds)
    {
        var patch = new FungalPatch(at.X, at.Y, Math.Max(8, radius), now + duration);
        var list = FungalPatches.GetOrAdd(instance, _ => []);
        lock (list)
        {
            list.RemoveAll(p => now >= p.Until);
            list.Add(patch);
        }
        return patch;
    }

    /// <summary>GameEvents.Kill: handles Breathstealer spore spawn on Rare kill and Sporeguard Fungal Ground + 10% Chaos explosion.</summary>
    public static void OnKill(KillContext kill)
    {
        if (!kill.PlayerKill) return;
        var player = kill.Player;
        var instance = player.Instance;
        if (instance is null) return;
        var stats = PlayerStatValues(player);

        bool breathstealer = kill.Monster.Rarity == MonsterRarity.Rare
            && stats.GetValueOrDefault(Stat.CreateBlightedSporeOnKillingRareEnemy) > 0;
        bool fungalOnKill = stats.GetValueOrDefault(Stat.LocalDisplayTriggerLevelXCreateFungalGroundOnKill) > 0
            || stats.GetValueOrDefault(Stat.CreateFungalGroundInsteadOfConsecratedGround) > 0;
        int chaosExplodeChance = stats.GetValueOrDefault(Stat.EnemiesKilledOnFungalGroundExplodeFor10PctChaosDamagePctChance);
        if (!breathstealer && !fungalOnKill && chaosExplodeChance <= 0) return;

        double now = AreaTime(player);
        var rng = AreaRng(player);

        // Check whether victim died on Fungal Ground (existing patch or stationary Sporeguard wearer's radius).
        bool victimOnFungal = false;
        if (chaosExplodeChance > 0)
        {
            victimOnFungal = IsOnFungalGround(instance, kill.At, now);
            if (!victimOnFungal)
            {
                int stationaryRadius = stats.GetValueOrDefault(Stat.FungalGroundWhileStationaryRadius);
                if (stationaryRadius > 0 && now >= player.Walk.ArrivesAt)
                {
                    var p = player.Walk.Position(now);
                    double dx = p.X - kill.At.X, dy = p.Y - kill.At.Y;
                    victimOnFungal = dx * dx + dy * dy <= (double)stationaryRadius * stationaryRadius;
                }
            }
        }

        if (breathstealer)
            SpawnSpore(instance, kill.At, now, rng);

        if (fungalOnKill)
            CreateFungalGround(instance, kill.At, FungalGroundKillRadius, now);

        if (victimOnFungal && chaosExplodeChance > 0 && rng.Next(100) < chaosExplodeChance)
        {
            int chaosDmg = (int)Math.Clamp(Math.Round(kill.Monster.MaxLife * 0.10), 1, int.MaxValue);
            kill.Later(() => ExplodeFungalCorpse(player, kill.Monster, kill.At, chaosDmg));
        }
    }

    static async Task ExplodeFungalCorpse(Player player, SpawnedMonster corpse, (int X, int Y) at, int rawChaos)
    {
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0) return;
        double now = AreaTime(player);
        var targets = new List<(uint Id, SpawnedMonster M)>();
        foreach (var (id, m) in player.Area.Monsters.ToArray())
        {
            if (!m.Hostile || m.Dead || m.Life == 0 || ReferenceEquals(m, corpse)) continue;
            var pos = m.Walk.Position(now);
            double dx = pos.X - at.X, dy = pos.Y - at.Y;
            if (dx * dx + dy * dy <= FungalExplosionRadius * FungalExplosionRadius)
                targets.Add((id, m));
        }
        foreach (var (id, m) in targets)
            await HitMonster(player, id, m, rawChaos, "Sporeguard Fungal Explosion", log: false);
    }

    /// <summary>Updates player spore/fungal bonuses and enemy fungal/spore-slow debuffs in one tick (O(1) no-op when empty).</summary>
    public static Task Tick(Player player, double dt)
    {
        var instance = player.Instance;
        if (instance is null || player.Closed || player.LeavingArea) return Task.CompletedTask;
        bool hasSpores = Spores.TryGetValue(instance, out var sporeList);
        bool hasFungal = FungalPatches.TryGetValue(instance, out var fungalList);
        int stationaryRadius = PlayerStatValues(player).GetValueOrDefault(Stat.FungalGroundWhileStationaryRadius);
        if (!hasSpores && !hasFungal && stationaryRadius <= 0)
        {
            PlayerSporeStates.TryRemove(player, out _);
            return Task.CompletedTask;
        }

        double now = AreaTime(player);
        var pos = player.Walk.Position(now);
        var merged = new Dictionary<uint, int>();
        List<ActiveSpore> liveSpores = [];
        if (hasSpores && sporeList is not null)
        {
            lock (sporeList)
            {
                sporeList.RemoveAll(s => now >= s.Until);
                liveSpores.AddRange(sporeList);
            }
            foreach (var sp in liveSpores)
            {
                if (sp.Aura.AffectsEnemies) continue;
                double dx = sp.X - pos.X, dy = sp.Y - pos.Y;
                if (dx * dx + dy * dy <= (double)sp.Aura.Radius * sp.Aura.Radius)
                    foreach (var (k, v) in SporeStatsFor(sp.Aura))
                        merged[k] = merged.GetValueOrDefault(k) + v;
            }
        }

        bool onFungal = IsOnFungalGround(instance, pos, now)
            || (stationaryRadius > 0 && now >= player.Walk.ArrivesAt);
        if (onFungal)
        {
            merged[Stat.IsOnFungalGround] = 1;
            merged[Stat.BaseDamageTakenPlusPct] = merged.GetValueOrDefault(Stat.BaseDamageTakenPlusPct) - 10;
        }

        if (merged.Count > 0 || onFungal)
            PlayerSporeStates[player] = new PlayerSporeState(merged, onFungal);
        else
            PlayerSporeStates.TryRemove(player, out _);

        // Apply enemy-affecting spores (monster_aura_slow) and Fungal Ground enemy debuff (-10% damage dealt).
        if (IsMonsterRunner(player) && (liveSpores.Any(s => s.Aura.AffectsEnemies) || hasFungal))
        {
            foreach (var (_, m) in player.Area.Monsters)
            {
                if (!m.Hostile || m.Dead || m.Life == 0) continue;
                var mp = m.Walk.Position(now);
                bool slowed = false;
                foreach (var sp in liveSpores)
                {
                    if (!sp.Aura.AffectsEnemies) continue;
                    double dx = sp.X - mp.X, dy = sp.Y - mp.Y;
                    if (dx * dx + dy * dy <= (double)sp.Aura.Radius * sp.Aura.Radius) { slowed = true; break; }
                }
                if (slowed) m.SetTemporaryStats(SporeSlowKey, EnemySlowStats);
                else m.RemoveTemporaryStats(SporeSlowKey);

                if (IsOnFungalGround(instance, mp, now)) m.SetTemporaryStats(FungalGroundEnemyKey, FungalEnemyDebuffStats);
                else m.RemoveTemporaryStats(FungalGroundEnemyKey);
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>GameEvents.FinalStatsBuilding: adds active Breathstealer Spore &amp; Fungal Ground stats (O(1) no-op otherwise).</summary>
    public static void ApplyStats(Player player, Dictionary<uint, int> stats)
    {
        if (PlayerSporeStates.IsEmpty || !PlayerSporeStates.TryGetValue(player, out var state)) return;
        foreach (var (k, v) in state.Stats)
            stats[k] = stats.GetValueOrDefault(k) + v;
    }

    public static Task ClearPlayer(Player player)
    {
        PlayerSporeStates.TryRemove(player, out _);
        return Task.CompletedTask;
    }

    public static void ClearAll()
    {
        Spores.Clear();
        FungalPatches.Clear();
        PlayerSporeStates.Clear();
    }

    /// <summary>Returns active spores in an instance (for diagnostics and unit tests).</summary>
    public static IReadOnlyList<ActiveSpore> ActiveSporesIn(InstanceState instance, double now)
    {
        if (!Spores.TryGetValue(instance, out var list)) return [];
        lock (list)
        {
            list.RemoveAll(s => now >= s.Until);
            return [.. list];
        }
    }
}
