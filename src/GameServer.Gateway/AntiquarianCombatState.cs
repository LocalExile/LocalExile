/// <summary>Connection-owned, pausable combat state. No wall-clock or character-name global lifetime.</summary>
sealed class AntiquarianCombatState
{
    EffectClock? adrenaline;
    double adrenalineRate = 1;
    public double AdrenalineUntil => adrenaline?.Deadline ?? double.NegativeInfinity;
    public bool Adrenaline(double now) => now < AdrenalineUntil;
    public double AdrenalineRemainingBase(double now) { adrenaline?.Advance(now); return adrenaline?.Remaining ?? 0; }
    double recoveryAt, recoveryUntil, recoveryPerSecond, pendingRecovery;
    public double RecoveryPerSecond(double now) => now < recoveryUntil ? recoveryPerSecond : 0;
    public double RecoveryUntil(double now) => RecoveryPerSecond(now) > 0 ? recoveryUntil : double.NegativeInfinity;

    public void Observe(double now, bool adrenalineSource, bool recoverySource, bool alive)
    {
        Integrate(now);
        if (!alive) { Clear(); return; }
        if (!adrenalineSource) adrenaline = null;
        if (!recoverySource) recoveryPerSecond = 0;
    }

    // Adrenaline cannot be gained while already active; repeated Ward breaks never extend it.
    public bool GrantAdrenaline(double now, int milliseconds)
    {
        if (milliseconds <= 0 || Adrenaline(now)) return false;
        adrenaline = new(milliseconds / 1000d, now, adrenalineRate);
        return true;
    }
    public void SetBuffExpiryRate(double now, double expiryRate)
    { adrenaline?.ChangeRate(now, expiryRate); adrenalineRate = Math.Max(0, expiryRate); }

    // LocalExile v1: another accepted stun refreshes this one-second recovery, without stacking rates.
    public void Stunned(double now, uint maximumLife, int percent)
    {
        Integrate(now);
        recoveryAt = now; recoveryUntil = now + 1;
        recoveryPerSecond = maximumLife * Math.Max(0d, percent) / 100;
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
        adrenaline = null; adrenalineRate = 1;
        recoveryAt = recoveryUntil = recoveryPerSecond = pendingRecovery = 0;
    }

    public readonly record struct Carried(double AdrenalineSeconds, double RecoverySeconds,
        double RecoveryPerSecond, double PendingRecovery, double AdrenalineRate = 1);
    public Carried Carry(double now)
    {
        Integrate(now);
        adrenaline?.Advance(now);
        return new(adrenaline?.Remaining ?? 0, Math.Max(0, recoveryUntil - now),
            recoveryPerSecond, pendingRecovery, adrenalineRate);
    }
    public void Restore(Carried state, double now)
    {
        Clear(); adrenalineRate = state.AdrenalineRate;
        adrenaline = new(state.AdrenalineSeconds, now, adrenalineRate);
        recoveryAt = now; recoveryUntil = now + state.RecoverySeconds;
        recoveryPerSecond = state.RecoveryPerSecond; pendingRecovery = state.PendingRecovery;
    }
}
