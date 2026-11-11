using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;

/// <summary>
/// The Slayer's node effects that need state the stat sum does not have (Agents/docs/game/slayer.md): the counts the
/// client reads for Overwhelm and Impact, Headsman's on-kill buffs and Bane of Legends' damage. The client computes its
/// own panel from <c>number_of_nearby_enemies</c> and <c>number_of_enemies_killed_recently</c> (0x14276e1f0,
/// 0x14274c820) but never counts them: the server does and sends them, and the pools read the same values.
/// </summary>
public static class Slayer
{
    public const string MovementOnKill = "gain_movement_speed_+%_for_20_seconds_on_kill";
    public const string AttackSpeedOnRareKill = "gain_attack_speed_+%_for_20_seconds_on_killing_rare_or_unique_enemy";
    public const string CritPerNearbyEnemy = "slayer_critical_strike_multiplier_+_per_nearby_enemy_up_to_100";
    public const string AreaPerEnemyKilled = "slayer_area_of_effect_+%_per_enemy_killed_recently_up_to_50%";
    public const string MoreVsUnique = "slayer_damage_+%_final_against_unique_enemies";
    public const string MoreIfKilledRecently = "damage_+%_if_enemy_killed_recently_final";
    public const string NearbyEnemies = "number_of_nearby_enemies", EnemiesKilledRecently = "number_of_enemies_killed_recently",
        HaveKilledRecently = "have_killed_recently";

    /// <summary>"for 20 seconds" (both Headsman stats' descriptions).</summary>
    public const double HeadsmanSeconds = 20;

    /// <summary>"Nearby": 60 cells (6 m), the radius of Overwhelm's own enemy aura (BuffTemplates SlayerCritMultiplier).
    /// The client does not count nearby enemies, so the radius is not in its code.</summary>
    public const int NearbyRadius = 60;

    // BuffDefinitions bane_of_legends_movement_speed and bane_of_legends_attack_speed: invisible, not IsRecovery (no
    // values in 0x1c1), CancelOnDeath. Headsman was once called Bane of Legends.
    const ushort MovementBuffRow = 1341, AttackSpeedBuffRow = 1342;

    /// <summary>Overwhelm's crit multiplier: +X per nearby enemy, the product capped at +100 (client 0x14276e1f0:
    /// <c>min(100, S[26a6] × S[18a1])</c>, no lower bound).</summary>
    public static long CritMultiplierFromNearby(Func<string, int> get) =>
        Math.Min(100, (long)get(CritPerNearbyEnemy) * get(NearbyEnemies));

    /// <summary>Impact's increased area: X per enemy killed recently, the product capped at 50 (client 0x14274c820,
    /// RE area-increased-terms §4: <c>min(50, S[26a2] × S[1f22])</c>).</summary>
    public static long AreaFromKills(Func<string, int> get) =>
        Math.Min(50, (long)get(AreaPerEnemyKilled) * get(EnemiesKilledRecently));

    /// <summary>A count stopped where the capped product above stops changing (10 enemies at +10 per enemy, 10 kills at
    /// 5% per kill). The tick compares these to decide a resend; the counts it sends stay uncapped, as other stats read
    /// them with their own caps (the client reads number_of_nearby_enemies in about ten calculators).</summary>
    public static int UpToCap(int count, int perUnit, int cap) =>
        perUnit > 0 ? Math.Min(count, (cap + perUnit - 1) / perUnit) : count;

    // Other stats the client reads in the same calculators as number_of_nearby_enemies (0x18a1: 0x14251aa40,
    // 0x142525b99, 0x142710f20, 0x142753203, 0x14285d770) or number_of_enemies_killed_recently (0x1f22, area
    // 0x14274c820), each with its own cap or threshold.
    static readonly string[] OtherNearbyReaders =
    [
        "physical_damage_reduction_%_per_nearby_enemy", "movement_speed_+%_per_nearby_enemy",
        "movement_speed_+%_per_nearby_enemy_up_to_50%", "attack_and_cast_speed_+%_per_nearby_enemy_up_to_30%",
        "additional_block_%_with_5_or_more_nearby_enemies", "self_take_no_extra_damage_from_critical_strikes_if_only_one_nearby_enemy",
    ];
    static readonly string[] OtherKillReaders =
        ["area_of_effect_+%_per_enemy_killed_recently", "area_of_effect_+%_per_enemy_killed_recently_up_to_25%"];

    /// <summary>Bane of Legends' two more multipliers on one hit, against a unique target or not.</summary>
    public static double DamageMore(Func<string, int> get, bool uniqueTarget) =>
        (uniqueTarget ? Math.Max(0, 1 + get(MoreVsUnique) / 100.0) : 1)
        * (get(HaveKilledRecently) != 0 ? Math.Max(0, 1 + get(MoreIfKilledRecently) / 100.0) : 1);

