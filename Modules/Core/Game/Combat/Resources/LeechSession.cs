

/// <summary>Owner-local leech state. All callers hold the owner's combat lock and use its pausable game clock.</summary>
public sealed class LeechSession
{
    public LeechRecoveryState Life { get; } = new();
    public LeechRecoveryState Mana { get; } = new();
    public LeechRecoveryState EnergyShield { get; } = new();
    LeechLimits.Snapshot? limits;
    bool keepLife, keepEnergyShield, keepAttackEnergyShield;
    bool convertLifeToEnergyShield;
    bool immortalAmbition, fullLife, fullShield;
    bool lifeRecoveryRedirected;
    double routedAt, pendingLifeRecovery, pendingShieldRecovery;
    public bool ConvertsLifeToEnergyShield => convertLifeToEnergyShield;
    double lifeInstant, manaInstant, shieldInstant, convertedLifeInstant;
    bool lifeInstantReplaced, manaInstantReplaced, shieldInstantReplaced;
    // Vaal Pact: "Life Leech from Melee Damage is Instant" — the life instant fraction a melee hit uses instead.
    double meleeLifeInstant;
    bool recoverLife = true, recoverEnergyShield = true;
    public bool RecoversLife => recoverLife;
    public bool RecoversEnergyShield => recoverEnergyShield;
    // Vaal Pact's "Cannot Recover Life other than from Leech": life gain on hit is not leech, so the keystone blocks it
    // while leech itself keeps recovering. Regeneration and the other non-leech sources are gated where they commit.
    bool vaalPact;
    public bool LifeGainOnHitBlocked => vaalPact;
    public bool InstantLifeChanged, InstantManaChanged;
    public double LifeInstantCarry, ManaInstantCarry;
    // What a kill recovers (RecoverOnKill), from the same stat snapshot as this configuration.
    public KillRecovery OnKill { get; set; }

    /// <summary>Leeching of a resource started or stopped (the stats that read it follow).</summary>
    public Action? Changed;
    /// <summary>An instance was granted or removed, or the rates were configured (the orbs' leech buffs follow).</summary>
    public Action? DisplayChanged;

    int Count => Life.Count + Mana.Count + EnergyShield.Count;

    internal void SetLifeRecoveryRedirected(double now, bool redirected)
    {
        if (redirected == lifeRecoveryRedirected) return;
        SettleRedirectedLife(now);
        lifeRecoveryRedirected = redirected;
        DisplayChanged?.Invoke();
    }

    // Reports a start or stop, and a granted or removed instance, against the state before a change.
    void Report(double now, (bool, bool, bool) before, int count, bool displayChanged = false)
    {
        if (Active(now) != before) Changed?.Invoke();
        if (displayChanged || Count != count) DisplayChanged?.Invoke();
    }

    /// <summary>When the next instance ends; null without one.</summary>
    public double? NextExpiry(double now)
    {
        SettleRedirectedLife(now);
        double next = double.PositiveInfinity;
        foreach (var state in new[] { Life, Mana, EnergyShield })
            if (state.NextExpiry(now) is double expires) next = Math.Min(next, expires);
        return double.IsFinite(next) ? next : null;
    }

    /// <summary>When the leech of a resource next stops on its own; null while none leeches.</summary>
    public double? NextChangeAt(double now)
    {
        SettleRedirectedLife(now);
        double next = double.PositiveInfinity;
        foreach (var state in new[] { Life, Mana, EnergyShield })
            if (state.Until(now) is double until) next = Math.Min(next, until);
        return double.IsFinite(next) ? next : null;
    }

    public void Configure(double now, LeechLimits.Snapshot value, Func<uint, int> get,
        Func<uint, int?>? replacements = null, int equippedClaws = 0)
    {
        var before = Active(now);
        int count = Count;
        ConfigureCore(now, value, get, replacements, equippedClaws);
        Report(now, before, count);
        DisplayChanged?.Invoke();
    }

