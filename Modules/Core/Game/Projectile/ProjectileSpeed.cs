using System.Globalization;


/// <summary>Ordinary additive projectile speed, resolved once when the projectile leaves the caster.</summary>
static class ProjectileSpeed
{
    // Task.Delay(TimeSpan) accepts at most 0xfffffffe milliseconds on .NET 6 and later.
    const double MaximumFlightSeconds = (uint.MaxValue - 1L) / 1000.0;

    /// <summary>
    /// The player's virtual projectile_speed_+% already includes base_projectile_speed_+% (PanelStats).
    /// Add only the skill's raw base input, separately: overlaying its dictionary would lose player bonuses.
    /// More/less and conditional modifiers are outside this calculation. A finite positive override is in cells/s.
    /// </summary>
    public static float CellsPerSecond(int worldUnitsPerSecond, Func<string, int> playerFinal,
        Func<string, int> skillRaw, string? testOverride = null)
    {
        if (float.TryParse(testOverride, NumberStyles.Float, CultureInfo.InvariantCulture, out float forced)
            && float.IsFinite(forced) && forced > 0)
            return forced;

        long increased = (long)playerFinal("projectile_speed_+%") + skillRaw("base_projectile_speed_+%");
        double speed = Math.Max(0, worldUnitsPerSecond) / (250.0 / 23.0) * Math.Max(0, 1 + increased / 100.0);
        return (float)(speed * Math.Max(0, 1 + skillRaw("support_slower_projectiles_projectile_speed_+%_final") / 100.0));
    }

    /// <summary>Keep the longest possible flight within Task.Delay's numeric limit before predicting collisions.
    /// Ordinary positive overrides remain exact; only a zero/unsafe delay needs a distance-dependent speed floor.</summary>
    public static float ForFlightDistance(float cellsPerSecond, double maximumFlightCells)
    {
        double distance = Math.Max(1, maximumFlightCells);
        float minimum = (float)(distance / MaximumFlightSeconds);
        // A downward float rounding at the boundary would still make Task.Delay throw.
        if (distance / minimum > MaximumFlightSeconds) minimum = MathF.BitIncrement(minimum);
        return Math.Max(cellsPerSecond, minimum);
    }
}
