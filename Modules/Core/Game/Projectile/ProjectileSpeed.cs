using System.Globalization;


/// <summary>Ordinary additive projectile speed, resolved once when the projectile leaves the caster.</summary>
public static class ProjectileSpeed
{
    // Task.Delay(TimeSpan) accepts at most 0xfffffffe milliseconds on .NET 6 and later.
    const double MaximumFlightSeconds = (uint.MaxValue - 1L) / 1000.0;

    public static string ModifierStat(bool arrows) => arrows ? "arrow_speed_+%" : "projectile_speed_+%";

    // The character's projectile virtual already contains its raw base. An explicit arrow virtual
    // selects that view in full, including when it is zero.
    public static int CharacterIncrease(IReadOnlyDictionary<uint, int> player, bool arrows)
    {
        int Get(string stat) => player.GetValueOrDefault(Program.Stats.Key(stat)!.Value);
        if (arrows && player.TryGetValue(Program.Stats.Key(ModifierStat(true))!.Value, out int arrow)) return arrow;
        return Get(ModifierStat(false)) + (arrows ? Get("base_arrow_speed_+%") : 0);
    }

    // The 3.29.3c arrow/projectile virtual rounds the signed modifier before the returning action reads it.
    public static int EffectivePercent(int characterIncrease, Func<string, int> skillRaw, bool arrows = false)
    {
        int increased = characterIncrease + skillRaw("base_projectile_speed_+%")
            + (arrows ? skillRaw("base_arrow_speed_+%") : 0);
        float final = Math.Max(0, (100 + skillRaw("support_slower_projectiles_projectile_speed_+%_final")) / 100f);
        float modifier = (100 + increased) * final - 100f;
        return Math.Max(-99, (int)(modifier + (modifier < 0 ? -.5f : .5f)));
    }

    static float SpeedFactor(int percent) => 1f + percent * .01f;

    // The runtime retains the outbound speed of each flight (including its per-shot or skill-specific scaling).
    public static double ReturnMultiplier(int ordinary, int returned)
    {
        int outbound = Math.Max(-99, ordinary);
        return (double)SpeedFactor(Math.Max(-99, outbound + returned)) / SpeedFactor(outbound);
    }

    public static float CellsPerSecond(int worldUnitsPerSecond, Func<string, int> playerFinal,
        Func<string, int> skillRaw, string? testOverride = null) =>
        CellsPerSecond(worldUnitsPerSecond, EffectivePercent(playerFinal(ModifierStat(false)), skillRaw), testOverride);

    public static float CellsPerSecond(int worldUnitsPerSecond, int effectivePercent, string? testOverride = null)
    {
        if (float.TryParse(testOverride, NumberStyles.Float, CultureInfo.InvariantCulture, out float forced)
            && float.IsFinite(forced) && forced > 0)
            return forced;

        return Math.Max(0, worldUnitsPerSecond) * .092f * SpeedFactor(effectivePercent);
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
