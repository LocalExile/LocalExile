/// <summary>Petrified Blood's active buff and the life loss it defers. The buff reserves mana and, while it runs,
/// prevents a share of the life loss a hit would deal below half Life, then loses part of that prevented amount over
/// four seconds. The state keeps each hit's deferred loss as its own four-second segment, so overlapping hits add their
/// rates instead of refreshing one shared pool.</summary>
sealed class PetrifiedBloodState
{
    public bool Active { get; private set; }
    public ushort SkillId { get; private set; }
    public int Effect { get; private set; }
    public ushort Buff { get; private set; }
    /// <summary>The buff instance this connection actually sent; 0 while none is presented.</summary>
    public ushort PresentedBuff { get; set; }
    public int PreventPct { get; private set; } = 40;
    public int LoseOverTimePct { get; private set; } = 100;
    public int LifeCostPct { get; private set; } = 40;
    /// <summary>The Low Life boundary in percent of maximum Life (50 by default, changed by the threshold stats).</summary>
    public int LowLifeThresholdPct { get; private set; } = LowLife.DefaultThresholdPct;
    /// <summary>The recovery-rate value last sent for the client's own pool prediction, or null when it must be sent
    /// again. The client steps Life from <c>life_recovery_per_minute</c> and only knows the maximum pool, so while the
    /// buff blocks non-flask recovery that rate goes as 0 and returns when Life is back on Low Life.</summary>
    public bool? SentRecoveryBlocked { get; set; }
    /// <summary>The buff instance this connection actually sent for the deferred life loss; 0 while none is shown.</summary>
    public ushort PresentedLossBuff { get; set; }
    /// <summary>The deferred-loss rate and last segment end carried by the shown buff, for change detection.</summary>
    public int SentLossPerMinute { get; set; }
    public double SentLossExpiry { get; set; }
    /// <summary>The skill's mana reservation in permyriad (3500 = 35%).</summary>
    public int ManaPercent { get; private set; }

    readonly List<Debt> debts = [];
    double last;
    double carry;

    readonly record struct Debt(double Rate, double Start, double End);

    public void Activate(ushort skillId, int effect, int loseOverTimePct, int preventPct, int lifeCostPct, int manaPercent,
        int lowLifeThresholdPct, ushort buff, double now)
    {
        Active = true;
        SkillId = skillId;
        Effect = effect;
        LoseOverTimePct = Math.Clamp(loseOverTimePct, 0, 100);
        PreventPct = Math.Clamp(preventPct, 0, 100);
        LifeCostPct = Math.Max(0, lifeCostPct);
        ManaPercent = Math.Max(0, manaPercent);
        LowLifeThresholdPct = Math.Clamp(lowLifeThresholdPct, 0, 100);
        SentRecoveryBlocked = null;
        Buff = buff;
        last = now;
    }

    /// <summary>Re-reads the gem's values after a level, quality or support change without disturbing the running
    /// buff, its instance or the life loss already owed.</summary>
    public void Refresh(int effect, int loseOverTimePct, int preventPct, int lifeCostPct, int manaPercent,
        int lowLifeThresholdPct)
    {
        if (!Active) return;
        Effect = effect;
        LoseOverTimePct = Math.Clamp(loseOverTimePct, 0, 100);
        PreventPct = Math.Clamp(preventPct, 0, 100);
        LifeCostPct = Math.Max(0, lifeCostPct);
        ManaPercent = Math.Max(0, manaPercent);
        LowLifeThresholdPct = Math.Clamp(lowLifeThresholdPct, 0, 100);
    }

    /// <summary>Re-reads only the Low Life boundary after a passive or item changes the threshold stats.</summary>
    public void RefreshThreshold(int lowLifeThresholdPct)
    {
        if (Active) LowLifeThresholdPct = Math.Clamp(lowLifeThresholdPct, 0, 100);
    }

    /// <summary>Ends the buff and its reservation but keeps the life loss already owed; deactivating Petrified Blood
    /// does not forgive the deferred damage.</summary>
    public void Suspend()
    {
        Active = false;
        SkillId = 0;
        Effect = 0;
        ManaPercent = 0;
        SentRecoveryBlocked = null;
        Buff = 0;
    }

