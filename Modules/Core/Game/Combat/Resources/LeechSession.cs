

/// <summary>Owner-local leech state. All callers hold the owner's combat lock and use its pausable game clock.</summary>
sealed class LeechSession
{
    public LeechRecoveryState Life { get; } = new();
    public LeechRecoveryState Mana { get; } = new();
    public LeechRecoveryState EnergyShield { get; } = new();
    LeechLimits.Snapshot? limits;
    bool keepLife, keepEnergyShield, keepAttackEnergyShield;
    bool convertLifeToEnergyShield;
    public bool ConvertsLifeToEnergyShield => convertLifeToEnergyShield;
    double lifeInstant, manaInstant, shieldInstant, convertedLifeInstant;
    bool recoverLife = true, recoverEnergyShield = true;
    public bool RecoversLife => recoverLife;
    public bool RecoversEnergyShield => recoverEnergyShield;
    public bool InstantLifeChanged, InstantManaChanged;
    public double LifeInstantCarry, ManaInstantCarry;
    // What a kill recovers (RecoverOnKill), from the same stat snapshot as this configuration.
    public KillRecovery OnKill { get; set; }

    public void Configure(double now, LeechLimits.Snapshot value, Func<string, int> get, Func<string, int?> replacement,
        int equippedClaws = 0)
    {
        limits = value;
        convertLifeToEnergyShield = replacement("virtual_leech_energy_shield_instead_of_life") is int converted
            ? converted != 0 : get("keystone_ghost_reaver") != 0 || get("leech_energy_shield_instead_of_life") != 0;
        keepLife = replacement("virtual_life_leech_does_not_stop_at_full_life") is int lifeOverleech
            ? lifeOverleech != 0 : get("life_leech_does_not_stop_at_full_life") != 0
                || get("virtual_life_leech_does_not_stop_at_full_life") != 0;
        keepEnergyShield = get("energy_shield_leech_does_not_stop_on_full_energy_shield") != 0;
        keepAttackEnergyShield = get("energy_shield_leech_from_attacks_does_not_stop_on_full_energy_shield") != 0;
        recoverLife = get("cannot_recover_life") == 0;
        recoverEnergyShield = get("cannot_recover_energy_shield") == 0;
        equippedClaws = Math.Clamp(equippedClaws, 0, 2);
        double commonInstant = (double)get("leech_%_is_instant")
            + (equippedClaws > 0 ? get("leech_%_is_instant_while_wielding_claw") : 0);
        lifeInstant = Math.Clamp(replacement("virtual_life_leech_%_is_instant")
            ?? commonInstant + get("life_leech_%_is_instant")
                + (double)equippedClaws * get("life_leech_%_is_instant_per_equipped_claw"), 0, 100);
        manaInstant = Math.Clamp(replacement("virtual_mana_leech_%_is_instant") ?? commonInstant, 0, 100);
        shieldInstant = Math.Clamp(replacement("virtual_energy_shield_leech_%_is_instant") ?? commonInstant, 0, 100);
        // GGG 3.22.1d explicitly preserves Life-instant sources through Ghost Reaver. Keep that source scope:
        // native ES grants do not inherit a Life-only bonus. An explicit final ES replacement still wins.
        convertedLifeInstant = Math.Clamp(replacement("virtual_energy_shield_leech_%_is_instant") ?? lifeInstant, 0, 100);
        foreach (var resource in value.Resources)
        {
            var state = State(resource.Pool);
            double speed = replacement($"virtual_{resource.Pool}_leech_speed_+%")
                ?? (double)get($"{resource.Pool}_leech_speed_+%") + get("life_mana_es_leech_speed_+%");
            double total = Math.Max(0, 1 + speed / 100);
            // Both raw flags describe the same boolean doubling; two copies do not quadruple recovery.
            if (resource.Pool == "life" && (get("life_leech_speed_is_doubled") != 0
                || get("total_recovery_per_minute_from_life_leech_is_doubled") != 0)) total *= 2;
            double recovery = Math.Max(0, 1 + get($"{resource.Pool}_recovery_rate_+%") / 100.0);
            if (resource.Pool == "life" && !recoverLife || resource.Pool == "energy_shield" && !recoverEnergyShield) recovery = 0;
            state.Configure(now, new(total, resource.MaximumPerSecond, recovery));
            if (resource.CannotLeech) state.Clear(now);
        }
    }

    public (double Life, double Mana, double EnergyShield) Hit(double now,
        (double Life, double Mana, double EnergyShield) amounts, bool lifeFull, bool manaFull, bool shieldFull,
        bool attack = false)
    {
        if (limits is null) return default;
        ClearFull(now, lifeFull, manaFull, shieldFull);
        double Grant(int index, double amount, bool full, bool keep, double instant, bool recover)
        {
            var resource = limits.Resources[index];
            if (resource.CannotLeech || full && !keep) return 0;
            double value = State(resource.Pool).Grant(now, amount, resource.MaximumPool, resource.MaximumPerInstance, instant, attack);
            return recover && !full ? value : 0;
        }
        // Life and ES source categories create independent ES instances under Ghost Reaver, each with its own cap
        // and duration. They share the ES recovery rate/cap, retaining the Life source's instant fraction.
        bool keepShield = keepEnergyShield || attack && keepAttackEnergyShield;
        double convertedShield = convertLifeToEnergyShield && !limits.Resources[0].CannotLeech
            ? Grant(2, amounts.Life, shieldFull, keepShield, convertedLifeInstant, recoverEnergyShield) : 0;
        return (convertLifeToEnergyShield ? 0 : Grant(0, amounts.Life, lifeFull, keepLife, lifeInstant, recoverLife),
            Grant(1, amounts.Mana, manaFull, false, manaInstant, true),
            convertedShield + Grant(2, amounts.EnergyShield, shieldFull, keepShield, shieldInstant, recoverEnergyShield));
    }

    public void ClearFull(double now, bool life, bool mana, bool shield)
    {
        if (life) LifeInstantCarry = 0;
        if (mana) ManaInstantCarry = 0;
        if (life && !keepLife) Life.Clear(now);
        if (mana) Mana.Clear(now);
        if (shield && !keepEnergyShield)
        {
            if (keepAttackEnergyShield) EnergyShield.ClearNonAttack(now);
            else EnergyShield.Clear(now);
        }
    }

    public (double Life, double Mana, double EnergyShield) TakeRecovery(double now) =>
        (Life.TakeRecovery(now), Mana.TakeRecovery(now), EnergyShield.TakeRecovery(now));

    // Reading conditions expires instances without consuming the recovery accumulated for the resource writer.
    public (bool Life, bool Mana, bool EnergyShield) Active(double now) =>
        (Life.Active(now), Mana.Active(now), EnergyShield.Active(now));

    public void Clear(double now)
    {
        Life.Clear(now); Mana.Clear(now); EnergyShield.Clear(now);
        LifeInstantCarry = ManaInstantCarry = 0;
        InstantLifeChanged = InstantManaChanged = false;
    }

    LeechRecoveryState State(string pool) => pool == "life" ? Life : pool == "mana" ? Mana : EnergyShield;
}
