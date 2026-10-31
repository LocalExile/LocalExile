

/// <summary>Resolved returning-damage multipliers shared by projectile sources.</summary>
static class ProjectileReturn
{
    /// <summary>The "while Returning" more multiplier: the support's less Damage and the game's returning-projectile
    /// final multipliers, combined multiplicatively (all are percentages, e.g. -70 = ×0.3).</summary>
    public static double DamageMore(Func<string, int> stat)
    {
        double more = 1;
        foreach (string name in new[]
                 {
                     "support_return_damage_+%_final_from_returning_projectile",
                     "support_return_returning_projectiles_damage_+%_final",
                     "active_skill_returning_projectile_damage_+%_final",
                     "damage_+%_final_from_returning_projectile",
                 })
            more *= Math.Max(0, 1 + stat(name) / 100.0);
        return more;
    }
}
