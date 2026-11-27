/// <summary>Ordinary ES: non-chaos damage is absorbed first; damage restarts the two-second recharge delay.
/// Character.ot supplies 2000%/minute recharge (one third of maximum per second). Caller supplies game time.</summary>
public sealed class EnergyShield
{
    public readonly record struct Snapshot(uint Current, double Fraction, double? InterruptedSecondsAgo);

    public uint Current { get => current; private set { bool was = Full; if (current != value) { current = value; Changed?.Invoke(); } if (Full != was) ConditionsChanged?.Invoke(); } }
    public uint Maximum { get => maximum; private set { bool was = Full; maximum = value; if (Full != was) ConditionsChanged?.Invoke(); } }
    public bool Full => Maximum > 0 && Current >= Maximum;
    public bool Recharging { get => recharging; private set { if (recharging == value) return; recharging = value; ConditionsChanged?.Invoke(); } }
    uint current, maximum;
    bool recharging;
    /// <summary>Full or Recharging changed (what stat conditions read).</summary>
    public Action? ConditionsChanged;
    /// <summary>Current changed (the Life component writes it: AreaLife).</summary>
    public Action? Changed;
    double interruptedAt = double.NegativeInfinity, fraction;
    double rechargeStartedAt = double.NegativeInfinity, rechargeProtection;

    public void SetRechargeProtection(double seconds) => rechargeProtection = Math.Max(0, seconds);

    void StopRecharge()
    {
        rechargeStartedAt = double.NegativeInfinity;
        Recharging = false;
    }

    public void SetMaximum(uint maximum, bool refill = false)
    {
        Maximum = maximum;
        Current = refill ? maximum : Math.Min(Current, maximum);
        if (Current == Maximum) { fraction = 0; StopRecharge(); }
    }
    public void Interrupt(double now)
    {
        if (!double.IsFinite(now) || Recharging && now >= rechargeStartedAt && now - rechargeStartedAt < rechargeProtection) return;
        interruptedAt = Math.Max(interruptedAt, now);
        StopRecharge();
    }
    /// <summary>Start the recharge timer at an accepted use or block.</summary>
    public void StartRecharge(double now, Func<uint, int> get)
    {
        if (!double.IsFinite(now) || Current >= Maximum || Recharging || RechargePerMinute(Maximum, get) <= 0
            || get(Stat.CannotRecoverEnergyShield) != 0 || get(Stat.NoEnergyShieldRecovery) != 0) return;
        interruptedAt = double.NegativeInfinity;
        rechargeStartedAt = now;
        Recharging = true;
    }

    public int Damage(int amount, bool bypass, double now)
    {
        if (amount <= 0) return 0;
        Interrupt(now);
        if (bypass) return amount;
        uint absorbed = Math.Min(Current, (uint)amount);
        Current -= absorbed;
        return amount - (int)absorbed;
    }
    public void Empty() { Current = 0; fraction = 0; StopRecharge(); }
    /// <summary>A cost paid from Energy Shield (a skill's cost): unlike damage, it does not restart the recharge delay.
    /// False, and nothing spent, when there is not that much.</summary>
    public bool Spend(uint amount)
    {
        if (amount > Current) return false;
        Current -= amount;
        return true;
    }
    /// <summary>Instant recovery (Kiloava's Leadership on block), up to the maximum.</summary>
    public void Recover(uint amount)
    {
        Current = (uint)Math.Min(Maximum, (long)Current + amount);
        if (Current == Maximum) { fraction = 0; StopRecharge(); }
    }
    // Leech/gain does not start recharge or reset its interruption timer.
    public void Gain(double amount) => Gain(amount, Maximum);

    public void Gain(double amount, uint ceiling)
    {
        if (!double.IsFinite(amount) || amount <= 0) return;
        uint cap = Math.Min(Maximum, Math.Max(Current, ceiling));
        Current = RecoveryPool.Advance(Current, cap, amount, ref fraction);
        if (Current >= cap) StopRecharge();
    }
    /// <summary>Loss of a pool, independent of incoming damage and its recharge interruption.</summary>
    public void Lose(double amount, ref double carry)
    {
        Current = RecoveryPool.Advance(Current, Maximum, -amount, ref carry);
        if (Current == 0) fraction = 0;
    }

