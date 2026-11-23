

/// <summary>Resolved returning-damage multipliers shared by projectile sources.</summary>
public static class ProjectileReturn
{
    /// <summary>The "while Returning" more multiplier: the support's less Damage and the game's returning-projectile
    /// final multipliers, combined multiplicatively (all are percentages, e.g. -70 = ×0.3).</summary>
    public static double DamageMore(Func<uint, int> stat)
    {
        double more = 1;
        foreach (uint name in new[]
                 {
                     Stat.SupportReturnDamagePlusPctFinalFromReturningProjectile,
                     Stat.SupportReturnReturningProjectilesDamagePlusPctFinal,
                     Stat.ActiveSkillReturningProjectileDamagePlusPctFinal,
                     Stat.DamagePlusPctFinalFromReturningProjectile,
                 })
            more *= Math.Max(0, 1 + stat(name) / 100.0);
        return more;
    }
}