    public sealed class State
    {
        public readonly Queue<double> Kills = new();
        public double MovementUntil = double.NegativeInfinity, AttackSpeedUntil = double.NegativeInfinity;
        public double MovementShown, AttackSpeedShown;
        public ushort MovementBuff, AttackSpeedBuff;
        // What the stat sum adds now (PlayerStats). Only the tick changes them, so pools and client switch together.
        public int Nearby, Killed, Movement, AttackSpeed;
        // Counts each applied change: the player stats cache (PlayerStatsKey) rebuilds on it, as the pools do not hold
        // everything these values change (the area total).
        public long Revision;
        // Bane of Legends' last logged multiplier: an area hit changes target on every hit, so only a new value logs.
        public double BaneLogged = 1;

        public int KilledWithin(double now)
        {
            while (Kills.Count > 0 && now - Kills.Peek() >= RecentEvents.WindowSeconds) Kills.Dequeue();
            return Kills.Count;
        }
    }

    static readonly ConditionalWeakTable<ClientState, State> States = new();
    public static State Of(ClientState conn) => States.GetValue(conn, _ => new());

    public static long Revision(ClientState conn) => States.TryGetValue(conn, out var state) ? state.Revision : 0;

    // The passive and item sum (the stat sources cache), without what PlayerStats adds.
    static int Raw(ClientState conn, string name) => Stats.Key(name) is uint key && conn.StatSourcesCache is { } cached
        ? cached.Sources.GetValueOrDefault(key) : 0;

    /// <summary>A kill of the player's (GameEvents.Kill): it counts as killed recently, and starts or refreshes
    /// Headsman's buffs (attack speed only for a rare or unique enemy). The tick applies them.</summary>
    public static void Kill(KillContext kill)
    {
        if (!kill.PlayerKill) return;
        var conn = kill.Conn;
        lock (conn.Monsters)
        {
            if (conn.PlayerLife == 0) return;
            double now = conn.Clock.Elapsed.TotalSeconds;
            var state = Of(conn);
            state.Kills.Enqueue(now);
            if (Raw(conn, MovementOnKill) != 0) state.MovementUntil = now + HeadsmanSeconds;
            if (Raw(conn, AttackSpeedOnRareKill) != 0 && kill.Monster.Rarity is MonsterRarity.Rare or MonsterRarity.Unique)
                state.AttackSpeedUntil = now + HeadsmanSeconds;
        }
    }

    /// <summary>GameEvents.PlayerStatSources: the counts and Headsman's speeds as last applied.</summary>
    public static Dictionary<uint, int> PlayerStats(ClientState conn, Dictionary<uint, int> stats)
    {
        if (!States.TryGetValue(conn, out var state)) return stats;
        void Add(string name, int value)
        {
            if (value != 0 && Stats.Key(name) is uint k)
                stats[k] = (int)Math.Clamp((long)stats.GetValueOrDefault(k) + value, int.MinValue, int.MaxValue);
        }
        Add(NearbyEnemies, state.Nearby);
        Add(EnemiesKilledRecently, state.Killed);
        Add(HaveKilledRecently, state.Killed > 0 ? 1 : 0);
        // Impact's area joins the base form, which the panel's total and every skill read (as Rampage's does): the
        // client takes the player's stats as sent and evaluates none of them (04-player-entity.md §4.1a).
        Add(SkillArea.BaseIncreased, (int)AreaFromKills(name => Stats.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0));
        Add("base_movement_velocity_+%", state.Movement);
        Add("attack_speed_+%", state.AttackSpeed);
        return stats;
    }

    /// <summary>GameEvents.OutgoingDamage: Bane of Legends' more damage on the rolled hit, which its ailments read.</summary>
    public static DamagePacket OutgoingDamage(ClientState conn, IReadOnlyDictionary<uint, int> player, SpawnedMonster target,
        DamagePacket rolled)
    {
        int Get(string name) => Stats.Key(name) is uint key ? player.GetValueOrDefault(key) : 0;
        double more = DamageMore(Get, target.Rarity == MonsterRarity.Unique);
        var state = Of(conn);
        if (state.BaneLogged == more) return more == 1 ? rolled : rolled.Scale(more);
        state.BaneLogged = more;
        if (more == 1) return rolled;
        Console.WriteLine($"[gateway] Bane of Legends: {more:0.###}x on {target.Rarity} {target.Label}"
            + $" ({(target.Rarity == MonsterRarity.Unique ? "unique" : "not unique")}, killed recently: {Get(HaveKilledRecently) != 0})");
        return rolled.Scale(more);
    }

