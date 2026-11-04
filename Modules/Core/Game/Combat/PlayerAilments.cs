using System.Net.Sockets;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

static partial class Game
{
    internal static void ApplyPlayerAilmentsLocked(ClientState conn, DamagePacket baseHit, DamagePacket taken,
        bool critical, bool blocked, double now, IReadOnlyDictionary<uint, int> stats, uint? source = null)
    {
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        // Changing Seasons lets any hit chill (all_damage_taken_can_chill), so a physical one is not skipped, and so does
        // physical damage taken as an element (PlayerDamageTaken.Shift).
        if (blocked || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || InGracePeriod(conn)
            || !baseHit.Components.Concat(taken.Components).Any(c => DamageTypes.IsElemental(c.Type))
                && Get("all_damage_taken_can_chill") == 0) return;
        var previousAilments = conn.ElementalAilments.Snapshot(now);
        double previousAction = conn.ElementalAilments.ActionFactor(now);
        // Ailment damage is a separate ordinary roll from the pre-hit range. Hit luck and the hit's critical
        // multiplier cannot alter its distribution; non-damaging ailments still use the actual mitigated hit.
        int fire = critical ? baseHit[DamageType.Fire].Roll(conn.IncomingHitRandom) : 0;
        var ailmentBase = DamagePacket.Of(DamageType.Fire, fire, fire);
        conn.ElementalAilments.Apply(ailmentBase, taken, critical, conn.PlayerMaxLife, now, Get, conn.IncomingHitRandom, source);
        QueueFlaskAilmentChanges(conn, previousAilments, conn.ElementalAilments.Snapshot(now));
        PlayerAilmentsChangedLocked(conn, previousAction, now);
    }

    // A new freeze, or one that outlasts the last, cancels the player's action and channel and holds its walk (ShowFreeze
    // stops the client's too); any change of action speed re-times the walk.
    internal static void PlayerAilmentsChangedLocked(ClientState conn, double previousAction, double now)
    {
        double frozenUntil = conn.ElementalAilments.FreezeUntil(now);
        if (conn.ElementalAilments.ActionFactor(now) == 0 && (previousAction != 0 || frozenUntil > conn.FreezeHeldUntil))
        {
            conn.FreezeGeneration++;
            conn.FreezeHeldUntil = frozenUntil;
            Interlocked.Increment(ref conn.ActionCount);
            conn.ChannelSkill = 0;
            conn.Walk.Pause(now, frozenUntil - now);
        }
        if (previousAction != conn.ElementalAilments.ActionFactor(now)) conn.ApplyMovementSpeed();
    }

    internal static int AdvancePlayerAilmentsLocked(ClientState conn, double from, double until,
        IReadOnlyDictionary<uint, int> stats)
    {
        if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) { conn.ElementalAilments.Clear(); return 0; }
        if (!conn.ElementalAilments.HasEffects) return 0;
        double previousAction = conn.ElementalAilments.ActionFactor(from);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        int damage = conn.ElementalAilments.Advance(from, until, Get, Resistance(conn, DamageType.Fire),
            MaxResistance(conn, DamageType.Fire), PlayerImmunities(conn).Contains(DamageType.Fire),
            source => DamageTakenFromBlindedPct(conn, source, stats));
        damage = IncomingResourceDefences.TakenOverTime(damage, Get, ref conn.NonChaosDotTakenCarry);
        int lifeDamage = conn.Immune ? 0 : IncomingResourceDefences.DamageOverTime(conn.EnergyShield,
            damage, Get(IncomingResourceDefences.BypassStat), until, ref conn.NonChaosDotBypassCarry);
        conn.PlayerLife = (uint)Math.Max(0, (long)conn.PlayerLife - lifeDamage);
        if (conn.PlayerLife == 0)
        {
            conn.InvalidateIncomingHits();
            // Commit the mode outcome inside lethal damage, before disconnects or network writes can intervene.
            RecordHardcoreDeath(conn);
        }
        if (previousAction != conn.ElementalAilments.ActionFactor(until)) conn.ApplyMovementSpeed();
        return damage;
    }

    // The chilled, frozen and shocked buffs on the player, as on monsters (AilmentBuff, on the fixed top instances that
    // NextBuff never hands out); a kind whose magnitude changed is sent again. The player's ignited buff is not sent.
    static async Task SendPlayerAilmentBuffs(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        (bool Ignite, int Chill, bool Freeze, int Shock) before, (bool Ignite, int Chill, bool Freeze, int Shock) after)
    {
        foreach (var (packet, log) in PlayerAilmentBuffs(before, after)) await Send(remote, stream, ciphers, packet, log);
    }

    internal static List<(byte[] Packet, string Log)> PlayerAilmentBuffs((bool Ignite, int Chill, bool Freeze, int Shock) before,
        (bool Ignite, int Chill, bool Freeze, int Shock) after)
    {
        var packets = new List<(byte[], string)>();
        foreach (var (kind, old, now) in new[] { (MonsterElementalAilments.Kind.Chill, before.Chill, after.Chill),
            (MonsterElementalAilments.Kind.Freeze, before.Freeze ? 100 : 0, after.Freeze ? 100 : 0),
            (MonsterElementalAilments.Kind.Shock, before.Shock, after.Shock) })
        {
            if (old == now) continue;
            ushort instance = MonsterElementalAilments.Instance(kind);
            if (old > 0) packets.Add((InstanceEntities.RemoveBuff(PlayerId, instance), $"0x01c2 player {kind} ends"));
            if (now > 0) packets.Add((AilmentBuff(PlayerId, instance, kind, now), $"0x01c1 player {kind} {now}%"));
        }
        return packets;
    }

    internal static bool PlayerActionBlocked(ClientState conn, double requestedAt, double now) =>
        conn.HitRecovery.BlocksInput(requestedAt, now) || conn.ElementalAilments.ActionFactor(now) == 0;

    /// <summary>The signed <c>action_speed_-%</c> from the character's raw sources and dev overrides: positive is slower.
    /// The client's own action-speed bucket, and the planned ActionSpeed/EffectClock contract (#246), keep it separate
    /// from attack/cast/movement increases so it is applied once.</summary>
    internal static int ActionSpeedReduction(ClientState conn)
    {
        if (Stats.Key("action_speed_-%") is not uint key) return 0;
        lock (conn.Monsters) if (conn.PlayerStats.TryGetValue(key, out int dev)) return dev;
        return CharacterStatSources(conn).Stats.GetValueOrDefault(key);
    }

    internal static double PlayerActionFactor(ClientState conn)
    {
        var sources = CharacterStatSources(conn).Stats;
        return Math.Max(.01, PlayerActionFactorFrom(conn, name => Stats.Key(name) is uint k
            ? conn.PlayerStats.GetValueOrDefault(k, sources.GetValueOrDefault(k)) : 0));
    }

    internal static double PlayerActionFactorFrom(ClientState conn, Func<string, int> get)
    {
        double extra = get("has_tailwind") == 0 ? 0 : Math.Floor(8 * Math.Max(0, 1 + get("tailwind_effect_on_self_+%") / 100d)) - 8;
        double factor = conn.ElementalAilments.TotalActionFactor(conn.Clock.Elapsed.TotalSeconds, get("action_speed_-%") - extra)
            * Math.Max(.01, 1 + get("temporal_chains_action_speed_+%_final") / 100d);
        return get("action_speed_cannot_be_reduced_below_base") != 0 ? Math.Max(1, factor) : factor;
    }
}
