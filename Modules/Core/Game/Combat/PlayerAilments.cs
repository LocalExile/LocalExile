using System.Net.Sockets;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    public static void ApplyPlayerAilmentsLocked(ClientState conn, DamagePacket baseHit, DamagePacket taken,
        bool critical, bool blocked, double now, IReadOnlyDictionary<uint, int> stats, uint? source = null)
    {
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        // Changing Seasons lets any hit chill (all_damage_taken_can_chill), so a physical one is not skipped, and so does
        // physical damage taken as an element (PlayerDamageTaken.Shift).
        if (blocked || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || InGracePeriod(conn)
            || !baseHit.Components.Concat(taken.Components).Any(c => DamageTypes.IsElemental(c.Type))
                && Get("all_damage_taken_can_chill") == 0) return;
        var previousAilments = conn.ElementalAilments.Snapshot(now);
        double previousFreeze = conn.ElementalAilments.FreezeStatusUntil(now);
        double previousAction = conn.ElementalAilments.ActionFactor(now);
        // Ailment damage is a separate ordinary roll from the pre-hit range. Hit luck and the hit's critical
        // multiplier cannot alter its distribution; non-damaging ailments still use the actual mitigated hit.
        int fire = critical ? baseHit[DamageType.Fire].Roll(conn.IncomingHitRandom) : 0;
        var ailmentBase = DamagePacket.Of(DamageType.Fire, fire, fire);
        conn.ElementalAilments.Apply(ailmentBase, taken, critical, conn.PlayerMaxLife, now, Get, conn.IncomingHitRandom, source);
        if (conn.ElementalAilments.FreezeStatusUntil(now) > previousFreeze)
            conn.RecentEvents.Record(StatConditions.BeenFrozen, now);
        QueueFlaskAilmentChanges(conn, previousAilments, conn.ElementalAilments.Snapshot(now));
        PlayerAilmentsChangedLocked(conn, previousAction, now);
    }

    // A new freeze, or one that outlasts the last, cancels the player's action and channel and holds its walk (ShowFreeze
    // stops the client's too); any change of action speed re-times the walk.
    public static void PlayerAilmentsChangedLocked(ClientState conn, double previousAction, double now)
    {
        double frozenUntil = conn.ElementalAilments.FreezeUntil(now);
        if (conn.ElementalAilments.ActionFactor(now) == 0 && (previousAction != 0 || frozenUntil > conn.FreezeHeldUntil))
        {
            conn.FreezeGeneration++;
            conn.FreezeHeldUntil = frozenUntil;
            Interlocked.Increment(ref conn.ActionCount);
            conn.ChannelSkill = 0;
            // A lockstep client's walk ends with the freeze's 0x1d1 instead (ShowFreeze), as with a hit's recovery.
            if (!conn.LockstepClient) conn.Walk.Pause(now, frozenUntil - now);
        }
        if (previousAction != conn.ElementalAilments.ActionFactor(now)) conn.ApplyMovementSpeed();
    }

    public static int AdvancePlayerAilmentsLocked(ClientState conn, double from, double until,
        IReadOnlyDictionary<uint, int> stats)
    {
        if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0)
        {
            conn.ElementalAilments.Clear();
            conn.IncomingAilments.Clear();
            return 0;
        }
        double previousAction = conn.ElementalAilments.ActionFactor(from);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        var immunities = InGracePeriod(conn)
            ? [.. Enum.GetValues<DamageType>()]
            : DamageImmunities.From(Get);
        int damage = 0, lifeDamage = 0;
        if (conn.ElementalAilments.HasEffects)
        {
            damage = conn.ElementalAilments.Advance(from, until, Get, Resistance(conn, DamageType.Fire),
                MaxResistance(conn, DamageType.Fire), immunities.Contains(DamageType.Fire),
                source => DamageTakenFromBlindedPct(conn, source, stats));
            damage = IncomingResourceDefences.TakenOverTime(damage, Get, ref conn.NonChaosDotTakenCarry,
                IncomingResourceDefences.LessTakenPct(conn.Pools, Get));
            lifeDamage = conn.Immune ? 0 : IncomingResourceDefences.DamageOverTime(conn.EnergyShield,
                damage, Get(IncomingResourceDefences.BypassStat), until, ref conn.NonChaosDotBypassCarry);
        }
        if (conn.PlayerLife > 0 && !conn.IncomingAilments.Idle)
        {
            var physical = DamageType.Physical;
            var chaos = DamageType.Chaos;
            lifeDamage = (int)Math.Min(int.MaxValue, (long)lifeDamage + conn.IncomingAilments.Advance(from, until,
                conn.Pools, conn.EnergyShield, Get, Resistance(conn, physical), MaxResistance(conn, physical),
                Resistance(conn, chaos), MaxResistance(conn, chaos), conn.Immune,
                immunities.Contains(physical), immunities.Contains(chaos), conn.Walk.MovingSeconds,
                ref conn.NonChaosDotBypassCarry, ref conn.ChaosDotBypassCarry));
        }
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

    /// <summary>
    /// A hostile object's accepted, unblocked contact may inflict bleeding and poison. The chances,
    /// damage and duration come from that monster's own effects and damage packet. Immunity is handled
    /// by the shared incoming owner; an evaded, blocked or damage-immune contact never reaches here.
    /// </summary>
    public static void ApplyMonsterDamagingAilmentsLocked(ClientState conn, uint source, SpawnedMonster m,
        DamagePacket hit, bool blocked, double now, bool attack = true)
    {
        if (blocked || conn.PlayerLife == 0 || conn.Closed || conn.LeavingArea || InGracePeriod(conn)) return;
        var effects = m.Mods.Effects;
        double physical = hit[DamageType.Physical].Min;
        double chaos = hit[DamageType.Chaos].Min;
        var playerStats = CharacterPlayerStats(conn);
        int Player(string name) => Stats.Key(name) is uint key ? playerStats.GetValueOrDefault(key) : 0;
        int bleedChance = Math.Max(effects["global_bleed_on_hit"] != 0 ? 100 : 0,
            attack ? effects["bleed_on_hit_with_attacks_%"] : 0);
        if (attack) bleedChance += Player("receive_bleeding_chance_%_when_hit_by_attack");
        int poisonChance = Math.Max(effects["global_poison_on_hit"] != 0 ? 100 : 0,
            effects["base_chance_to_poison_on_hit_%"]);
        if (attack) poisonChance = Math.Max(poisonChance,
            effects["base_attacks_poison"] != 0 ? 100 : effects["chance_to_poison_on_hit_with_attacks_%"]);
        bool poisonable = effects["all_damage_can_poison"] != 0
            ? hit.Components.Any(c => c.Range.Min > 0)
            : physical + chaos > 0;
        double poisonBase = effects["all_damage_can_poison"] != 0 ? hit.MinTotal : physical + chaos;
        if (bleedChance > 0 && physical > 0 && Player("cannot_gain_bleeding") == 0
            && Player("base_cannot_gain_bleeding") == 0
            && Player("virtual_immune_to_bleeding") == 0 && Player("immune_to_bleeding") == 0
            && GameServer.Core.World.RandomRolls.Chance(bleedChance, 100, conn.IncomingHitRandom))
        {
            double more = Math.Max(0, 1 + effects["bleeding_damage_+%"] / 100.0);
            double seconds = Ailments.BleedDurationSeconds
                * Math.Max(0, 1 + effects["base_bleed_duration_+%"] / 100.0);
            conn.IncomingAilments.ApplyBleed(now, physical * 0.10 * more
                * Math.Max(0, 1 + effects["monster_bleeding_damage_+%_final"] / 100.0), seconds, source,
                victim: Player);
        }
        if (poisonChance > 0 && poisonable && Player("cannot_be_poisoned") == 0
            && Player("immune_to_poison") == 0
            && GameServer.Core.World.RandomRolls.Chance(poisonChance, 100, conn.IncomingHitRandom))
        {
            double more = Math.Max(0, 1 + effects["base_poison_damage_+%"] / 100.0);
            double seconds = Ailments.PoisonDurationSeconds
                * Math.Max(0, 1 + effects["base_poison_duration_+%"] / 100.0);
            conn.IncomingAilments.ApplyPoison(now, poisonBase * 0.30 * more
                * Math.Max(0, 1 + effects["monster_poison_damage_+%_final"] / 100.0), seconds, source,
                victim: Player);
        }
    }

    // The chilled, frozen and shocked buffs on the player, as on monsters (AilmentBuff, on the fixed top instances that
    // NextBuff never hands out); a kind whose magnitude changed is sent again. The player's ignited buff is not sent.
    static async Task SendPlayerAilmentBuffs(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        (bool Ignite, int Chill, bool Freeze, int Shock) before, (bool Ignite, int Chill, bool Freeze, int Shock) after)
    {
        foreach (var (packet, log) in PlayerAilmentBuffs(before, after)) await Send(remote, stream, ciphers, packet, log);
    }

    public static List<(byte[] Packet, string Log)> PlayerAilmentBuffs((bool Ignite, int Chill, bool Freeze, int Shock) before,
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

    public static List<(byte[] Packet, string Log)> IncomingAilmentBuffs(
        IncomingDamagingAilments.State before, IncomingDamagingAilments.State after, double now)
    {
        var packets = new List<(byte[], string)>();
        const ushort bleedingInstance = ushort.MaxValue - 4, poisonInstance = ushort.MaxValue - 3;
        if (before.Bleeding != after.Bleeding || before.BleedRevision != after.BleedRevision
            || before.BleedUntil != after.BleedUntil)
        {
            if (before.Bleeding) packets.Add((InstanceEntities.RemoveBuff(PlayerId, bleedingInstance), "0x01c2 player bleeding ends"));
            if (after.Bleeding && LabyrinthTrials.BleedingBuffRow is ushort row)
                packets.Add((InstanceEntities.AddBuff(PlayerId, bleedingInstance, row,
                    u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, after.BleedUntil - now))),
                    "0x01c1 player bleeding"));
        }
        if (before.Poisons != after.Poisons || before.PoisonRevision != after.PoisonRevision
            || before.PoisonUntil != after.PoisonUntil)
        {
            if (before.Poisons > 0) packets.Add((InstanceEntities.RemoveBuff(PlayerId, poisonInstance), "0x01c2 player poison ends"));
            if (after.Poisons > 0)
                packets.Add((InstanceEntities.AddBuff(PlayerId, poisonInstance, PoisonBuffRow, PlayerId,
                    stacks: (ushort)Math.Clamp(after.Poisons, 1, ushort.MaxValue),
                    u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, after.PoisonUntil - now))),
                    $"0x01c1 player poisoned ({after.Poisons})"));
        }
        return packets;
    }

    public static bool PlayerActionBlocked(ClientState conn, double requestedAt, double now) =>
        conn.HitRecovery.BlocksInput(requestedAt, now) || conn.ElementalAilments.ActionFactor(now) == 0;

    /// <summary>The signed <c>action_speed_-%</c> from the character's raw sources and dev overrides: positive is slower.
    /// The client's own action-speed bucket, and the planned ActionSpeed/EffectClock contract (#246), keep it separate
    /// from attack/cast/movement increases so it is applied once.</summary>
    public static int ActionSpeedReduction(ClientState conn)
    {
        if (Stats.Key("action_speed_-%") is not uint key) return 0;
        lock (conn.Monsters) if (conn.PlayerStats.TryGetValue(key, out int dev)) return dev;
        return CharacterStatSources(conn).Stats.GetValueOrDefault(key);
    }

    public static double PlayerActionFactor(ClientState conn)
    {
        var sources = CharacterStatSources(conn).Stats;
        return Math.Max(.01, PlayerActionFactorFrom(conn, name => Stats.Key(name) is uint k
            ? conn.PlayerStats.GetValueOrDefault(k, sources.GetValueOrDefault(k)) : 0));
    }

    public static double PlayerActionFactorFrom(ClientState conn, Func<string, int> get)
    {
        double extra = get("has_tailwind") == 0 ? 0 : Math.Floor(8 * Math.Max(0, 1 + get("tailwind_effect_on_self_+%") / 100d)) - 8;
        double factor = conn.ElementalAilments.TotalActionFactor(conn.Clock.Elapsed.TotalSeconds, get("action_speed_-%") - extra)
            * Math.Max(.01, 1 + get("temporal_chains_action_speed_+%_final") / 100d);
        return get("action_speed_cannot_be_reduced_below_base") != 0 ? Math.Max(1, factor) : factor;
    }
}
