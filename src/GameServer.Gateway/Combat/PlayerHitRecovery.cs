/// <summary>Ordinary physical monster melee stun eligibility and bounded native TakeHit recovery.
/// PoB CalcDefence: physical hits count 25% extra, chance = 200 * effective damage / threshold, only above 20%.
/// Monster.ot adds 20% more melee stun duration to the 350 ms base; recovery rounds up to a 33 ms tick.
/// Special threshold substitutions, conditional immunity and monster-specific modifiers are outside this slice.</summary>
public sealed class PlayerHitRecovery
{
    public const byte TakeHitAnimation = 5;
    public double Until { get; private set; } = double.NegativeInfinity;
    /// <summary>Accepted recovery permanently invalidates older paid windups, independently of Move and expiry.</summary>
    public int Generation { get; private set; }
    public bool Active(double now) => now < Until;
    public bool BlocksInput(double requestedAt, double now) => Active(now) || requestedAt < Until;
    public void Clear() => Until = double.NegativeInfinity;

    public bool Begin(double now, float seconds)
    {
        if (Active(now) || !float.IsFinite(seconds) || seconds <= 0) return false;
        Until = now + seconds;
        Generation++;
        return true;
    }

    public static double Chance(int damage, uint maximumLife, Func<uint, int> get)
    {
        if (damage <= 0 || maximumLife == 0 || get(Stat.CannotBeStunned) != 0
            || get(Stat.CannotBeStunnedByAttacks) != 0 || get(Stat.CannotBeStunnedByHitsOfOnlyPhysicalDamage) != 0
            // Unwavering Stance: "Cannot be Stunned".
            || get(Stat.KeystoneUnwaveringStance) != 0
            || EnergyShieldPresent(get) || get(Stat.MaximumLifeIsOne) != 0
            || get(Stat.AvoidInterruptionPct) != 0) return 0;
        // These need their own threshold calculation. Do not silently use life for a substituted threshold.
        if (get(Stat.StunThresholdBasedOnPctManaInsteadOfLife) != 0
            || get(Stat.StunThresholdBasedOnPctEnergyShieldInsteadOfLife) != 0
            || get(Stat.StunThresholdBasedOnEnergyShieldInsteadOfLife) != 0
            || get(Stat.OccultistStunThresholdIsBasedOnEnergyShieldInsteadOfLife) != 0
            || get(Stat.AdditionalStunThresholdBasedOnPctOfEnergyShield) != 0) return 0;
        double threshold = maximumLife * (1 + get(Stat.StunThresholdPlusPct) / 100.0);
        if (threshold <= 0) return 0;
        double chance = Math.Min(100, 200 * (damage * 1.25) / threshold);
        return chance <= 20 ? 0 : chance * (100 - Math.Clamp(get(Stat.AvoidStunPct), 0, 100)) / 100;
    }

    public static bool EnergyShieldPresent(Func<uint, int> get, int equippedBase = 0) =>
        equippedBase > 0 || get(Stat.MaximumEnergyShield) > 0 || get(Stat.BaseMaximumEnergyShield) > 0;

    // cannot_be_stunned_while_leeching (Endless Hunger) is supported: the stats fold it into cannot_be_stunned while
    // is_leeching, which Chance reads.
    public static bool UnsupportedStat(uint id) => UnsupportedStat(Program.Stats.Name(id), id);

    static bool UnsupportedStat(string name, uint id) =>
        name.StartsWith("cannot_be_stunned_", StringComparison.Ordinal)
            && id is not (Stat.CannotBeStunnedByAttacks or Stat.CannotBeStunnedByHitsOfOnlyPhysicalDamage
                or Stat.CannotBeStunnedBySpells or Stat.CannotBeStunnedBySuppressedSpellDamage or Stat.CannotBeStunnedByBlockedHits
                or Stat.CannotBeStunnedWhileLeeching)
        || name.StartsWith("base_cannot_be_stunned_", StringComparison.Ordinal)
        || name.Contains("avoid_stun", StringComparison.Ordinal) && id is not (Stat.AvoidStunPct or Stat.BaseAvoidStunPct)
        || name.Contains("stun_avoid", StringComparison.Ordinal)
        // The per-charge threshold and recovery stats ChargeEffects expands (Brutal's threshold, Frenzy's recovery) are
        // already in stun_threshold_+% / stun_recovery_+% with the charges held.
        || name.StartsWith("stun_threshold_+%_", StringComparison.Ordinal) && !ChargeEffects.Expands(id)
        || name.StartsWith("stun_recovery_+%_", StringComparison.Ordinal) && !ChargeEffects.Expands(id)
        || name.StartsWith("skill_cannot_be_stunned", StringComparison.Ordinal)
        || id is Stat.StunThresholdPlus or Stat.StunThresholdIncreasedByOvercappedFireResistance
            or Stat.LocalFlaskStunRecoveryPlusPctWhileHealing or Stat.BlockAndStunPlusPctRecoveryPerFortification;

    public static float Duration(Func<uint, int> get, double actionSpeed = 1)
    {
        double recovery = 1 + get(Stat.StunRecoveryPlusPct) / 100.0;
        double duration = 1 + get(Stat.StunDurationOnSelfPlusPct) / 100.0;
        if (recovery <= 0 || duration <= 0 || !double.IsFinite(actionSpeed) || actionSpeed <= 0) return 0;
        // Action speed also scales the recovery animation; stun_recovery_+% is the separate, additive stun stat.
        double seconds = Math.Ceiling(.35 * 1.2 * duration / recovery / actionSpeed / .033) * .033;
        // Do not let malformed developer stats create an unrepresentable native time or an unbounded input lockout.
        return seconds is > 0 and <= 60 ? (float)seconds : 0;
    }
}
