using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

static partial class Game
{
    // The "more" support stats that scale Essence Drain's debuff: a projectile spell's chaos damage over time.
    static readonly string[] EssenceDrainSupportMore =
    [
        "support_controlled_destruction_spell_damage_+%_final", "support_void_manipulation_chaos_damage_+%_final",
        "support_rapid_decay_damage_over_time_+%_final", "support_efficacy_spell_damage_+%_final",
        "support_efficacy_damage_over_time_+%_final", "support_phys_chaos_projectile_chaos_damage_over_time_+%_final",
        "cruelty_damage_over_time_+%_final",
    ];

    /// <summary>Essence Drain (GrantedEffects <c>EssenceDrain</c>) and its transfigured versions: the debuff one projectile
    /// hit puts on the monster; null for any other skill. Its damage is base_chaos_damage_to_deal_per_minute (the gem level's
    /// resolved value) with the increases a spell's chaos damage over time takes (its implicit
    /// spell_damage_modifiers_apply_to_skill_dot admits spell damage; it is not area damage, and projectile damage does not
    /// apply to its damage over time), essence_drain_damage_+%, the supports' more multipliers and the damage over time
    /// multipliers. Duration: base_skill_effect_duration with skill effect duration and siphon_duration_+%. Leech:
    /// siphon_life_leech_from_damage_permyriad. Of Desperation's life loss is not modelled.</summary>
    internal static MonsterEssenceDrain.Cast? EssenceDrainCast(ClientState conn, ushort skillId, int effect)
    {
        string? id = SkillsFor(conn).EffectId(effect);
        if (id is not ("EssenceDrain" or "EssenceDrainAltX" or "EssenceDrainAltY")) return null;
        var own = SupportedSkill(conn, skillId).Stats;
        var player = CharacterPlayerStats(conn);
        var merged = MergeSkillStats(player, own);
        int Get(string name) => Stats.Key(name) is uint k ? merged.GetValueOrDefault(k) : 0;
        int Own(string name) => Stats.Key(name) is uint k ? own.GetValueOrDefault(k) : 0;
        int Player(string name) => Stats.Key(name) is uint k ? player.GetValueOrDefault(k) : 0;

        double perSecond = Get("base_chaos_damage_to_deal_per_minute") / 60.0;
        if (!(perSecond > 0) || DamageStats.Prohibited(DamageType.Chaos, Get)) return null;
        double increased = (double)Get("damage_+%") + Get("spell_damage_+%") + Get("chaos_damage_+%")
            + Get("damage_over_time_+%") + Get("chaos_damage_over_time_+%") + Get("essence_drain_damage_+%");
        double more = EssenceDrainSupportMore.Aggregate(1.0, (m, name) => m * Math.Max(0, 1 + Own(name) / 100.0));
        double multiplier = 1 + ((double)Get("dot_multiplier_+") + Get("chaos_dot_multiplier_+")) / 100.0;
        double dps = perSecond * Math.Max(0, 1 + increased / 100.0) * more * Math.Max(0, multiplier);
        double seconds = SkillDurationModifiers.Seconds(Own("base_skill_effect_duration"), Player, Own)
            * Math.Max(0, 1 + Get("siphon_duration_+%") / 100.0);
        return new(dps, seconds, Math.Max(0, Get("siphon_life_leech_from_damage_permyriad")), skillId, conn);
    }

    /// <summary>A projectile hit that the monster survived: puts the shot's Essence Drain debuff on it.</summary>
    internal static void EssenceDrainOnHitLocked(uint id, SpawnedMonster m, MonsterEssenceDrain.Cast cast, double now)
    {
        m.EssenceDrain.Apply(now, cast);
        Console.WriteLine($"[gateway] essence drain: hit 0x{id:x} ({m.Label}), {cast.DamagePerSecond:0.#}/s for " +
            $"{cast.Seconds:0.##} s, leech {cast.LeechPermyriad / 100.0:0.##}%");
    }

    // The debuff's leech goes to its caster only, if alive and still in this instance: on this connection directly, on
    // another player's own frame (KillShares) so its pools are changed under its own lock.
    static void CreditSiphonLeechLocked(ClientState runner, ClientState? caster, SpawnedMonster m, double life)
    {
        if (caster is null || caster.Closed || caster.LeavingArea || caster.PlayerLife == 0) return;
        if (ReferenceEquals(caster, runner)) { SiphonLeechLocked(runner, m, life); return; }
        if (caster.Instance is null || !ReferenceEquals(caster.Instance, runner.Instance)) return;
        caster.KillShares.Enqueue((_, _, _) =>
        {
            using (ProjectileMutation(caster)) SiphonLeechLocked(caster, m, life);
            return Task.CompletedTask;
        });
    }