    /// <summary>Death (or a fresh character) drops the buff, its reservation and every unpaid segment.</summary>
    public void Clear()
    {
        Suspend();
        debts.Clear();
        carry = 0;
    }

    public void AddDeferred(double now, double amount)
    {
        if (amount <= 0) return;
        if (debts.Count == 0) last = now;
        debts.Add(new(amount / PetrifiedBlood.DeferSeconds, now, now + PetrifiedBlood.DeferSeconds));
    }

    /// <summary>The whole life points owed since the previous call. Fractions stay in the state; the caller applies
    /// them to the life pool in whole points, as the other recovery loops do.</summary>
    public double Take(double now)
    {
        if (now < last) now = last;
        if (debts.Count == 0) { last = now; return 0; }
        double amount = 0;
        foreach (var debt in debts)
            amount += debt.Rate * Math.Max(0, Math.Min(now, debt.End) - Math.Max(last, debt.Start));
        debts.RemoveAll(debt => debt.End <= now);
        last = now;
        carry += amount;
        double whole = Math.Truncate(carry);
        carry -= whole;
        return whole;
    }

    /// <summary>The current deferred-loss rate and the last segment's end, for the client's orb loss overlay. This
    /// does not consume or settle the debt; <see cref="Take"/> owns the actual life loss.</summary>
    public (int PerMinute, double Expiry) Display(double now)
    {
        if (now < last) now = last;
        double rate = 0, expiry = now;
        foreach (var debt in debts)
        {
            if (debt.End <= now || debt.Start > now) continue;
            rate += debt.Rate;
            expiry = Math.Max(expiry, debt.End);
        }
        return ((int)Math.Clamp(Math.Round(rate * 60), 0, int.MaxValue), expiry);
    }
}

/// <summary>Petrified Blood (`PetrifiedBlood` GrantedEffects row, BuffDefinitions 1819): the data and the pure
/// calculations. The effect prevents 40% of the life loss a hit deals below half Life; the gem's level decides how
/// much of that prevented amount is lost over four seconds (100% at level 1, 81% at level 20, less with quality). The
/// rest is permanent mitigation. Hits only: damage over time and life loss already owed are not protected.</summary>
static class PetrifiedBlood
{
    public const string EffectId = "PetrifiedBlood";

    /// <summary>BuffDefinitions row <c>petrified_blood</c> (the table's 1819th row).</summary>
    public const ushort BuffRow = 1819;

    /// <summary>BuffDefinitions row <c>slow_life_loss_from_hit_damage</c> (1796): the orb's deferred-loss overlay.</summary>
    public const ushort SlowLossBuffRow = 1796;

    public const double DeferSeconds = 4;

    public const string PreventSource = "petrified_blood_%_life_loss_below_half_from_hit_to_prevent",
        LoseOverTimeSource = "petrified_blood_%_prevented_life_loss_to_lose_over_time",
        LifeCostSource = "skill_grants_life_cost_%_mana_cost_while_not_on_low_life";

    /// <summary>Low Life uses the shared threshold: 50% by default, changed by the low-life threshold stats.</summary>
    public static bool OnLowLife(ClientState conn) =>
        LowLife.Is(conn.PlayerLife, conn.PlayerMaxLife, conn.PetrifiedBlood.LowLifeThresholdPct);

    /// <summary>The largest whole Life total that is still Low Life under the buff's threshold.</summary>
    public static uint LowLifeMaximum(ClientState conn) =>
        (uint)((long)conn.PlayerMaxLife * conn.PetrifiedBlood.LowLifeThresholdPct / 100);

    /// <summary>Whether the buff currently stops non-flask recovery: it runs and Life is at or above Low Life.</summary>
    public static bool BlocksRecovery(ClientState conn) =>
        conn.PetrifiedBlood.Active && conn.PlayerLife > 0 && conn.PlayerLife >= LowLifeMaximum(conn);

