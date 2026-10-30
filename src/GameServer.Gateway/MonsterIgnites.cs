using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

/// <summary>
/// The ignites the player's hits put on one monster (wiki Ignite): each burns for its own duration, and only the
/// strongest at a moment deals damage. Guarded by the Monsters lock, on the connection's pauseable clock.
/// </summary>
sealed class MonsterIgnites
{
    readonly record struct Effect(double From, double Until, double PerSecond);
    readonly List<Effect> effects = [];
    double carry;
    /// <summary>The <c>ignited</c> buff instance the client shows while one burns.</summary>
    public ushort? Shown;

    public double PerSecond(double now) =>
        effects.Where(e => e.From <= now && now < e.Until).Select(e => e.PerSecond).DefaultIfEmpty().Max();

    public bool Burning(double now) => PerSecond(now) > 0;

    public bool Idle => effects.Count == 0 && Shown is null;

    public void Add(double now, DamageOverTime ignite)
    {
        if (ignite.DamagePerSecond > 0 && ignite.RemainingSeconds > 0)
            effects.Add(new(now, now + ignite.RemainingSeconds, ignite.DamagePerSecond));
    }

    public void Clear() { effects.Clear(); carry = 0; }

    /// <summary>The strongest ignite's damage from <paramref name="from"/> to <paramref name="until"/>, through every start
    /// and end between (a weaker, longer one burns on after the strongest ends), × <paramref name="taken"/> (the monster's
    /// fire mitigation): the whole points, the fraction carried to the next call.</summary>
    public int Advance(double from, double until, double taken)
    {
        if (until <= from || effects.Count == 0) return 0;
        var bounds = effects.SelectMany(e => new[] { e.From, e.Until }).Where(t => from < t && t < until)
            .Append(from).Append(until).Distinct().Order().ToArray();
        double damage = carry;
        for (int i = 1; i < bounds.Length; i++)
            damage += PerSecond((bounds[i - 1] + bounds[i]) / 2) * taken * (bounds[i] - bounds[i - 1]);
        effects.RemoveAll(e => e.Until <= until);
        int whole = (int)Math.Min(int.MaxValue, Math.Floor(damage));
        carry = effects.Count == 0 ? 0 : damage - whole;
        return whole;
    }
}

partial class Program
{
    // BuffDefinitions 0 `ignited` ("You're on fire", BuffVisuals 2): not IsRecovery and no +0xff bool, so the 0x1c1 carries
    // no values. Without a duration: the server takes it off (0x1c2), so a pause cannot desync it.
    const ushort IgnitedBuffRow = 0;

    /// <summary>The wiki's Ignite: damaging ailments from critical strikes have +50% to Damage over Time Multiplier,
    /// additive with the others (Character.ot's critical_ailment_dot_multiplier_+; the player stats do not carry it).</summary>
    internal const int CriticalAilmentDotMultiplier = 50;

    // The increases the wiki's Ignite lists (Damage, Damage over Time, Elemental, Fire, Burning, Ignite Damage) and
    // ailment damage; hit-only ones (spell, area, attack, melee damage) never reach an ailment.
    static readonly string[] IgniteIncreases = ["damage_+%", "damage_over_time_+%", "elemental_damage_+%", "fire_damage_+%",
        "fire_damage_over_time_+%", "burn_damage_+%", "ignite_damage_+%", "base_ailment_damage_+%"];

