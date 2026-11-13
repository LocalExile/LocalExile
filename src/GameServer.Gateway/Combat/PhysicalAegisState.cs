/// <summary>
/// The source-owned Physical Aegis resource. The equipped grant supplies its capacity and recharge delay; this state
/// owns the remaining physical-hit absorption and the native active/depleted presentation.
/// </summary>
public sealed class PhysicalAegisState
{
    public object? Owner;
    public WornGrantedSkills.Grant? Source;
    public int Maximum;
    public int Current;
    public int Variation;
    public double RechargeDelay;
    public double RestoresAt = double.PositiveInfinity;

    // The three native BuffDefinitions rows: active (960), depleted (965), and the invisible persistence row (970).
    public ushort StageBuff;
    public ushort PersistBuff;
    public ushort StageRow;
    public int PresentedCurrent = -1;

    public readonly PassiveBuffAuras.BlindState Blind = new();

    public sealed record Carried(WornGrantedSkills.Grant Source, int Maximum, int Current, int Variation,
        double RechargeDelay, double Remaining);

    public bool Exists => Source is not null && Maximum > 0;
    public bool Depleted => Exists && Current == 0;
    public bool Active => Exists && Current > 0;

    public void Install(object owner, WornGrantedSkills.Grant source, int maximum, double rechargeDelay, int variation)
    {
        Owner = owner;
        Source = source;
        Maximum = Math.Max(0, maximum);
        Current = Maximum;
        Variation = variation;
        RechargeDelay = Math.Max(0, rechargeDelay);
        RestoresAt = double.PositiveInfinity;
    }

    public bool Advance(double now)
    {
        if (!Exists || RestoresAt == double.PositiveInfinity || now < RestoresAt) return false;
        Current = Maximum;
        RestoresAt = double.PositiveInfinity;
        return true;
    }

    public int Absorb(int amount, double now)
    {
        Advance(now);
        if (!Active || amount <= 0) return Math.Max(0, amount);
        int absorbed = Math.Min(Current, amount);
        Current -= absorbed;
        // The extracted BuffDefinitions text says restoration follows a break in physical damage, not merely depletion.
        RestoresAt = now + RechargeDelay;
        return amount - absorbed;
    }

    public Carried? Capture(double at) => Exists
        ? new(Source!.Value, Maximum, Current, Variation, RechargeDelay,
            double.IsPositiveInfinity(RestoresAt) ? double.PositiveInfinity : Math.Max(0, RestoresAt - at))
        : null;

    public void Restore(Carried value, object owner, double at)
    {
        Install(owner, value.Source, value.Maximum, value.RechargeDelay, value.Variation);
        Current = Math.Clamp(value.Current, 0, Maximum);
        RestoresAt = double.IsPositiveInfinity(value.Remaining)
            ? double.PositiveInfinity : at + Math.Max(0, value.Remaining);
    }

    /// <summary>
    /// Drops the source identity and returns the buff instances the client must remove. The blind instances remain until
    /// the enemy-debuff reconciliation has removed them, so a depleted/removed Aegis cannot leave a stale blindness.
    /// </summary>
    public ushort[] Retire()
    {
        ushort[] buffs = [StageBuff, PersistBuff];
        Owner = null;
        Source = null;
        Maximum = Current = Variation = 0;
        RechargeDelay = 0;
        RestoresAt = double.PositiveInfinity;
        StageBuff = PersistBuff = 0;
        StageRow = 0;
        PresentedCurrent = -1;
        return [.. buffs.Where(buff => buff != 0).Distinct()];
    }
}
