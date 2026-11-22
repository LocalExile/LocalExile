/// <summary>Connection-owned, pausable combat state. No wall-clock or character-name global lifetime.</summary>
public sealed class AntiquarianCombatState
{
    public double AdrenalineUntil { get; private set; } = double.NegativeInfinity;
    public bool Adrenaline(double now) => now < AdrenalineUntil;
    double recoveryAt, recoveryUntil, recoveryPerSecond, pendingRecovery;
    public double RecoveryPerSecond(double now) => now < recoveryUntil ? recoveryPerSecond : 0;
    public double RecoveryUntil(double now) => RecoveryPerSecond(now) > 0 ? recoveryUntil : double.NegativeInfinity;

    /// <summary>Adrenaline or the stun recovery started or was taken away (the stat steps that read it run again).</summary>
    public Action? Changed;

    public void Observe(double now, bool adrenalineSource, bool recoverySource, bool alive)
    {
        Integrate(now);
        if (!alive) { Clear(); return; }
        bool changed = !adrenalineSource && Adrenaline(now) || !recoverySource && RecoveryPerSecond(now) > 0;
        if (!adrenalineSource) AdrenalineUntil = double.NegativeInfinity;
        if (!recoverySource) recoveryPerSecond = 0;
        if (changed) Changed?.Invoke();
    }

    // Adrenaline cannot be gained while already active; repeated Ward breaks never extend it.
    public bool GrantAdrenaline(double now, int milliseconds)
    {
        if (milliseconds <= 0 || Adrenaline(now)) return false;
        AdrenalineUntil = now + milliseconds / 1000d;
        Changed?.Invoke();
        return true;
    }

    // LocalExile v1: another accepted stun refreshes this one-second recovery, without stacking rates.
    public void Stunned(double now, uint maximumLife, int percent)
    {
        Integrate(now);
        recoveryAt = now; recoveryUntil = now + 1;
        recoveryPerSecond = maximumLife * Math.Max(0d, percent) / 100;
        Changed?.Invoke();
    }

    void Integrate(double now)
    {
        pendingRecovery += Math.Max(0, Math.Min(now, recoveryUntil) - recoveryAt) * recoveryPerSecond;
        recoveryAt = Math.Max(recoveryAt, now);
    }

    public double TakeRecovery(double now)
    {
        Integrate(now);
        double result = pendingRecovery; pendingRecovery = 0;
        return result;
    }

    public void Clear()
    {
        bool had = double.IsFinite(AdrenalineUntil) || recoveryPerSecond != 0;
        AdrenalineUntil = double.NegativeInfinity;
        recoveryAt = recoveryUntil = recoveryPerSecond = pendingRecovery = 0;
        if (had) Changed?.Invoke();
    }

    public readonly record struct Carried(double AdrenalineSeconds, double RecoverySeconds,
        double RecoveryPerSecond, double PendingRecovery);
    public Carried Carry(double now)
    {
        Integrate(now);
        return new(Math.Max(0, AdrenalineUntil - now), Math.Max(0, recoveryUntil - now),
            recoveryPerSecond, pendingRecovery);
    }
    public void Restore(Carried state, double now)
    {
        Clear(); AdrenalineUntil = now + state.AdrenalineSeconds;
        recoveryAt = now; recoveryUntil = now + state.RecoverySeconds;
        recoveryPerSecond = state.RecoveryPerSecond; pendingRecovery = state.PendingRecovery;
        Changed?.Invoke();
    }
}
