using System.Net.Sockets;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    public static PlayerElementalAilments.HitSettings IncomingAilmentSettings(Func<uint, int> get) => new(
        get(Stat.AlwaysIgnite) != 0 ? 100 : get(Stat.BaseChanceToIgnitePct),
        get(Stat.AlwaysFreeze) != 0 ? 100 : get(Stat.BaseChanceToFreezePct),
        get(Stat.AlwaysShock) != 0 ? 100 : get(Stat.BaseChanceToShockPct),
        get(Stat.ActiveSkillChillEffectPlusPctFinal), get(Stat.ActiveSkillShockDurationPlusPctFinal),
        get(Stat.ActiveSkillIgniteDamagePlusPctFinal)) { NeverFreeze = get(Stat.NeverFreeze) != 0 };

    public static void ApplyPlayerAilmentsLocked(Player conn, DamagePacket baseHit, DamagePacket taken,
        bool critical, bool blocked, double now, IReadOnlyDictionary<uint, int> stats, uint? source = null) =>
        ApplyPlayerAilmentsLocked(conn, baseHit, taken, critical, blocked, now, stats, default, source);

    public static void ApplyPlayerAilmentsLocked(Player conn, DamagePacket baseHit, DamagePacket taken,
        bool critical, bool blocked, double now, IReadOnlyDictionary<uint, int> stats,
        PlayerElementalAilments.HitSettings settings, uint? source = null)
    {
        int Get(uint key) => stats.GetValueOrDefault(key);
        // Changing Seasons lets any hit chill (all_damage_taken_can_chill), so a physical one is not skipped, and so does
        // physical damage taken as an element (PlayerDamageTaken.Shift).
        if (blocked || conn.Immune || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || InGracePeriod(conn)
            || !baseHit.Components.Concat(taken.Components).Any(c => DamageTypes.IsElemental(c.Type))
                && Get(Stat.AllDamageTakenCanChill) == 0) return;
        // Ailment damage is a separate ordinary roll from the pre-hit range. Hit luck and the hit's critical
        // multiplier cannot alter its distribution; non-damaging ailments still use the actual mitigated hit.
        int fire = critical || settings.IgniteChance > 0 ? baseHit[DamageType.Fire].Roll(conn.IncomingHitRandom) : 0;
        var ailmentBase = DamagePacket.Of(DamageType.Fire, fire, fire);
        ApplyPlayerAilmentsLocked(conn, now, ailments =>
            ailments.Apply(ailmentBase, taken, critical, conn.PlayerMaxLife, now, Get, conn.IncomingHitRandom, settings, source));
    }

    /// <summary>Apply one source's ailments under the combat lock. Hit, ground and reflected sources retain their
    /// own admission, magnitude and duration rules; accepted changes share flask events, recent freeze and movement.</summary>
    public static void ApplyPlayerAilmentsLocked(Player conn, double now, Action<PlayerElementalAilments> apply)
    {
        if (conn.Closed || conn.LeavingArea || conn.RageRetired || conn.PlayerLife == 0) return;
        var previousAilments = conn.ElementalAilments.Snapshot(now);
        double previousFreeze = conn.ElementalAilments.FreezeStatusUntil(now);
        double previousAction = conn.ElementalAilments.ActionFactor(now);
        apply(conn.ElementalAilments);
        if (conn.ElementalAilments.FreezeStatusUntil(now) > previousFreeze)
            conn.RecentEvents.Record(StatConditions.BeenFrozen, now);
        QueueFlaskAilmentChanges(conn, previousAilments, conn.ElementalAilments.Snapshot(now));
        PlayerAilmentsChangedLocked(conn, previousAction, now);
    }

    // A new freeze, or one that outlasts the last, cancels the player's action and channel and holds its walk (ShowFreeze
    // stops the client's too); any change of action speed re-times the walk.
    public static void PlayerAilmentsChangedLocked(Player conn, double previousAction, double now)
    {
        double frozenUntil = conn.ElementalAilments.FreezeUntil(now);
        if (conn.ElementalAilments.ActionFactor(now) == 0 && (previousAction != 0 || frozenUntil > conn.FreezeHeldUntil))
        {
            conn.FreezeGeneration++;
            conn.FreezeHeldUntil = frozenUntil;
            CancelMeleeRepeats(conn, now);
            Interlocked.Increment(ref conn.ActionCount);
            conn.ChannelSkill = 0;
            // A lockstep client's walk ends with the freeze's 0x1d1 instead (ShowFreeze), as with a hit's recovery.
            if (!conn.LockstepClient) conn.Walk.Pause(now, frozenUntil - now);
        }
        if (previousAction != conn.ElementalAilments.ActionFactor(now)) conn.ApplyMovementSpeed();
    }

    public static int AdvancePlayerAilmentsLocked(Player conn, double from, double until,
        IReadOnlyDictionary<uint, int> stats)
    {
        if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0)
        {
            conn.ElementalAilments.Clear();
            conn.IncomingAilments.Clear();
            return 0;
        }
        double previousAction = conn.ElementalAilments.ActionFactor(from);
        int Get(uint key) => stats.GetValueOrDefault(key);
        var immunities = PlayerImmunities(conn, stats);
        int damage = 0, lifeDamage = 0;
        if (conn.ElementalAilments.HasEffects)
        {
            damage = conn.ElementalAilments.Advance(from, until, Get, Resistance(conn, DamageType.Fire),
                MaxResistance(conn, DamageType.Fire), immunities.Contains(DamageType.Fire),
                source => DamageTakenFromBlindedPct(conn, source, stats));
            if (Get(Stat.BaseCannotBeDamaged) > 0) damage = 0;
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
                Resistance(conn, chaos), MaxResistance(conn, chaos), conn.Immune || Get(Stat.BaseCannotBeDamaged) > 0,
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
    public static void ApplyMonsterDamagingAilmentsLocked(Player conn, uint source, SpawnedMonster m,
        DamagePacket hit, bool blocked, double now, bool attack = true, IReadOnlyDictionary<uint, int>? prepared = null)
    {
        if (blocked || conn.PlayerLife == 0 || conn.Closed || conn.LeavingArea || InGracePeriod(conn)) return;
        var effects = m.Mods.Effects;
        var playerStats = prepared ?? CharacterPlayerStats(conn);
        int Player(uint key) => playerStats.GetValueOrDefault(key);
        int bleedChance = Math.Max(effects[Stat.GlobalBleedOnHit] != 0 ? 100 : 0,
            attack ? effects[Stat.BleedOnHitWithAttacksPct] : 0);
        if (attack) bleedChance += Player(Stat.ReceiveBleedingChancePctWhenHitByAttack);
        int poisonChance = Math.Max(effects[Stat.GlobalPoisonOnHit] != 0 ? 100 : 0,
            effects[Stat.BaseChanceToPoisonOnHitPct]);
        if (attack) poisonChance = Math.Max(poisonChance,
            effects[Stat.BaseAttacksPoison] != 0 ? 100 : effects[Stat.ChanceToPoisonOnHitWithAttacksPct]);
        if (bleedChance <= 0 && poisonChance <= 0) return;
        // Damaging ailments roll their base independently of the hit's roll, before victim mitigation.
        var rolled = hit.Roll(conn.IncomingHitRandom);
        double physical = rolled[DamageType.Physical].Min;
        double chaos = rolled[DamageType.Chaos].Min;
        bool poisonable = effects[Stat.AllDamageCanPoison] != 0
            ? rolled.Components.Any(c => c.Range.Min > 0)
            : physical + chaos > 0;
        double poisonBase = effects[Stat.AllDamageCanPoison] != 0 ? rolled.MinTotal : physical + chaos;
        if (bleedChance > 0 && physical > 0 && Player(Stat.CannotGainBleeding) == 0
            && Player(Stat.BaseCannotGainBleeding) == 0
            && Player(Stat.VirtualImmuneToBleeding) == 0 && Player(Stat.ImmuneToBleeding) == 0
            && GameServer.Core.World.RandomRolls.Chance(bleedChance, 100, conn.IncomingHitRandom)
            && !GameServer.Core.World.RandomRolls.Chance(Player(Stat.AvoidBleedPct), 100, conn.IncomingHitRandom))
        {
            double more = Math.Max(0, 1 + effects[Stat.BleedingDamagePlusPct] / 100.0);
            double seconds = Ailments.BleedDurationSeconds
                * Math.Max(0, 1 + effects[Stat.BaseBleedDurationPlusPct] / 100.0);
            conn.IncomingAilments.ApplyBleed(now, physical * 0.70 * more
                * Math.Max(0, 1 + effects[Stat.MonsterBleedingDamagePlusPctFinal] / 100.0), seconds, source,
                victim: Player);
        }
        if (poisonChance > 0 && poisonable && Player(Stat.CannotBePoisoned) == 0
            && Player(Stat.ImmuneToPoison) == 0
            && GameServer.Core.World.RandomRolls.Chance(poisonChance, 100, conn.IncomingHitRandom)
            && !GameServer.Core.World.RandomRolls.Chance(Player(Stat.AvoidPoisonPct), 100, conn.IncomingHitRandom))
        {
            double more = Math.Max(0, 1 + effects[Stat.BasePoisonDamagePlusPct] / 100.0);
            double seconds = Ailments.PoisonDurationSeconds
                * Math.Max(0, 1 + effects[Stat.BasePoisonDurationPlusPct] / 100.0);
            conn.IncomingAilments.ApplyPoison(now, poisonBase * 0.30 * more
                * Math.Max(0, 1 + effects[Stat.MonsterPoisonDamagePlusPctFinal] / 100.0), seconds, source,
                victim: Player);
        }
    }

    // The chilled, frozen and shocked buffs on the player, as on monsters (AilmentBuff, on the fixed top instances that
    // NextBuff never hands out); a kind whose magnitude changed is sent again. The player's ignited buff is not sent.
    static async Task SendPlayerAilmentBuffs(Player conn,
        (bool Ignite, int Chill, bool Freeze, int Shock) before, (bool Ignite, int Chill, bool Freeze, int Shock) after)
    {
        foreach (var (packet, log) in PlayerAilmentBuffs(before, after)) await Send(conn, packet, log);
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

    public static bool PlayerActionBlocked(Player conn, double requestedAt, double now) =>
        conn.HitRecovery.BlocksInput(requestedAt, now) || conn.ElementalAilments.ActionFactor(now) == 0;

    /// <summary>The signed <c>action_speed_-%</c> from the character's raw sources and dev overrides: positive is slower.
    /// The client's own action-speed bucket, and the planned ActionSpeed/EffectClock contract (#246), keep it separate
    /// from attack/cast/movement increases so it is applied once.</summary>
    public static int ActionSpeedReduction(Player conn)
    {
        uint key = Stat.ActionSpeedMinusPct;
                return CharacterStatSources(conn).Stats.GetValueOrDefault(key);
    }

    public static double PlayerActionFactor(Player conn)
    {
        var sources = CharacterStatSources(conn).Stats;
        // The Spirit Leadership buff joins CharacterPlayerStats, not the raw source cache this path reads; Rakiata's
        // action-speed floor has to reach the server's own attack/cast timing too.
        SpiritChieftains.Leadership? leadership;
        lock (conn.Monsters)
            leadership = conn.PlayerLife > 0 ? conn.SpiritChieftains.Active(AreaTime(conn)) : null;
        return Math.Max(.01, PlayerActionFactorFrom(conn, name =>
        {
            if (!(name is var key)) return 0;
            long value = sources.GetValueOrDefault(key);
            if (leadership is { } active)
                foreach (var (stat, amount) in active.Stats)
                    if (stat == key) value += amount;
            return (int)Math.Clamp(value, int.MinValue, int.MaxValue);
        }));
    }

    public static double PlayerActionFactorFrom(Player conn, Func<uint, int> get)
    {
        double extra = get(Stat.HasTailwind) == 0 ? 0 : Math.Floor(8 * Math.Max(0, 1 + get(Stat.TailwindEffectOnSelfPlusPct) / 100d)) - 8;
        double factor = conn.ElementalAilments.TotalActionFactor(AreaTime(conn), get(Stat.ActionSpeedMinusPct) - extra)
            * Math.Max(.01, 1 + get(Stat.TemporalChainsActionSpeedPlusPctFinal) / 100d);
        if (get(Stat.ActionSpeedCannotBeReducedBelowBase) != 0) factor = Math.Max(1, factor);
        // Rakiata's Leadership: action speed cannot be modified below 100% + this value (the level 1 spirit's 10 is
        // the wiki's "at least 110% of base value").
        int minimum = get(Stat.ActionSpeedPlusPctMinimumValueFromRakiatasLeadership);
        // A frozen player's factor is 0 and stays 0; the floor only lifts a lowered (positive) action speed.
        if (minimum != 0 && factor > 0) factor = Math.Max(factor, 1 + minimum / 100.0);
        return factor;
    }
}