    void ConfigureCore(double now, LeechLimits.Snapshot value, Func<uint, int> get,
        Func<uint, int?>? replacements, int equippedClaws)
    {
        int? replacement(uint key) => replacements?.Invoke(key);
        limits = value;
        convertLifeToEnergyShield = replacement(Stat.VirtualLeechEnergyShieldInsteadOfLife) is int converted
            ? converted != 0 : get(Stat.KeystoneGhostReaver) != 0 || get(Stat.LeechEnergyShieldInsteadOfLife) != 0;
        bool redirect = get(Stat.KeystoneSoulTether) != 0;
        if (redirect && !immortalAmbition) routedAt = now;
        if (!redirect) pendingShieldRecovery = 0;
        immortalAmbition = redirect;
        keepLife = immortalAmbition || (replacement(Stat.VirtualLifeLeechDoesNotStopAtFullLife) is int lifeOverleech
            ? lifeOverleech != 0 : get(Stat.LifeLeechDoesNotStopAtFullLife) != 0
                || get(Stat.VirtualLifeLeechDoesNotStopAtFullLife) != 0);
        keepEnergyShield = get(Stat.EnergyShieldLeechDoesNotStopOnFullEnergyShield) != 0;
        keepAttackEnergyShield = get(Stat.EnergyShieldLeechFromAttacksDoesNotStopOnFullEnergyShield) != 0;
        recoverLife = get(Stat.CannotRecoverLife) == 0;
        recoverEnergyShield = get(Stat.CannotRecoverEnergyShield) == 0;
        vaalPact = get(Stat.KeystoneVaalPact) != 0;
        equippedClaws = Math.Clamp(equippedClaws, 0, 2);
        double commonInstant = (double)get(Stat.LeechPctIsInstant)
            + (equippedClaws > 0 ? get(Stat.LeechPctIsInstantWhileWieldingClaw) : 0);
        var lifeReplacement = replacement(Stat.VirtualLifeLeechPctIsInstant);
        var manaReplacement = replacement(Stat.VirtualManaLeechPctIsInstant);
        var shieldReplacement = replacement(Stat.VirtualEnergyShieldLeechPctIsInstant);
        lifeInstantReplaced = lifeReplacement.HasValue;
        manaInstantReplaced = manaReplacement.HasValue;
        shieldInstantReplaced = shieldReplacement.HasValue;
        lifeInstant = Math.Clamp(lifeReplacement
            ?? commonInstant + get(Stat.LifeLeechPctIsInstant)
                + (double)equippedClaws * get(Stat.LifeLeechPctIsInstantPerEquippedClaw), 0, 100);
        // Vaal Pact: "Life Leech from Melee Damage is Instant". A melee hit's life leech is fully instant; a non-melee
        // hit keeps the ordinary fraction. Ghost Reaver's life-to-ES conversion keeps its own (no interaction).
        meleeLifeInstant = get(Stat.KeystoneVaalPact) != 0 || get(Stat.MeleeDamageLifeLeechIsInstant) != 0
            ? 100 : lifeInstant;
        manaInstant = Math.Clamp(manaReplacement ?? commonInstant, 0, 100);
        shieldInstant = Math.Clamp(shieldReplacement ?? commonInstant, 0, 100);
        // GGG 3.22.1d explicitly preserves Life-instant sources through Ghost Reaver. Keep that source scope:
        // native ES grants do not inherit a Life-only bonus.
        convertedLifeInstant = lifeInstant;
        foreach (var resource in value.Resources)
        {
            var state = State(resource.Pool);
            double speed = (double)get(Stat.Of($"{resource.Pool}_leech_speed_+%")) + get(Stat.LifeManaEsLeechSpeedPlusPct);
            double total = Math.Max(0, 1 + speed / 100);
            // Both raw flags describe the same boolean doubling; two copies do not quadruple recovery.
            if (resource.Pool == "life" && (get(Stat.LifeLeechSpeedIsDoubled) != 0
                || get(Stat.TotalRecoveryPerMinuteFromLifeLeechIsDoubled) != 0)) total *= 2;
            double recovery = Math.Max(0, 1 + get(Stat.Of($"{resource.Pool}_recovery_rate_+%")) / 100.0);
            if (resource.Pool is "life" or "energy_shield") recovery *= PanelStats.MapRecoveryMultiplier(get);
            if (resource.Pool == "life" && !recoverLife || resource.Pool == "energy_shield" && !recoverEnergyShield) recovery = 0;
            state.Configure(now, new(total, resource.MaximumPerSecond, recovery));
            if (resource.CannotLeech) state.Clear(now);
        }
    }

