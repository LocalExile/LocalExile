using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Program;

public static partial class Game
{
    sealed class BloodState
    {
        public readonly List<double> Stacks = new(10);
        public double Started, Until, Settled, Accrued, Carry, ProtectedUntil, ExpiryRate = 1;
        public ushort? Buff;

        public void Settle(double now)
        {
            if (now <= Settled) return;
            Accrued += Math.Max(0, Math.Min(now, Until) - Settled) * Stacks.Sum();
            Settled = now;
            if (now >= Until) Stacks.Clear();
        }
    }

    static readonly RuleTable<ClientState, BloodState> Blood = new();

    /// <summary>The Corrupted Blood a monster skill applies, from its own stats: the record's reader, plus the stack count
    /// the blood-projectile and bonespire family leaves out. Those rows (EssenceBloodProjectileNova — Greed's essence
    /// daemons — BellyTotemBloodProjectile, BellyTotemBonespire, BreachBonespire, BreachBonespireBoss,
    /// IntMissionDaemonBloodProjectile, IntMissionDaemonBonespire, PyramidBeamSmash, SkeletonSoldierBloodProjectileNova,
    /// VaalSideAreaVendigoBonespire) declare a duration and a per-stack percentage but no count, and apply one stack. A
    /// duration with no damage source is still not a Corrupted Blood skill.</summary>
    public static CorruptedBloodOnHit? CorruptedBloodFor(Func<string, int> stat)
    {
        if (CorruptedBloodOnHit.From(stat) is { } declared) return declared;
        int duration = stat("corrupted_blood_on_hit_duration");
        int perStack = stat("corrupted_blood_on_hit_%_average_damage_to_deal_per_minute_per_stack");
        return duration > 0 && perStack > 0 ? new(1, duration / 1000d, 0, perStack) : null;
    }

    public static void ApplyCorruptedBloodOnHit(ClientState conn, MonsterAttack attack, DamagePacket hit,
        bool blocked, double now, IReadOnlyDictionary<uint, int> stats)
    {
        if (attack.CorruptedBlood is not { Seconds: > 0 } skill || blocked || conn.Immune || conn.PlayerLife == 0
            || conn.Closed || conn.LeavingArea || InGracePeriod(conn)) return;
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        if (Get("cannot_be_inflicted_by_corrupted_blood") != 0 || Get("immune_to_corrupted_blood") != 0
            || Get("base_cannot_be_damaged") != 0) return;
        double dps = skill.PhysicalPerMinute / 60d
            + (hit.MinTotal + hit.MaxTotal) * .5 * skill.AverageHitPercentPerMinute / 6000d;
        if (dps <= 0 || RandomRolls.Chance(Get("avoid_corrupted_blood_%_chance"), 100, conn.IncomingHitRandom)) return;
        var state = Blood.GetValue(conn, _ => new());
        state.Settle(now);
        if (now < state.ProtectedUntil || state.Stacks.Count >= 5 && Get("cannot_gain_corrupted_blood_while_you_have_at_least_5_stacks") != 0) return;
        if (state.Stacks.Count == 0) state.Started = now;
        for (int i = 0; i < Math.Min(10, skill.Stacks); i++)
        {
            if (state.Stacks.Count < 10) state.Stacks.Add(dps);
            else
            {
                int weakest = state.Stacks.IndexOf(state.Stacks.Min());
                if (state.Stacks[weakest] >= dps) break;
                state.Stacks[weakest] = dps;
            }
        }
        state.ExpiryRate = RecipientEffectTime.Rate(2, Get);
        state.Until = now + skill.Seconds / state.ExpiryRate;
        BloodIcon(conn, state, now);
    }

    static void BloodIcon(ClientState conn, BloodState state, double now)
    {
        if (state.Buff is ushort previous)
            conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(PlayerId, previous), "player corrupted blood ends", WorldId: PlayerId));
        state.Buff = null;
        if (state.Stacks.Count == 0) return;
        ushort buff = conn.NextBuff++;
        state.Buff = buff;
        conn.Minions.OutputQueue.Add(new(InstanceEntities.AddBuff(PlayerId, buff, 238,
            u68: BitConverter.SingleToUInt32Bits((float)(state.Until - now)), stacks: (ushort)state.Stacks.Count),
            $"player corrupted blood ({state.Stacks.Count})", WorldId: PlayerId));
    }

    internal static int CorruptedBloodStacks(ClientState conn, double now) =>
        Blood.TryGetValue(conn, out var state) && now < state.Until ? state.Stacks.Count : 0;

    internal static void RemoveCorruptedBlood(ClientState conn, double now, double protection)
    {
        if (CorruptedBloodStacks(conn, now) == 0 || !Blood.TryGetValue(conn, out var state)) return;
        state.Settle(now);
        state.Stacks.Clear();
        state.Until = now;
        state.ProtectedUntil = now + protection;
        BloodIcon(conn, state, now);
    }

    public static void ClearCorruptedBlood(ClientState conn)
    {
        if (!Blood.TryGetValue(conn, out var state)) return;
        state.Stacks.Clear();
        BloodIcon(conn, state, 0);
        Blood.Remove(conn);
    }

    static int AdvanceCorruptedBlood(ClientState conn, BloodState state, double now, IReadOnlyDictionary<uint, int> stats,
        bool physicalImmune, double letheFactor)
    {
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        state.Settle(now);
        bool immune = Get("immune_to_corrupted_blood") != 0;
        if (immune) state.Stacks.Clear();
        if (state.Stacks.Count == 0)
        {
            if (state.Buff.HasValue) BloodIcon(conn, state, now);
        }
        else
        {
            double rate = RecipientEffectTime.Rate(2, Get);
            if (rate != state.ExpiryRate)
            {
                state.Until = RecipientEffectTime.RetimedDeadline(state.Until, now, state.ExpiryRate, rate);
                state.ExpiryRate = rate;
                BloodIcon(conn, state, now);
            }
        }
        double damage = state.Accrued;
        state.Accrued = 0;
        bool unaffected = Get("unaffected_by_corrupted_blood") != 0
            || Get("unaffected_by_corrupted_blood_while_leeching") != 0 && Get("is_leeching") != 0;
        if (immune || unaffected || physicalImmune || conn.Immune || InGracePeriod(conn) || Get("base_cannot_be_damaged") != 0)
            damage = state.Carry = 0;
        else if (damage > 0)
            damage *= conn.ElementalAilments.DamageTakenFactor(now, 0) * letheFactor;
        int whole = IncomingResourceDefences.TakenOverTime(damage, Get, ref state.Carry,
            IncomingResourceDefences.LessTakenPct(conn.Pools, Get), typedIncreasedPct: Get("physical_damage_taken_+%"),
            reductionPct: ChargeEffects.Reduction(Get).Physical, finalTakenPct: Get(IncomingResourceDefences.UnhingedSaneDamageFinal));
        if (state.Stacks.Count == 0 && now >= state.ProtectedUntil) Blood.Remove(conn);
        return IncomingResourceDefences.DamageOverTime(conn.EnergyShield, whole, Get(IncomingResourceDefences.BypassStat), now, ref conn.NonChaosDotBypassCarry);
    }
}