    /// <summary>
    /// The ignite one of the player's hits inflicts on <paramref name="m"/>, or null (wiki Ignite). The hit needs fire
    /// damage (<paramref name="baseFire"/>: its base and added fire damage after conversion, before any increase) and a
    /// critical strike (always ignites) or the chance (base_chance_to_ignite_%, chance_to_ignite_%,
    /// chance_to_freeze_shock_ignite_%); never_ignite stops it. It burns 90% of that fire per second (Ailments.Ignite) ×
    /// the ignite increases × <paramref name="more"/> × the damage over time multipliers (with +50% from a critical strike),
    /// for 4 s × increased ignite duration × the monster's base_self_ignite_duration_-% (Fire and Ignite Resistant).
    /// faster_burn_% and damaging_ailments_deal_damage_+%_faster multiply its damage per second and divide its duration.
    /// <paramref name="attacker"/> gives the player's and the skill's stats.
    /// </summary>
    internal static DamageOverTime? IgniteFromHit(double baseFire, bool critical, Func<string, int> attacker, double more,
        SpawnedMonster m, Random random)
    {
        if (baseFire <= 0 || attacker("never_ignite") != 0 || attacker("never_ignite_chill_freeze_shock") != 0) return null;
        int chance = attacker("base_chance_to_ignite_%") + attacker("chance_to_ignite_%") + attacker("chance_to_freeze_shock_ignite_%");
        if (!critical && !RandomRolls.Chance(chance, 100, random)) return null;
        double multiplier = 1 + (attacker("dot_multiplier_+") + attacker("fire_dot_multiplier_+") + attacker("ignite_dot_multiplier_+")
            + (critical ? CriticalAilmentDotMultiplier + attacker("critical_ailment_dot_multiplier_+") : 0)) / 100.0;
        var ignite = GameServer.Core.World.Ailments.Ignite(DamagePacket.Of(DamageType.Fire, baseFire, baseFire),
            IgniteIncreases.Sum(attacker), more * multiplier);
        // Deal Damage faster: more damage per second, the inverse on the duration (wiki Ignite; PoB IgniteBurnFaster).
        double faster = Math.Max(0, 1 + (attacker("faster_burn_%") + attacker("damaging_ailments_deal_damage_+%_faster")) / 100.0);
        double seconds = faster > 0 ? ignite.RemainingSeconds * Math.Max(0, 1 + attacker("ignite_duration_+%") / 100.0)
            * Math.Max(0, 1 - m.Mods.Effects["base_self_ignite_duration_-%"] / 100.0) / faster : 0;
        return ignite.DamagePerSecond > 0 && seconds > 0
            ? ignite with { DamagePerSecond = ignite.DamagePerSecond * faster, RemainingSeconds = seconds } : null;
    }

    /// <summary>Puts the ignite on a living monster (under the Monsters lock): the buff instance to send when it shows the
    /// monster burning for the first time, else null.</summary>
    internal static ushort? IgniteLocked(ClientState conn, SpawnedMonster m, DamageOverTime ignite, double now)
    {
        if (m.Dead || m.Life == 0) return null;
        m.Ignites.Add(now, ignite);
        if (m.Ignites.Shown is not null || !m.Ignites.Burning(now)) return null;
        m.Ignites.Shown = conn.NextBuff++;
        return m.Ignites.Shown;
    }

    internal static byte[] IgnitedBuff(uint id, ushort instance) => InstanceEntities.AddBuff(id, instance, IgnitedBuffRow, PlayerId);

    /// <summary>
    /// The player's ignites burn their monsters for one player tick (<paramref name="seconds"/> to now): fire damage over
    /// time less the monster's fire resistance and damage taken (<see cref="MonsterDotTaken"/>), the new life (0x1b1) and a
    /// kill as Righteous Fire's burn has, and the <c>ignited</c> buff off once none burns or the monster died. They burn
    /// on while the player is dead (the kill then gives no experience, MonsterKilled).
    /// </summary>
    internal static async Task BurnIgnites(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, double seconds)
    {
        var hits = new List<(uint Id, SpawnedMonster M, uint Life, int Damage)>();
        var ended = new List<(uint Id, ushort Buff)>();
        using (ProjectileMutation(conn))
        {
            if (conn.Closed || conn.LeavingArea || seconds <= 0) return;
            double now = ProjectileWorldTime(conn);
            foreach (var (id, m) in conn.Monsters)
            {
                var ignites = m.Ignites;
                if (ignites.Idle) continue;
                int damage = m.Dead || m.Life == 0 ? 0 : ignites.Advance(now - seconds, now, MonsterDotTaken(m, DamageType.Fire, now));
                if (damage > 0)
                {
                    (m.Life, bool stageStarts) = TakeDamageLocked(m, damage);
                    m.Aggro = m.Life > 0;
                    if (m.Life == 0)
                    {
                        m.Dead = true;
                        CollectVaalSoulAtDeathLocked(conn, m);
                    }
                    hits.Add((id, m, m.Life, damage));
                    if (stageStarts) CommitBossStage(remote, stream, ciphers, conn, id, m, now);
                }
                if (m.Dead || m.Life == 0) ignites.Clear();
                if (!ignites.Burning(now) && ignites.Shown is ushort buff)
                {
                    ended.Add((id, buff));
                    ignites.Shown = null;
                }
            }
        }
        foreach (var (id, buff) in ended)
            await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(id, buff), null);
        foreach (var (id, m, life, damage) in hits)
        {
            if (life == 0)
                await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(id, [(StatKeys.HideMiniLifeBar, 1)]), null);
            await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(id, life),
                life == 0 ? $"0x01b1 monster 0x{id:x} ({m.Label}) burnt to death by an ignite" : null);
            if (life == 0) await MonsterKilled(remote, stream, ciphers, conn, id, m);
        }
    }
}
