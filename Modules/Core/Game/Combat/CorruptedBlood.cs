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

        public void Settle(double now, DamageTakenWindow damageTime)
        {
            if (now <= Settled) return;
            Accrued += damageTime.Seconds(Settled, Math.Min(now, Until)) * Stacks.Sum();
            Settled = now;
            if (now >= Until) Stacks.Clear();
        }
    }

    static readonly RuleTable<Player, BloodState> Blood = new();

    /// <summary>The Corrupted Blood a monster skill applies, from its stats: <see cref="CorruptedBloodOnHit.From"/>'s reader,
    /// plus the stack count the blood-projectile family leaves out. Those rows (EssenceBloodProjectileNova — Greed's essence
    /// daemons — and the BellyTotem/Breach/Vaal bonespires) declare a duration and a per-stack percentage but no count, and
    /// apply one stack; a duration with no damage source is still not a Corrupted Blood skill.</summary>
    public static CorruptedBloodOnHit? CorruptedBloodFor(Func<uint, int> stat)
    {
        if (CorruptedBloodOnHit.From(stat) is { } declared) return declared;
        int duration = stat(Stat.CorruptedBloodOnHitDuration);
        int perStack = stat(Stat.CorruptedBloodOnHitPctAverageDamageToDealPerMinutePerStack);
        return duration > 0 && perStack > 0 ? new(1, duration / 1000d, 0, perStack) : null;
    }

    public static void ApplyCorruptedBloodOnHit(Player player, MonsterAttack attack, DamagePacket hit,
        bool blocked, double now, IReadOnlyDictionary<uint, int> stats)
    {
        if (attack.CorruptedBlood is not { Seconds: > 0 } skill || blocked || player.Immune || player.PlayerLife == 0
            || player.Closed || player.LeavingArea || InGracePeriod(player)) return;
        int Get(uint key) => stats.GetValueOrDefault(key);
        if (Get(Stat.CannotBeInflictedByCorruptedBlood) != 0 || Get(Stat.ImmuneToCorruptedBlood) != 0
            || Get(Stat.BaseCannotBeDamaged) != 0) return;
        double dps = skill.PhysicalPerMinute / 60d
            + (hit.MinTotal + hit.MaxTotal) * .5 * skill.AverageHitPercentPerMinute / 6000d;
        if (dps <= 0 || RandomRolls.Chance(Get(Stat.AvoidCorruptedBloodPctChance), 100, player.IncomingHitRandom)) return;
        var state = Blood.GetValue(player, _ => new());
        state.Settle(now, GuardDamageOverTime(player, DamageType.Physical));
        if (now < state.ProtectedUntil || state.Stacks.Count >= 5 && Get(Stat.CannotGainCorruptedBloodWhileYouHaveAtLeast5Stacks) != 0) return;
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
        BloodIcon(player, state, now);
    }

    static void BloodIcon(Player player, BloodState state, double now)
    {
        if (state.Buff is ushort previous)
            player.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(player.Id, previous), "player corrupted blood ends", WorldId: player.Id));
        state.Buff = null;
        if (state.Stacks.Count == 0) return;
        ushort buff = player.NextBuff++;
        state.Buff = buff;
        player.Minions.OutputQueue.Add(new(InstanceEntities.AddBuff(player.Id, buff, 238,
            u68: BitConverter.SingleToUInt32Bits((float)(state.Until - now)), stacks: (ushort)state.Stacks.Count),
            $"player corrupted blood ({state.Stacks.Count})", WorldId: player.Id));
    }

    internal static int CorruptedBloodStacks(Player player, double now) =>
        Blood.TryGetValue(player, out var state) && now < state.Until ? state.Stacks.Count : 0;

    internal static void RemoveCorruptedBlood(Player player, double now, double protection)
    {
        if (CorruptedBloodStacks(player, now) == 0 || !Blood.TryGetValue(player, out var state)) return;
        state.Settle(now, GuardDamageOverTime(player, DamageType.Physical));
        state.Stacks.Clear();
        state.Until = now;
        state.ProtectedUntil = now + protection;
        BloodIcon(player, state, now);
    }

    public static void ClearCorruptedBlood(Player player)
    {
        if (!Blood.TryGetValue(player, out var state)) return;
        state.Stacks.Clear();
        BloodIcon(player, state, 0);
        Blood.Remove(player);
    }

    static int AdvanceCorruptedBlood(Player player, BloodState state, double now, IReadOnlyDictionary<uint, int> stats,
        bool physicalImmune, double letheFactor)
    {
        int Get(uint key) => stats.GetValueOrDefault(key);
        state.Settle(now, GuardDamageOverTime(player, DamageType.Physical));
        bool immune = Get(Stat.ImmuneToCorruptedBlood) != 0;
        if (immune) state.Stacks.Clear();
        if (state.Stacks.Count == 0)
        {
            if (state.Buff.HasValue) BloodIcon(player, state, now);
        }
        else
        {
            double rate = RecipientEffectTime.Rate(2, Get);
            if (rate != state.ExpiryRate)
            {
                state.Until = RecipientEffectTime.RetimedDeadline(state.Until, now, state.ExpiryRate, rate);
                state.ExpiryRate = rate;
                BloodIcon(player, state, now);
            }
        }
        double damage = state.Accrued;
        state.Accrued = 0;
        bool unaffected = Get(Stat.UnaffectedByCorruptedBlood) != 0
            || Get(Stat.UnaffectedByCorruptedBloodWhileLeeching) != 0 && Get(Stat.IsLeeching) != 0;
        if (immune || unaffected || physicalImmune || player.Immune || InGracePeriod(player) || Get(Stat.BaseCannotBeDamaged) != 0)
            damage = state.Carry = 0;
        else if (damage > 0)
            damage *= PlayerAilmentDamageTakenFactor(player, now, 0) * letheFactor;
        int whole = IncomingResourceDefences.TakenOverTime(damage, Get, ref state.Carry,
            IncomingResourceDefences.LessTakenPct(player.Pools, Get),
            typedIncreasedPct: Get(Stat.PhysicalDamageTakenPlusPct)
                + (player.Walk.Moving(now) ? Get(Stat.PhysicalDamageOverTimeTakenPlusPctWhileMoving) : 0),
            reductionPct: ChargeEffects.Reduction(Get).Physical, finalTakenPct: Get(IncomingResourceDefences.UnhingedSaneDamageFinal));
        if (state.Stacks.Count == 0 && now >= state.ProtectedUntil) Blood.Remove(player);
        return IncomingResourceDefences.DamageOverTime(player.EnergyShield, whole, Get(IncomingResourceDefences.BypassStat), now, ref player.NonChaosDotBypassCarry);
    }
}
