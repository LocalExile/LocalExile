using System.Collections.Concurrent;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Modules.Blight;

/// <summary>
/// Blight Unique item mechanics and the Prismatic Oil keystone:
/// - <b>Breathstealer</b> (<c>create_blighted_spore_on_killing_rare_enemy</c>): killing a Rare monster spawns a 10-second
///   Blighted Spore carrying a weighted random aura from <c>BlightedSporeAuras</c> joined directly with <c>BuffDefinitions.StatsKeys</c>
///   (radius 60: speed, medium/large phys, life regen, resists, life-as-ES, accuracy/crits, cannot die, or enemy slow).
/// - <b>Sporeguard</b> (<c>fungal_ground_while_stationary_radius</c>, <c>local_display_trigger_level_x_create_fungal_ground_on_kill</c>,
///   <c>enemies_killed_on_fungal_ground_explode_for_10%_chaos_damage_%_chance</c>): creates Fungal Ground on kill / while stationary
///   (enemies on Fungal Ground deal 10% less damage; player takes 10% less damage) and explodes enemies killed on Fungal Ground for
///   10% of their maximum life as mitigated Chaos damage (consuming the corpse with purple <c>ChaosExplosionAo</c> art).
/// - <b>Worship the Blightheart</b> (<c>create_fungal_ground_instead_of_consecrated_ground</c>): treats the player's active
///   Consecrated Ground zones (<c>player.FlaskGrounds</c>) as Fungal Ground for both player bonuses, enemy debuffs, and corpse explosions.
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

    // Instance-scoped Stores (ConditionalWeakTable) so closed instances are automatically reclaimed by GC.
    static readonly Store<InstanceState, List<ActiveSpore>> Spores = new();
    static readonly Store<InstanceState, List<FungalPatch>> FungalPatches = new();
    // Per-player cached active spore/fungal aura contribution (empty outside active spores/fungal ground -> O(1) FinalStatsBuilding).
    sealed record PlayerSporeState(IReadOnlyDictionary<uint, int> Stats, bool OnFungalGround);
    static readonly ConcurrentDictionary<Player, PlayerSporeState> PlayerSporeStates = new();

    static readonly IReadOnlyDictionary<uint, int> EnemySlowStats =
        new Dictionary<uint, int> { [Stat.BaseMovementVelocityPlusPct] = -25 };

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

    /// <summary>Exact stat bonuses granted to players standing inside a <c>BlightedSporeAuras</c> spore
    /// (100% data-driven from <c>BuffDefinitions.StatsKeys</c> × <c>BlightedSporeAuras.BuffStatValues</c>).</summary>
    public static IReadOnlyDictionary<uint, int> SporeStatsFor(BlightTables.SporeAura aura)
    {
        var s = new Dictionary<uint, int>();
        if (aura.AffectsEnemies) return s;
        foreach (var (statId, value) in aura.Stats)
            if (statId != 0 && value != 0)
                s[statId] = s.GetValueOrDefault(statId) + value;
        return s;
    }

    /// <summary>Returns whether cell <paramref name="at"/> is currently covered by any active Fungal Ground patch in <paramref name="instance"/>.</summary>
    public static bool IsOnFungalGround(InstanceState? instance, (int X, int Y) at, double now)
    {
        if (instance is null || !SporesOrPatches(FungalPatches, instance, out var list)) return false;
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

    static bool SporesOrPatches<T>(Store<InstanceState, List<T>> store, InstanceState instance, out List<T> list) =>
        store.TryGetValue(instance, out list!);

    /// <summary>Returns whether cell <paramref name="at"/> sits inside a Consecrated Ground zone converted into Fungal Ground
    /// by the Prismatic Oil keystone <b>Worship the Blightheart</b> (<c>create_fungal_ground_instead_of_consecrated_ground</c>).</summary>
    static bool IsOnConvertedConsecratedGround(Player player, (int X, int Y) at, double now, IReadOnlyDictionary<uint, int>? stats = null)
    {
        if (!player.FlaskGrounds.Any) return false;
        stats ??= PlayerStatValues(player);
        if (stats.GetValueOrDefault(Stat.CreateFungalGroundInsteadOfConsecratedGround) <= 0) return false;
        foreach (var ground in player.FlaskGrounds.Grounds)
        {
            if (ground.Kind != FlaskGrounds.Kind.ConsecratedGround || now >= ground.Ends) continue;
            double dx = ground.X - at.X, dy = ground.Y - at.Y;
            if (dx * dx + dy * dy <= (double)ground.Radius * ground.Radius) return true;
        }
        return false;
    }

    /// <summary>Spawns a Blighted Spore at <paramref name="at"/> for 10 seconds (called on Rare kill with Breathstealer or directly by tests).</summary>
    public static ActiveSpore? SpawnSpore(InstanceState instance, (int X, int Y) at, double now, Random rng)
    {
        if (RollSporeAura(rng) is not { } aura) return null;
        var spore = new ActiveSpore(at.X, at.Y, now + SporeDurationSeconds, aura);
        var list = Spores.GetValue(instance, _ => []);
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
        var list = FungalPatches.GetValue(instance, _ => []);
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
        bool fungalOnKill = stats.GetValueOrDefault(Stat.LocalDisplayTriggerLevelXCreateFungalGroundOnKill) > 0;
        int chaosExplodeChance = stats.GetValueOrDefault(Stat.EnemiesKilledOnFungalGroundExplodeFor10PctChaosDamagePctChance);
        if (!breathstealer && !fungalOnKill && chaosExplodeChance <= 0) return;

        double now = AreaTime(player);
        var rng = AreaRng(player);

        // Check whether victim died on Fungal Ground (existing patch, stationary Sporeguard radius, or Worship the Blightheart Consecrated Ground).
        bool victimOnFungal = false;
        if (chaosExplodeChance > 0)
        {
            victimOnFungal = IsOnFungalGround(instance, kill.At, now)
                || IsOnConvertedConsecratedGround(player, kill.At, now, stats);
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
            int rawChaos = (int)Math.Clamp(Math.Round(kill.Monster.MaxLife * 0.10), 1, int.MaxValue);
            kill.Later(() => ExplodeFungalCorpse(player, kill.Monster.ObjectId, kill.Monster, kill.At, rawChaos));
        }
    }

    static async Task ExplodeFungalCorpse(Player player, uint corpseId, SpawnedMonster corpse, (int X, int Y) at, int rawChaos)
    {
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0) return;
        double now = AreaTime(player);
        var packet = DamagePacket.Of(DamageType.Chaos, rawChaos, rawChaos);

        // Consume the exploded corpse (matching KillExplosions.cs) and emit ChaosExplosionAo purple blast visual.
        bool removeCorpse = !corpse.CorpseConsumed;
        corpse.CorpseConsumed = true;
        player.Area.Monsters.Remove(corpseId);
        if (removeCorpse)
        {
            await SendWorldRemoval(player, corpseId, $"0x02bd Sporeguard explosion consumed corpse 0x{corpseId:x}");
            await SendRemainingMonsters(player);
        }

        uint effectId = player.TakeObjectId();
        var effectBytes = InstanceEntities.CreateObject(effectId, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
            new EffectObject(at.X, at.Y, ChaosExplosionAo).Blob());
        await SendWorldCreate(player, effectId, () => effectBytes, $"0x02bb Sporeguard Chaos explosion at ({at.X}, {at.Y})", wake: true, awaitBirth: false);
        SendAfter(player, 2.0, async () =>
        {
            if (!player.Closed && !player.LeavingArea)
                await SendWorldRemoval(player, effectId, null);
        });

        var targets = new List<(uint Id, SpawnedMonster M, int Mitigated)>();
        foreach (var (id, m) in player.Area.Monsters.ToArray())
        {
            if (!m.Hostile || m.Dead || m.Life == 0 || ReferenceEquals(m, corpse)) continue;
            var pos = m.Walk.Position(now);
            double dx = pos.X - at.X, dy = pos.Y - at.Y;
            if (dx * dx + dy * dy > FungalExplosionRadius * FungalExplosionRadius) continue;
            // Secondary Chaos explosion mitigated by monster Chaos Resistance and increased damage taken.
            var (mitigated, _, _) = MonsterHitDamage(m, packet, critical: false, attack: false, now, default);
            if (mitigated > 0) targets.Add((id, m, mitigated));
        }
        foreach (var (id, m, mitigated) in targets)
            await HitMonster(player, id, m, mitigated, "Sporeguard Fungal Chaos Explosion", log: false);
    }

    /// <summary>Updates player spore/fungal bonuses and enemy fungal/spore-slow debuffs in one tick (O(1) no-op when empty).</summary>
    public static Task Tick(Player player, double dt)
    {
        var instance = player.Instance;
        if (instance is null || player.Closed || player.LeavingArea) return Task.CompletedTask;
        bool hasSpores = Spores.TryGetValue(instance, out var sporeList);
        bool hasFungal = FungalPatches.TryGetValue(instance, out var fungalList);
        var pStats = PlayerStatValues(player);
        int stationaryRadius = pStats.GetValueOrDefault(Stat.FungalGroundWhileStationaryRadius);
        bool worshipBlightheart = player.FlaskGrounds.Any
            && pStats.GetValueOrDefault(Stat.CreateFungalGroundInsteadOfConsecratedGround) > 0;
        if (!hasSpores && !hasFungal && stationaryRadius <= 0 && !worshipBlightheart)
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

        bool stationaryFungal = stationaryRadius > 0 && now >= player.Walk.ArrivesAt;
        bool onFungal = IsOnFungalGround(instance, pos, now)
            || stationaryFungal
            || (worshipBlightheart && IsOnConvertedConsecratedGround(player, pos, now, pStats));
        if (onFungal)
        {
            merged[Stat.IsOnFungalGround] = 1;
            merged[Stat.BaseDamageTakenPlusPct] = merged.GetValueOrDefault(Stat.BaseDamageTakenPlusPct) - 10;
        }

        if (merged.Count > 0 || onFungal)
            PlayerSporeStates[player] = new PlayerSporeState(merged, onFungal);
        else
            PlayerSporeStates.TryRemove(player, out _);

        // Apply enemy-affecting spores (monster_aura_slow) and Fungal Ground enemy debuff (-10% damage dealt)
        // from patches, stationary Sporeguard wearers, and Worship the Blightheart Consecrated Ground zones.
        bool anyEnemyFungalSource = hasFungal || stationaryFungal || worshipBlightheart;
        if (IsMonsterRunner(player) && (liveSpores.Any(s => s.Aura.AffectsEnemies) || anyEnemyFungalSource))
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

                bool enemyOnFungal = IsOnFungalGround(instance, mp, now)
                    || (worshipBlightheart && IsOnConvertedConsecratedGround(player, mp, now, pStats));
                if (!enemyOnFungal && stationaryFungal)
                {
                    double dx = pos.X - mp.X, dy = pos.Y - mp.Y;
                    enemyOnFungal = dx * dx + dy * dy <= (double)stationaryRadius * stationaryRadius;
                }
                if (enemyOnFungal) m.SetTemporaryStats(FungalGroundEnemyKey, FungalEnemyDebuffStats);
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
