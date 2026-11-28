using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

// The coefficients belong to qualified physical sources; the charge session owns their time intervals.
public sealed class ChargeDegeneration
{
    public const uint Cold = Stat.ColdDamageTakenPerMinutePerFrenzyChargeWhileMoving;
    public const uint Fire = Stat.FireDamageTakenPerMinutePerEnduranceChargeIfYouHaveBeenHitRecently;
    public const uint Lightning = Stat.LightningDamageTakenPerMinutePerPowerChargeIfHaveCritRecently;
    public static readonly uint[] Coefficients = [Fire, Cold, Lightning];
    public static readonly DamageType[] Types = [DamageType.Fire, DamageType.Cold, DamageType.Lightning];
    public double At = double.NaN, HitUntil, CritUntil;
    public readonly double[] Pending = new double[3], TakenCarry = new double[3], BypassCarry = new double[3];
    public int[] PerMinute = new int[3];
    public object? Rules;
    public int Epoch;
    public uint[] Hosts = [];
    public Item[] Items = [];
    public bool Active;

    public void Clear(double at)
    {
        At = at; HitUntil = CritUntil = 0; Active = false;
        Array.Clear(Pending); Array.Clear(TakenCarry); Array.Clear(BypassCarry);
        Array.Clear(PerMinute); Hosts = []; Items = [];
    }

    public void Advance(double now, ChargeSession session, Func<double, double, double> moving)
    {
        if (double.IsNaN(At)) { At = now; return; }
        if (now <= At) return;
        for (int i = 0; i < 3; i++)
        {
            var type = i == 0 ? ChargeType.Endurance : i == 1 ? ChargeType.Frenzy : ChargeType.Power;
            if (PerMinute[i] <= 0 || session.Converted(type)) continue;
            var limits = session.Limits(type);
            int minimum = limits.Minimum, count = session.Effective(type);
            double end = Math.Min(now, i == 0 ? HitUntil : i == 2 ? CritUntil : double.PositiveInfinity);
            double Seconds(double until) => i == 1 ? moving(At, until) : Math.Max(0, until - At);
            double seconds = minimum * Seconds(end)
                + Math.Max(0, count - minimum) * Seconds(Math.Min(end, session.State.ExpiresAt(type)));
            Pending[i] += seconds * PerMinute[i] / 60d;
        }
        At = now;
    }
}

public static partial class Game
{
    public sealed record CarriedChargeDegeneration(uint[] Hosts, double HitSeconds, double CritSeconds,
        double[] Pending, double[] TakenCarry, double[] BypassCarry);

    static void ConfigureChargeDegeneration(Player player, IReadOnlyList<ItemModStats.Worn> worn, HashSet<uint> inactive)
    {
        var session = ChargesOf(player); var state = session.Degeneration;
        double now = ResourceEffectsTime(player);
        var active = worn.Where(w => !inactive.Contains(w.Id)).ToArray();
        var items = active.Select(w => w.Item).ToArray();
        if (!ReferenceEquals(state.Rules, CombatLifetime) || state.Epoch != player.ProjectileEpoch
            || !CurrentGameWork || player.Closed || player.RageRetired || player.PlayerLife == 0)
        {
            state.Clear(now); state.Rules = CombatLifetime; state.Epoch = player.ProjectileEpoch;
            state.Items = []; state.Hosts = []; Array.Clear(state.PerMinute);
        }
        if (player.LeavingArea || !CurrentGameWork || player.Closed || player.RageRetired || player.PlayerLife == 0) return;
        session.SettleDegeneration(now);
        if (items.Length != state.Items.Length || items.Where((item, i) => !ReferenceEquals(item, state.Items[i])).Any())
        {
            var raw = ItemModStats.Sum(items, ItemsFor(player).Mods);
            state.PerMinute = ChargeDegeneration.Coefficients.Select(n => Math.Max(0, raw.GetValueOrDefault(n))).ToArray();
            state.Items = items;
            state.Hosts = active.Where(w => ItemModStats.Sum([w.Item], ItemsFor(player).Mods)
                .Any(s => ChargeDegeneration.Coefficients.Contains(s.Key) && s.Value > 0)).Select(w => w.Id).Order().ToArray();
        }
        state.Active = state.Hosts.Length > 0;
        if (state.Active) ResourceEffectsOf(player);
    }

