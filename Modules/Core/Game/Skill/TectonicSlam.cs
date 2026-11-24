public static partial class Game
{
    sealed class TectonicCadence
    {
        public int Slams;
    }

    static readonly RuleTable<GemSourceLifetimes.SourceLifetime, TectonicCadence> TectonicCadences = new();

    static (double HitFactor, double RadiusFactor) CommitTectonicSlamLocked(Player player, ushort skill, int effect,
        bool cataclysm, Func<uint, int> get, double now)
    {
        if (CaptureGemSourceLifetimeLocked(player, skill, effect) is not { } source
            || !player.GemSourceLifetimes.IsCurrent(source)) return (1, 1);

        const ChargeType endurance = ChargeType.Endurance;
        var charges = ChargesOf(player);
        var limits = charges.Limits(endurance);
        int held = charges.Converted(endurance) ? 0
            : now < charges.State.ExpiresAt(endurance) ? charges.Effective(endurance) : limits.Minimum;
        int removable = Math.Max(0, held - limits.Minimum);
        if (cataclysm)
        {
            int consumed = charges.Spend(endurance, removable, now);
            double hitFactor = Math.Max(0, 1 + get(Stat.EnduranceChargeSlamDamagePlusPctFinalPerEnduranceChargeConsumed)
                * (double)consumed / 100);
            int area = ActorScale.Saturate((long)get(Stat.TectonicSlamAreaOfEffectPlusPctFinalPerEnduranceChargeConsumed)
                * consumed);
            return (hitFactor, SkillArea.RadiusMultiplier(area));
        }

        var cadence = TectonicCadences.GetValue(source, _ => new());
        if (++cadence.Slams == 3)
        {
            cadence.Slams = 0;
            if (removable > 0) charges.Spend(endurance, 1, now);
        }
        int heldArea = ActorScale.Saturate((long)get(Stat.ActiveSkillAreaOfEffectPlusPctFinalPerEnduranceCharge) * held);
        return (1, SkillArea.RadiusMultiplier(heldArea));
    }
}