    public (double Life, double Mana, double EnergyShield) Hit(double now,
        (double Life, double Mana, double EnergyShield) amounts, bool lifeFull, bool manaFull, bool shieldFull,
        bool attack = false, bool melee = false) =>
        HitWithWeapon(now, amounts, lifeFull, manaFull, shieldFull, attack, melee, false, 0);

    internal (double Life, double Mana, double EnergyShield) HitWithWeapon(double now,
        (double Life, double Mana, double EnergyShield) amounts, bool lifeFull, bool manaFull, bool shieldFull,
        bool attack, bool melee, bool weaponLifeInstant, double weaponInstantPercent)
    {
        var before = Active(now);
        int count = Count;
        bool routeChanged = immortalAmbition && (fullLife != lifeFull || fullShield != shieldFull) && Life.Count > 0;
        var instant = HitCore(now, amounts, lifeFull, manaFull, shieldFull, attack, melee, weaponLifeInstant, weaponInstantPercent);
        Report(now, before, count, routeChanged);
        return instant;
    }

    (double Life, double Mana, double EnergyShield) HitCore(double now,
        (double Life, double Mana, double EnergyShield) amounts, bool lifeFull, bool manaFull, bool shieldFull,
        bool attack, bool melee, bool weaponLifeInstant, double weaponInstantPercent)
    {
        if (limits is null) return default;
        ClearFullCore(now, lifeFull, manaFull, shieldFull);
        double Grant(int index, double amount, bool full, bool keep, double instant, bool recover)
        {
            var resource = limits.Resources[index];
            if (resource.CannotLeech || full && !keep) return 0;
            double value = State(resource.Pool).Grant(now, amount, resource.MaximumPool, resource.MaximumPerInstance, instant, attack);
            return recover && (!full || index == 0 && lifeRecoveryRedirected) ? value : 0;
        }
        // Life and ES source categories create independent ES instances under Ghost Reaver, each with its own cap
        // and duration. They share the ES recovery rate/cap, retaining the Life source's instant fraction.
        bool keepShield = keepEnergyShield || attack && keepAttackEnergyShield;
        double LifeFraction(double baseline) => lifeInstantReplaced ? baseline
            : weaponLifeInstant ? 100 : baseline + weaponInstantPercent;
        double convertedShield = convertLifeToEnergyShield && !limits.Resources[0].CannotLeech
            ? Grant(2, amounts.Life, shieldFull, keepShield, LifeFraction(convertedLifeInstant), recoverEnergyShield) : 0;
        return (convertLifeToEnergyShield ? 0 : Grant(0, amounts.Life, lifeFull, keepLife || lifeRecoveryRedirected,
                LifeFraction(melee ? meleeLifeInstant : lifeInstant), recoverLife),
            Grant(1, amounts.Mana, manaFull, false, manaInstant + (manaInstantReplaced ? 0 : weaponInstantPercent), true),
            convertedShield + Grant(2, amounts.EnergyShield, shieldFull, keepShield,
                shieldInstant + (shieldInstantReplaced ? 0 : weaponInstantPercent), recoverEnergyShield));
    }

    public void ClearFull(double now, bool life, bool mana, bool shield)
    {
        var before = Active(now);
        int count = Count;
        bool routeChanged = immortalAmbition && (fullLife != life || fullShield != shield) && Life.Count > 0;
        ClearFullCore(now, life, mana, shield);
        Report(now, before, count, routeChanged);
    }

