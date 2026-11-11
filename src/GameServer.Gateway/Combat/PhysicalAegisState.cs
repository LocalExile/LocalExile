/// <summary>
/// The source-owned Physical Aegis resource. The equipped grant supplies its capacity and recharge delay; this state
/// owns the remaining physical-hit absorption and the native active/depleted presentation.
/// </summary>
sealed class PhysicalAegisState
{
    internal object? Owner;
    internal WornGrantedSkills.Grant? Source;
    internal int Maximum;
    internal int Current;
    internal int Variation;
    internal double RechargeDelay;
    internal double RestoresAt = double.PositiveInfinity;

    // The three native BuffDefinitions rows: active (960), depleted (965), and the invisible persistence row (970).
    internal ushort StageBuff;
    internal ushort PersistBuff;
    internal ushort StageRow;
    internal int PresentedCurrent = -1;

    internal readonly PassiveBuffAuras.BlindState Blind = new();

    internal sealed record Carried(WornGrantedSkills.Grant Source, int Maximum, int Current, int Variation,
        double RechargeDelay, double Remaining);

    internal bool Exists => Source is not null && Maximum > 0;
    internal bool Depleted => Exists && Current == 0;
    internal bool Active => Exists && Current > 0;

    internal void Install(object owner, WornGrantedSkills.Grant source, int maximum, double rechargeDelay, int variation)
    {
        Owner = owner;
        Source = source;
        Maximum = Math.Max(0, maximum);
        Current = Maximum;
        Variation = variation;
        RechargeDelay = Math.Max(0, rechargeDelay);
        RestoresAt = double.PositiveInfinity;
    }

    internal bool Advance(double now)
    {
        if (!Exists || RestoresAt == double.PositiveInfinity || now < RestoresAt) return false;
        Current = Maximum;
        RestoresAt = double.PositiveInfinity;
        return true;
    }

    internal int Absorb(int amount, double now)
    {
        Advance(now);
        if (!Active || amount <= 0) return Math.Max(0, amount);
        int absorbed = Math.Min(Current, amount);
        Current -= absorbed;
        // The extracted BuffDefinitions text says restoration follows a break in physical damage, not merely depletion.
        RestoresAt = now + RechargeDelay;
        return amount - absorbed;
    }

    internal Carried? Capture(double at) => Exists
        ? new(Source!.Value, Maximum, Current, Variation, RechargeDelay,
            double.IsPositiveInfinity(RestoresAt) ? double.PositiveInfinity : Math.Max(0, RestoresAt - at))
        : null;

    internal void Restore(Carried value, object owner, double at)
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
    internal ushort[] Retire()
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