    /// <summary>The player tick: count the hostile enemies near the living player and those killed in the last four
    /// seconds (only with a stat that reads the count), end Headsman's buffs at their time or at death, and when a value
    /// changed rebuild the pools and send the stats and skills.</summary>
    public static async Task Tick(ClientState conn)
    {
        if (!conn.PlayerSent || conn.Closed || conn.LeavingArea) return;
        bool changed;
        var packets = new List<byte[]>();
        lock (conn.Monsters)
        {
            var state = Of(conn);
            double now = conn.Clock.Elapsed.TotalSeconds;
            bool alive = conn.PlayerLife > 0;
            if (!alive)
            {
                state.MovementUntil = state.AttackSpeedUntil = double.NegativeInfinity;
                state.Kills.Clear();
            }
            int killed = state.KilledWithin(now), areaPerKill = Raw(conn, AreaPerEnemyKilled);
            bool readsKills = areaPerKill != 0 || Raw(conn, MoreIfKilledRecently) != 0;
            if (!readsKills) killed = 0;
            int nearby = 0, critPerEnemy = Raw(conn, CritPerNearbyEnemy);
            if (alive && critPerEnemy != 0)
            {
                // Awake hostile monsters the client has been shown, as Flicker Strike's targets (FlickerDestination).
                var here = conn.Walk.Position(now);
                foreach (var (id, m) in conn.Monsters)
                    if (m is { Hostile: true, Dead: false, Life: > 0 } && !conn.AsleepMonsters.Contains(id)
                        && (conn.Instance is null || conn.MonsterViews.ContainsKey(id))
                        && Dist(m.Walk.Position(now), here) <= NearbyRadius) nearby++;
            }
            int movement = now < state.MovementUntil ? Raw(conn, MovementOnKill) : 0;
            int attackSpeed = now < state.AttackSpeedUntil ? Raw(conn, AttackSpeedOnRareKill) : 0;
            // The counts sent are the real ones; a resend happens when a value one of the player's stats reads changes.
            // With only Slayer's stats that is each count up to where its capped product stops (at one for Bane's
            // "killed recently" alone); another stat reading a count makes every change of it count.
            bool otherNearby = OtherNearbyReaders.Any(n => Raw(conn, n) != 0), otherKills = OtherKillReaders.Any(n => Raw(conn, n) != 0);
            int Near(int count) => otherNearby ? count : UpToCap(count, critPerEnemy, 100);
            int Kills(int count) => otherKills ? count : areaPerKill != 0 ? UpToCap(count, areaPerKill, 50) : Math.Min(count, 1);
            changed = (Near(nearby), Kills(killed), movement, attackSpeed)
                != (Near(state.Nearby), Kills(state.Killed), state.Movement, state.AttackSpeed);
            Present(conn, ref state.MovementBuff, ref state.MovementShown, MovementBuffRow, movement != 0, state.MovementUntil, now, packets);
            Present(conn, ref state.AttackSpeedBuff, ref state.AttackSpeedShown, AttackSpeedBuffRow, attackSpeed != 0,
                state.AttackSpeedUntil, now, packets);
            if (changed) (state.Nearby, state.Killed, state.Movement, state.AttackSpeed) = (nearby, killed, movement, attackSpeed);
            if (changed)
            {
                state.Revision++;
                RecomputePoolsAndSpeed(conn);
                int Stat(string name) => Stats.Key(name) is uint key ? CharacterPlayerStats(conn).GetValueOrDefault(key) : 0;
                Console.WriteLine($"[gateway] Slayer: {nearby} nearby enemies, {killed} killed recently, Headsman +{movement}% "
                    + $"movement +{attackSpeed}% attack speed; crit multiplier {conn.Pools.CriticalStrikeMultiplier}%, "
                    + $"area +{Stat("skill_area_of_effect_+%")}%");
            }
        }
        var (remote, stream, ciphers) = conn.Wire;
        foreach (var packet in packets) await Send(remote, stream, ciphers, packet, null);
        if (changed) await SendCharacterStats(remote, stream, ciphers, conn, "Slayer counts or Headsman changed");
    }

    // A Headsman buff: shown for its time left while on, shown again when a kill moved its deadline, taken off when it
    // ends. Its stats are in PlayerStats, not in the buff.
    static void Present(ClientState conn, ref ushort buff, ref double shown, ushort row, bool on, double until, double now,
        List<byte[]> packets)
    {
        if (buff != 0 && (!on || shown != until))
        {
            packets.Add(InstanceEntities.RemoveBuff(PlayerId, buff));
            buff = 0;
        }
        if (!on || buff != 0) return;
        buff = conn.NextBuff++;
        shown = until;
        packets.Add(InstanceEntities.AddBuff(PlayerId, buff, row, PlayerId,
            u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, until - now))));
    }

    /// <summary>A new area is a new connection: the leaving one forgets its state.</summary>
    public static Task Leaving(ClientState conn)
    {
        States.Remove(conn);
        return Task.CompletedTask;
    }
}
