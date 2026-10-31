

/// <summary>Serialized by the owner's combat lock. All deadlines and recovery use that owner's game clock.</summary>
sealed class FortificationSession
{
    public const string OnMeleeHit = "fortify_on_hit";
    public const string ChanceOnMeleeHit = "chance_to_fortify_on_melee_hit_+%";
    public const string CannotGain = "cannot_gain_fortification_from_hits";
    public const string Current = "current_fortification", Uncapped = "uncapped_current_fortification";
    public const string Maximum = "virtual_max_fortification";
    public const string Regeneration = "life_regeneration_rate_per_minute_%";
    public const string AttackSpeed = "attack_speed_+%";
    public readonly FortificationState State = new();
    public FortificationState.Value Value { get; private set; } = new(0, 0, 20, null);
    public int AddedMaximum { get; private set; }
    public int RegenPerStack { get; private set; }
    public int SpeedPerStack { get; private set; }
    public long Revision { get; private set; }
    public long PresentedRevision = -1;
    public ushort Buff;
    double accountedStackSeconds, regenerationPerStack, recovery;
    double? snapshotTime;

    public IDisposable Capture(double now)
    {
        double? previous = snapshotTime;
        Read(now);
        snapshotTime ??= now;
        return new Snapshot(() => snapshotTime = previous);
    }

    sealed class Snapshot(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    public FortificationState.Value Read(double now)
    {
        now = snapshotTime ?? now;
        var before = Value;
        Value = State.Read(now, AddedMaximum);
        recovery += (State.StackSeconds - accountedStackSeconds) * regenerationPerStack;
        accountedStackSeconds = State.StackSeconds;
        if (before != Value) Revision++;
        return Value;
    }

    public FortificationState.Value Observe(double now, Func<string, int> get)
    {
        Read(now); // Settle the previous cap and recovery inputs before adopting new sources.
        int speed = get(FortificationState.AttackSpeedPerStack), regen = get(FortificationState.LifeRegenPerStack);
        int added = get(FortificationState.MaximumStat);
        if (speed != SpeedPerStack || regen != RegenPerStack || added != AddedMaximum) Revision++;
        (SpeedPerStack, RegenPerStack, AddedMaximum) = (speed, regen, added);
        return Read(now);
    }

    public void ConfigureRecovery(double now, uint maximumLife, bool finalRegenerationOverride,
        double regenerationMultiplier = 1)
    {
        Read(now);
        regenerationPerStack = finalRegenerationOverride ? 0
            : maximumLife * (double)RegenPerStack / 6000 * regenerationMultiplier;
    }

    public double TakeRecovery(double now)
    {
        Read(now);
        double result = recovery;
        recovery = 0;
        return result;
    }

    public bool Hit(double now, decimal rawDamage, decimal maximumLife,
        GameServer.Core.Protocol.Instance.MonsterRarity rarity, Func<string, int> get, Random random)
    {
        Observe(now, get);
        if (get(CannotGain) != 0) return false;
        bool always = get(OnMeleeHit) != 0;
        int chance = Math.Clamp(get(ChanceOnMeleeHit), 0, 100);
        if (!always && (chance == 0 || random.Next(100) >= chance)) return false;
        int count = FortificationGainModel.Calculate(rawDamage, maximumLife, rarity, Value.Maximum,
            FortificationGainModel.Modifiers.From(get));
        if (!State.TryGrantFromHit(now, count, get(FortificationState.DurationStat))) return false;
        Revision++;
        Read(now);
        return true;
    }

    public void Clear()
    {
        bool changed = Value.Uncapped != 0;
        State.Clear();
        Value = new(0, 0, FortificationState.Maximum(AddedMaximum), null);
        accountedStackSeconds = regenerationPerStack = recovery = 0;
        if (changed) Revision++;
    }

    public void Restore(FortificationState.Transfer transfer, double now)
    {
        Clear();
        State.Restore(transfer, now);
        Read(now);
    }
}