    /// <summary>
    /// Splits a hit's post-Energy-Shield Life loss into the part taken at once and the part Petrified Blood defers.
    /// Only the loss below half Life is protected, so a hit that leaves the player above Low Life is unchanged.
    /// The immediate part is rounded to a whole point (the life pool's precision); the deferred part keeps its
    /// fraction inside the state.
    /// </summary>
    public static (int Immediate, double Deferred) SplitHitLoss(long life, uint maxLife, int lifeLoss,
        int preventPct, int loseOverTimePct)
    {
        if (lifeLoss <= 0) return (lifeLoss, 0);
        long half = maxLife / 2;
        long aboveHalf = Math.Min(lifeLoss, Math.Max(0, life - half));
        long belowHalf = lifeLoss - aboveHalf;
        if (belowHalf <= 0) return (lifeLoss, 0);
        double prevented = belowHalf * preventPct / 100.0;
        int immediate = (int)Math.Round(lifeLoss - prevented, MidpointRounding.AwayFromZero);
        // The integer immediate part can move the split by less than one point; never defer more than the hit still
        // has left to lose, or a level-1 Petrified Blood could take more Life than the hit dealt.
        double deferred = Math.Min(prevented * loseOverTimePct / 100.0, Math.Max(0, lifeLoss - immediate));
        return (immediate, deferred);
    }

    /// <summary>Applies the buff to a hit's life loss and records the deferred amount. Returns the life loss the
    /// caller takes at once.</summary>
    public static int PreventHitLifeLoss(ClientState conn, int lifeLoss, double now)
    {
        var state = conn.PetrifiedBlood;
        if (!state.Active || lifeLoss <= 0) return lifeLoss;
        var (immediate, deferred) = SplitHitLoss(conn.PlayerLife, conn.PlayerMaxLife, lifeLoss,
            state.PreventPct, state.LoseOverTimePct);
        state.AddDeferred(now, deferred);
        return immediate;
    }

    /// <summary>
    /// Life recovery other than flasks, including life leech, cannot raise Life above Low Life. A pool already above
    /// it gains nothing; below it the recovery stops at the boundary. Damage (a negative amount) keeps the ordinary
    /// full maximum. Leech instances themselves are not cleared at Low Life: their own cap is maximum unreserved Life,
    /// but the recovery they apply through this function is capped like regeneration and Recoup.
    /// </summary>
    public static uint RecoverLife(ClientState conn, uint current, double amount, ref double carry, uint minimum = 0)
    {
        if (amount > 0 && conn.PetrifiedBlood.Active)
        {
            uint half = LowLifeMaximum(conn);
            uint maximum = current >= half ? current : half;
            return RecoveryPool.Advance(current, maximum, amount, ref carry, minimum);
        }
        return RecoveryPool.Advance(current, conn.PlayerMaxLife, amount, ref carry, minimum);
    }

    /// <summary>The life cost a skill adds while the buff runs and the player is not on Low Life: the buff's share of
    /// the base mana cost, rounded to a whole point.</summary>
    public static int LifeCost(ClientState conn, int manaCost)
    {
        var state = conn.PetrifiedBlood;
        if (!state.Active || manaCost <= 0 || OnLowLife(conn)) return 0;
        return (int)Math.Round(manaCost * (double)state.LifeCostPct / 100.0, MidpointRounding.AwayFromZero);
    }

    /// <summary>The mana the buff reserves, using the same efficiency and per-skill percent rounding as
    /// <see cref="AuraCatalog.RequiredMana"/>.</summary>
    public static int RequiredMana(CharacterStats.Pools pools, PetrifiedBloodState state) =>
        state.Active ? RequiredMana(pools, state.ManaPercent) : 0;

    public static int RequiredMana(CharacterStats.Pools pools, int manaPercent)
    {
        if (manaPercent <= 0) return 0;
        double efficiency = Math.Max(-99, pools.ManaReservationEfficiencyPct) / 100.0 + 1;
        double percent = Math.Round(manaPercent / 100.0 / efficiency, 2, MidpointRounding.AwayFromZero);
        return (int)Math.Clamp(Math.Ceiling(pools.Mana * percent / 100.0), 0, int.MaxValue);
    }
}