    // The debuff's life leech: a leech instance like a hit's (same caps and rate), with no gain on hit.
    static void SiphonLeechLocked(ClientState conn, SpawnedMonster m, double life)
    {
        if (!(life > 0) || conn.Closed || conn.LeavingArea || conn.Paused || conn.PlayerLife == 0) return;
        if (m.Mods.Effects["cannot_have_life_leeched_from"] != 0) return;
        var session = LeechOf(conn);
        double now = ProjectileWorldTime(conn);
        var instant = session.Hit(now, (life, 0, 0), conn.PlayerLife >= conn.PlayerMaxLife,
            conn.PlayerMana >= UnreservedMana(conn), conn.EnergyShield.Current >= conn.EnergyShield.Maximum);
        uint before = conn.PlayerLife;
        conn.PlayerLife = PetrifiedBlood.RecoverLife(conn, before, instant.Life, ref session.LifeInstantCarry, minimum: 1);
        conn.EnergyShield.Gain(instant.EnergyShield);
        session.InstantLifeChanged |= before != conn.PlayerLife;
        TrimLeechLocked(conn);
    }

    internal static async Task TickEssenceDrains(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var hits = new List<(uint Id, SpawnedMonster M, uint Life, int Damage, ClientState? Owner)>();
        double leeched = 0;
        using (ProjectileMutation(conn))
        {
            if (conn.Closed || conn.LeavingArea) return;
            double now = ProjectileWorldTime(conn);
            foreach (var (id, m) in conn.Monsters)
            {
                if (m.EssenceDrain.Idle) continue;
                if (m.Dead || m.Life == 0)
                {
                    m.EssenceDrain.Clear();
                    continue;
                }
                // The kill belongs to the instance whose damage crossed the monster's remaining life.
                double before = m.Life, sum = 0;
                MonsterEssenceDrain.Cast? winner = null;
                var (damage, leech) = m.EssenceDrain.Advance(now, MonsterDotTaken(m, DamageType.Chaos, now),
                    (cast, dealt, life) =>
                    {
                        if (winner is null && (sum += dealt) >= before) winner = cast;
                        CreditSiphonLeechLocked(conn, cast.Caster, m, life);
                    });
                if (damage <= 0) continue;
                leeched += leech;
                // Its damage, Rampage, Vaal soul and kill go to that instance's owner, never to this connection for running
                // the world; with the owner gone, the death without any player's on-kill effects.
                var owner = DotOwner(conn, winner?.Caster);
                (m.Life, bool stageStarts) = owner is null ? TakeDamageLocked(m, damage) : TakeOwnerDamageLocked(owner, m, damage);
                m.Aggro = m.Life > 0;
                if (m.Life == 0)
                {
                    m.Dead = true;
                    m.LastHitBy = owner;
                    if (owner is not null) CollectVaalSoulAtDeathLocked(owner, m);
                    m.EssenceDrain.Clear();
                }
                hits.Add((id, m, m.Life, damage, owner));
                if (stageStarts) CommitBossStage(remote, stream, ciphers, conn, id, m, now);
            }
        }
        foreach (var (id, m, life, damage, owner) in hits)
        {
            if (life == 0)
                await SendWorldRecord(remote, stream, ciphers, conn, id, InstanceEntities.StatsUpdate(id, [(StatKeys.HideMiniLifeBar, 1)]), null);
            await SendWorldRecord(remote, stream, ciphers, conn, id, InstanceProtocol.LifeUpdate(id, life),
                life == 0 ? $"0x01b1 monster 0x{id:x} ({m.Label}) died of essence drain" : null);
            if (life == 0)
            {
                Console.WriteLine($"[gateway] essence drain killed 0x{id:x} ({m.Label})");
                if (owner is not null) await DotKilled(remote, stream, ciphers, conn, id, m);
                else await MonsterKilled(remote, stream, ciphers, conn, id, m, byMinion: true);
            }
        }
        // One log line a second at most: the damage and leech the debuffs dealt since the last line.
        if (hits.Count == 0) return;
        essenceDrainLogDamage += hits.Sum(h => (long)h.Damage);
        essenceDrainLogLeech += leeched;
        double clock = conn.Clock.Elapsed.TotalSeconds;
        if (clock - essenceDrainLogAt < 1) return;
        Console.WriteLine($"[gateway] essence drain ticks: {essenceDrainLogDamage} damage, {essenceDrainLogLeech:0.##} life " +
            $"leeched over {Math.Min(clock - essenceDrainLogAt, 99):0.#} s (player life {conn.PlayerLife}/{conn.PlayerMaxLife})");
        essenceDrainLogAt = clock;
        essenceDrainLogDamage = 0;
        essenceDrainLogLeech = 0;
    }

    static double essenceDrainLogAt, essenceDrainLogLeech;
    static long essenceDrainLogDamage;
}
