using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

// The coefficients belong to qualified physical sources; the charge session owns their time intervals.
public sealed class ChargeDegeneration
{
    public const string Cold = "cold_damage_taken_per_minute_per_frenzy_charge_while_moving";
    public const string Fire = "fire_damage_taken_per_minute_per_endurance_charge_if_you_have_been_hit_recently";
    public const string Lightning = "lightning_damage_taken_per_minute_per_power_charge_if_have_crit_recently";
    public static readonly string[] Coefficients = [Fire, Cold, Lightning];
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

    static void ConfigureChargeDegeneration(ClientState conn, IReadOnlyList<ItemModStats.Worn> worn, HashSet<uint> inactive)
    {
        var session = ChargesOf(conn); var state = session.Degeneration;
        double now = ResourceEffectsTime(conn);
        var active = worn.Where(w => !inactive.Contains(w.Id)).ToArray();
        var items = active.Select(w => w.Item).ToArray();
        if (!ReferenceEquals(state.Rules, CombatLifetime) || state.Epoch != conn.ProjectileEpoch
            || !CurrentGameWork || conn.Closed || conn.RageRetired || conn.PlayerLife == 0)
        {
            state.Clear(now); state.Rules = CombatLifetime; state.Epoch = conn.ProjectileEpoch;
            state.Items = []; state.Hosts = []; Array.Clear(state.PerMinute);
        }
        if (conn.LeavingArea || !CurrentGameWork || conn.Closed || conn.RageRetired || conn.PlayerLife == 0) return;
        session.SettleDegeneration(now);
        if (items.Length != state.Items.Length || items.Where((item, i) => !ReferenceEquals(item, state.Items[i])).Any())
        {
            var raw = ItemModStats.Sum(items, ItemsFor(conn).Mods);
            state.PerMinute = ChargeDegeneration.Coefficients.Select(n => Math.Max(0, raw.GetValueOrDefault(n))).ToArray();
            state.Items = items;
            state.Hosts = active.Where(w => ItemModStats.Sum([w.Item], ItemsFor(conn).Mods)
                .Any(s => ChargeDegeneration.Coefficients.Contains(s.Key) && s.Value > 0)).Select(w => w.Id).Order().ToArray();
        }
        state.Active = state.Hosts.Length > 0;
        if (state.Active) ResourceEffectsOf(conn);
    }

    public static bool ChargeDegenerationActive(ClientState conn)
    {
        var session = ChargesOf(conn); var state = session.Degeneration; double now = ResourceEffectsTime(conn);
        return state.Active && (state.PerMinute[0] > 0 && now < state.HitUntil && !session.Converted(ChargeType.Endurance) && session.Effective(ChargeType.Endurance) > 0
            || state.PerMinute[1] > 0 && conn.Walk.Moving(now) && !session.Converted(ChargeType.Frenzy) && session.Effective(ChargeType.Frenzy) > 0
            || state.PerMinute[2] > 0 && now < state.CritUntil && !session.Converted(ChargeType.Power) && session.Effective(ChargeType.Power) > 0);
    }

    public static int AdvanceChargeDegenerationLocked(ClientState conn, double now, IReadOnlyDictionary<uint, int> stats)
    {
        var session = ChargesOf(conn); var state = session.Degeneration;
        if (!ReferenceEquals(state.Rules, CombatLifetime) || state.Epoch != conn.ProjectileEpoch
            || conn.Closed || conn.LeavingArea || conn.RageRetired || !CurrentGameWork)
        { state.Clear(now); return 0; }
        if (!state.Active && !state.Pending.Any(p => p > 0)) return 0;
        session.SettleDegeneration(now);
        int Get(string name) => GolemStat(stats, name);
        int total = 0;
        for (int i = 0; i < 3; i++)
        {
            double raw = state.Pending[i]; state.Pending[i] = 0;
            var type = ChargeDegeneration.Types[i];
            if (raw <= 0 || conn.PlayerLife == 0 || conn.Closed || conn.LeavingArea || !CurrentGameWork
                || InGracePeriod(conn) || InTown(conn) || conn.Immune || HardcoreProgressBlocked(conn)
                || DamageImmunities.From(Get).Contains(type)) continue;
            double resistance = Math.Clamp(Resistance(conn, type), -200, MaxResistance(conn, type));
            int damage = IncomingResourceDefences.TakenOverTime(raw * (1 - resistance / 100d), Get, ref state.TakenCarry[i],
                IncomingResourceDefences.LessTakenPct(conn.Pools, Get),
                typedIncreasedPct: ActorScale.Saturate((long)Get($"{type.ToString().ToLowerInvariant()}_damage_taken_+%") + Get("elemental_damage_taken_+%")),
                reductionPct: ChargeEffects.Reduction(Get).Elemental);
            int life = IncomingResourceDefences.DamageOverTime(conn.EnergyShield, damage,
                Get(IncomingResourceDefences.BypassStat), now, ref state.BypassCarry[i]);
            conn.PlayerLife = (uint)Math.Max(0, (long)conn.PlayerLife - life);
            total = ActorScale.Saturate((long)total + life);
        }
        if (conn.PlayerLife == 0) { state.Clear(now); conn.InvalidateIncomingHits(); RecordHardcoreDeath(conn); }
        return total;
    }

    public static CarriedChargeDegeneration CaptureChargeDegeneration(ClientState conn, double at)
    {
        var session = ChargesOf(conn); session.SettleDegeneration(at); var state = session.Degeneration;
        return new([.. state.Hosts], Math.Max(0, state.HitUntil - at), Math.Max(0, state.CritUntil - at),
            [.. state.Pending], [.. state.TakenCarry], [.. state.BypassCarry]);
    }
    public static void RestoreChargeDegeneration(ClientState conn, CarriedChargeDegeneration value, double at)
    {
        var state = ChargesOf(conn).Degeneration; state.Clear(at);
        state.Rules = CombatLifetime; state.Epoch = conn.ProjectileEpoch; state.Hosts = [.. value.Hosts];
        state.HitUntil = at + value.HitSeconds; state.CritUntil = at + value.CritSeconds;
        value.Pending.CopyTo(state.Pending, 0); value.TakenCarry.CopyTo(state.TakenCarry, 0); value.BypassCarry.CopyTo(state.BypassCarry, 0);
    }
}