    public Snapshot Capture(double now)
    {
        double? interruptedSecondsAgo = !double.IsFinite(interruptedAt) ? null
            : double.IsFinite(now) && double.IsFinite(now - interruptedAt) ? now - interruptedAt : 0;
        double carry = Current < Maximum && double.IsFinite(fraction) && fraction is >= 0 and < 1 ? fraction : 0;
        return new(Current, carry, interruptedSecondsAgo);
    }
    public void Restore(Snapshot snapshot, double now)
    {
        Current = Math.Min(snapshot.Current, Maximum);
        fraction = Current < Maximum && double.IsFinite(snapshot.Fraction) && snapshot.Fraction is >= 0 and < 1
            ? snapshot.Fraction : 0;
        StopRecharge();
        if (snapshot.InterruptedSecondsAgo is not double secondsAgo)
        {
            interruptedAt = double.NegativeInfinity;
            return;
        }
        if (!double.IsFinite(secondsAgo) || !double.IsFinite(now) || !double.IsFinite(now - secondsAgo))
        {
            interruptedAt = double.IsFinite(now) ? now : 0;
            return;
        }
        interruptedAt = now - secondsAgo;
    }
    /// <summary>Whether recharge would run now, full or not: no damage within the delay and a positive rate.</summary>
    public bool RechargeReady(double now, Func<uint, int> get) =>
        Maximum > 0 && (NotDelayed(get) || now >= interruptedAt + Delay(get)) && RechargePerMinute(Maximum, get) > 0;
    /// <summary>"Energy Shield Recharge is not delayed by Damage" (Vaal Discipline): while it holds, damage taken does not
    /// hold back recharge, and a delay already running is dropped.</summary>
    public static bool NotDelayed(Func<uint, int> get) => get(Stat.EnergyShieldRechargeNotDelayedByDamage) != 0;
    public static uint Max(int flat, int increased) =>
        (uint)Math.Clamp(((long)Math.Max(0, flat) * Math.Max(0L, 100L + increased) + 50) / 100, 0, int.MaxValue);
    public static double Delay(Func<uint, int> get) => 2 / Math.Max(0.01, 1 + get(Stat.EnergyShieldDelayMinusPct) / 100.0);
    public static double RechargePerMinute(uint max, Func<uint, int> get) =>
        get(Stat.CannotRechargeEnergyShield) != 0 || get(Stat.NoEnergyShieldRechargeOrRegeneration) != 0
            || get(Stat.KeystoneGhostReaver) != 0 ? 0 :
        BaseRechargePerMinute(max, get) * Math.Max(0, 1 + get(Stat.EnergyShieldRecoveryRatePlusPct) / 100.0)
        * PanelStats.MapRecoveryMultiplier(get);

    public static double BaseRechargePerMinute(uint max, Func<uint, int> get) =>
        max * 20.0 * Math.Max(0, 1 + get(Stat.EnergyShieldRechargeRatePlusPct) / 100.0)
        * GameEvents.StatMultiplier(GameEvents.EnergyShieldRechargeMore, get)
        // Wicked Ward: "40% less Energy Shield Recharge Rate".
        * (get(Stat.KeystoneWickedWard) != 0 ? 0.6 : 1)
        // Eldritch Battery: "50% less Energy Shield Recharge Rate".
        * (get(Stat.KeystoneEldritchBattery) != 0 ? 0.5 : 1);
    public static double RegenPerMinute(uint max, Func<uint, int> get) =>
        get(Stat.MapPlayersNoRegenerationIncludingEs) != 0 || get(Stat.NoEnergyShieldRechargeOrRegeneration) != 0 ? 0 :
        Math.Max(0, get(Stat.BaseEnergyShieldRegenerationRatePerMinute)
            + 60d * get(Stat.EnergyShieldRegenerationRatePerSecond) + max * get(Stat.BaseEnergyShieldRegenerationRatePerMinutePct) / 100.0)
        * Math.Max(0, 1 + get(Stat.EnergyShieldRegenerationRatePlusPct) / 100.0)
        * Math.Max(0, 1 + get(Stat.EnergyShieldRecoveryRatePlusPct) / 100.0) * PanelStats.MapRecoveryMultiplier(get);

    /// <summary>Whether life regeneration converted to Energy Shield by Zealot's Oath may run. The native ES
    /// regeneration prohibitions apply to the converted amount as well.</summary>
    public static bool ConversionAllowed(Func<uint, int> get) =>
        get(Stat.MapPlayersNoRegenerationIncludingEs) == 0 && get(Stat.NoEnergyShieldRechargeOrRegeneration) == 0 &&
        get(Stat.CannotRecoverEnergyShield) == 0 &&
        get(Stat.NoEnergyShieldRecovery) == 0;
    // The ordinary 0x1b3 virtual recharge-active stat does not start the client's recharge predictor. Feed its
    // verified native recovery input during the recharge phase instead; keep the recharge-rate stat for the panel.
    public static double PredictionPerMinute(uint max, Func<uint, int> get, bool recharging) =>
        RegenPerMinute(max,get) + (recharging ? RechargePerMinute(max,get) : 0);
    public void Advance(double from, double to, Func<uint, int> get, bool alive)
    {
        if (!alive) { Empty(); return; }
        if (NotDelayed(get)) interruptedAt = double.NegativeInfinity;
        if (to <= from) return;
        int ceilingPct = PetrifiedBlood.RecoveryCeilingPct(get);
        uint ceiling = ceilingPct < 100 ? (uint)((long)Maximum * ceilingPct / 100) : Maximum;
        if (Current >= Maximum || Current >= ceiling) { StopRecharge(); return; }
        double start = Recharging ? rechargeStartedAt : interruptedAt + Delay(get);
        double recharge = RechargePerMinute(Maximum, get);
        if (to >= start && recharge > 0)
        {
            if (!Recharging) rechargeStartedAt = Math.Max(from, start);
            Recharging = true;
        }
        else StopRecharge();
        double rechargeSeconds = Math.Max(0, to - Math.Max(from, start));
        double change = fraction + RegenPerMinute(Maximum, get) * (to - from) / 60 + recharge * rechargeSeconds / 60;
        uint whole = (uint)Math.Min(uint.MaxValue, Math.Floor(change));
        Current = (uint)Math.Min(ceiling, (long)Current + whole);
        fraction = Current >= ceiling ? 0 : change - whole;
        if (Current >= ceiling) StopRecharge();
    }
}