    public static bool ChargeDegenerationActive(Player player)
    {
        var session = ChargesOf(player); var state = session.Degeneration; double now = ResourceEffectsTime(player);
        return state.Active && (state.PerMinute[0] > 0 && now < state.HitUntil && !session.Converted(ChargeType.Endurance) && session.Effective(ChargeType.Endurance) > 0
            || state.PerMinute[1] > 0 && player.Walk.Moving(now) && !session.Converted(ChargeType.Frenzy) && session.Effective(ChargeType.Frenzy) > 0
            || state.PerMinute[2] > 0 && now < state.CritUntil && !session.Converted(ChargeType.Power) && session.Effective(ChargeType.Power) > 0);
    }

    public static int AdvanceChargeDegenerationLocked(Player player, double now, IReadOnlyDictionary<uint, int> stats)
    {
        var session = ChargesOf(player); var state = session.Degeneration;
        if (!ReferenceEquals(state.Rules, CombatLifetime) || state.Epoch != player.ProjectileEpoch
            || player.Closed || player.LeavingArea || player.RageRetired || !CurrentGameWork)
        { state.Clear(now); return 0; }
        if (!state.Active && !state.Pending.Any(p => p > 0)) return 0;
        session.SettleDegeneration(now);
        int Get(uint name) => GolemStat(stats, name);
        int total = 0;
        for (int i = 0; i < 3; i++)
        {
            double raw = state.Pending[i]; state.Pending[i] = 0;
            var type = ChargeDegeneration.Types[i];
            if (raw <= 0 || player.PlayerLife == 0 || player.Closed || player.LeavingArea || !CurrentGameWork
                || InGracePeriod(player) || InTown(player) || player.Immune || HardcoreProgressBlocked(player)
                || DamageImmunities.From(Get).Contains(type)) continue;
            double resistance = Math.Clamp(Resistance(player, type), -200, MaxResistance(player, type));
            int damage = IncomingResourceDefences.TakenOverTime(raw * (1 - resistance / 100d), Get, ref state.TakenCarry[i],
                IncomingResourceDefences.LessTakenPct(player.Pools, Get),
                typedIncreasedPct: ActorScale.Saturate((long)Get(Stat.Of($"{type.ToString().ToLowerInvariant()}_damage_taken_+%")) + Get(Stat.ElementalDamageTakenPlusPct)),
                reductionPct: ChargeEffects.Reduction(Get).Elemental);
            int life = DamagePlayerNonChaosOverTime(player, type, damage, now, Get, ref state.BypassCarry[i]);
            player.PlayerLife = VaalImmortalCallLife(player, (uint)Math.Max(0, (long)player.PlayerLife - life), now);
            total = ActorScale.Saturate((long)total + life);
        }
        if (player.PlayerLife == 0) { state.Clear(now); player.InvalidateIncomingHits(); RecordHardcoreDeath(player); }
        return total;
    }

    public static CarriedChargeDegeneration CaptureChargeDegeneration(Player player, double at)
    {
        var session = ChargesOf(player); session.SettleDegeneration(at); var state = session.Degeneration;
        return new([.. state.Hosts], Math.Max(0, state.HitUntil - at), Math.Max(0, state.CritUntil - at),
            [.. state.Pending], [.. state.TakenCarry], [.. state.BypassCarry]);
    }
    public static void RestoreChargeDegeneration(Player player, CarriedChargeDegeneration value, double at)
    {
        var state = ChargesOf(player).Degeneration; state.Clear(at);
        state.Rules = CombatLifetime; state.Epoch = player.ProjectileEpoch; state.Hosts = [.. value.Hosts];
        state.HitUntil = at + value.HitSeconds; state.CritUntil = at + value.CritSeconds;
        value.Pending.CopyTo(state.Pending, 0); value.TakenCarry.CopyTo(state.TakenCarry, 0); value.BypassCarry.CopyTo(state.BypassCarry, 0);
    }
}