    void ClearFullCore(double now, bool life, bool mana, bool shield)
    {
        // Full Life, not Low Life: Petrified Blood ignores leech recovery above Low Life without removing the
        // instance, so it keeps its full-pool cap and can resume when Life drops.
        fullLife = life; fullShield = shield;
        if (life) { LifeInstantCarry = 0; pendingLifeRecovery = 0; }
        if (shield) pendingShieldRecovery = 0;
        if (mana) ManaInstantCarry = 0;
        if (life && !keepLife && !lifeRecoveryRedirected) Life.Clear(now);
        if (mana) Mana.Clear(now);
        if (shield && !keepEnergyShield)
        {
            if (keepAttackEnergyShield) EnergyShield.ClearNonAttack(now);
            else EnergyShield.Clear(now);
        }
    }

    double RedirectedRate(double at)
    {
        double freeRate = Math.Max(0, EnergyShield.MaximumPerSecond - EnergyShield.OfferedPerSecond(at));
        return Math.Min(freeRate, Life.OfferedPerSecond(at)) * EnergyShield.RecoveryMultiplier;
    }

    // Accrue to each expiry so the additional ES recovery shares the remaining native ES cap at every interval.
    // Instances retain the original Life grant's duration and base rate when the destination pool changes.
    void SettleRedirectedLife(double now)
    {
        if (!immortalAmbition || now <= routedAt) return;
        if (fullLife && !fullShield && !lifeRecoveryRedirected)
        {
            while (routedAt < now)
            {
                double end = Math.Min(now, Math.Min(Life.NextExpiry(routedAt) ?? now, EnergyShield.NextExpiry(routedAt) ?? now));
                pendingShieldRecovery += (end - routedAt) * RedirectedRate(routedAt);
                routedAt = end;
            }
        }
        double life = Life.TakeRecovery(now);
        if (!fullLife || lifeRecoveryRedirected) pendingLifeRecovery += life;
        routedAt = now;
    }

    public (double Life, double Mana, double EnergyShield) TakeRecovery(double now)
    {
        SettleRedirectedLife(now);
        var values = (Life.TakeRecovery(now) + pendingLifeRecovery, Mana.TakeRecovery(now),
            EnergyShield.TakeRecovery(now) + pendingShieldRecovery);
        pendingLifeRecovery = pendingShieldRecovery = 0;
        return values;
    }

    public (LeechRecoveryState.DisplayState Life, LeechRecoveryState.DisplayState Mana,
        LeechRecoveryState.DisplayState EnergyShield) Display(double now)
    {
        SettleRedirectedLife(now);
        var life = Life.Display(now);
        var shield = EnergyShield.Display(now);
        if (immortalAmbition && fullLife && !lifeRecoveryRedirected)
        {
            double bonus = fullShield ? 0 : RedirectedRate(now) * 60;
            if (bonus > 0)
                shield = new((int)Math.Min(int.MaxValue, Math.Round(shield.PerMinute + bonus)), Math.Max(shield.Expiry, life.Expiry));
            life = new(0, now);
        }
        return (lifeRecoveryRedirected ? new(0, now) : life, Mana.Display(now), shield);
    }

    // Reading conditions expires instances without consuming the recovery accumulated for the resource writer.
    public (bool Life, bool Mana, bool EnergyShield) Active(double now)
    {
        SettleRedirectedLife(now);
        bool life = Life.Active(now);
        return (life, Mana.Active(now), EnergyShield.Active(now)
            || immortalAmbition && fullLife && !fullShield && !lifeRecoveryRedirected && life);
    }

    public void Clear(double now)
    {
        var before = Active(now);
        int count = Count;
        Life.Clear(now); Mana.Clear(now); EnergyShield.Clear(now);
        Report(now, before, count);
        LifeInstantCarry = ManaInstantCarry = 0;
        pendingLifeRecovery = pendingShieldRecovery = 0;
        fullLife = fullShield = false;
        routedAt = now;
        InstantLifeChanged = InstantManaChanged = false;
    }

    LeechRecoveryState State(string pool) => pool == "life" ? Life : pool == "mana" ? Mana : EnergyShield;
}
