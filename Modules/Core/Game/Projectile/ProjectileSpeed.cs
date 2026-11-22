using System.Globalization;


/// <summary>Ordinary additive projectile speed, resolved once when the projectile leaves the caster.</summary>
public static class ProjectileSpeed
{
    // Task.Delay(TimeSpan) accepts at most 0xfffffffe milliseconds on .NET 6 and later.
    const double MaximumFlightSeconds = (uint.MaxValue - 1L) / 1000.0;

    public static string ModifierStat(bool arrows) => arrows ? "arrow_speed_+%" : "projectile_speed_+%";

    const string MoreMastery = "passive_mastery_more_projectile_speed_+%_final";
    const string LessMastery = "passive_mastery_less_projectile_speed_+%_final";
    internal const string BallLightningRate = "ball_lightning_projectile_speed_and_hit_frequency_+%_final";

    // The character's projectile virtual already contains its raw base. An explicit arrow virtual
    // selects that view in full, including when it is zero.
    public static int CharacterIncrease(IReadOnlyDictionary<uint, int> player, bool arrows)
    {
        int Get(string stat) => player.GetValueOrDefault(Program.Stats.Key(stat)!.Value);
        if (arrows && player.TryGetValue(Program.Stats.Key(ModifierStat(true))!.Value, out int arrow)) return arrow;
        return Get(ModifierStat(false)) + (arrows ? Get("base_arrow_speed_+%") : 0);
    }

    public static int EffectivePercent(IReadOnlyDictionary<uint, int> player, Func<string, int> skillRaw, bool arrows = false)
    {
        int Mastery(string stat) => player.GetValueOrDefault(Program.Stats.Key(stat)!.Value);
        return EffectivePercent(CharacterIncrease(player, arrows), skillRaw, arrows, Mastery(MoreMastery), Mastery(LessMastery));
    }

    public static int EffectivePercent(int characterIncrease, Func<string, int> skillRaw, bool arrows = false) =>
        EffectivePercent(characterIncrease, skillRaw, arrows, 0, 0);

    static int EffectivePercent(int characterIncrease, Func<string, int> skillRaw, bool arrows, int moreMastery, int lessMastery)
    {
        int increased = characterIncrease + skillRaw("base_projectile_speed_+%")
            + (arrows ? skillRaw("base_arrow_speed_+%") : 0);
        // The combined final modifier rounds before either the arrow or projectile virtual uses it.
        float slower = Factor(skillRaw("support_slower_projectiles_projectile_speed_+%_final"));
        float active = Factor(skillRaw("active_skill_projectile_speed_+%_final"));
        int final = RoundPercent(100f * slower * active * Factor(moreMastery) * Factor(lessMastery) - 100f);
        return Math.Max(-99, RoundPercent((100 + increased) * Factor(final) - 100f));
    }

    static float Factor(int percent) => Math.Max(0f, (100 + percent) / 100f);
    static int RoundPercent(float percent) => (int)(percent + (percent < 0 ? -.5f : .5f));

    static float SpeedFactor(int percent) => 1f + percent * .01f;

    // The runtime retains the outbound speed of each flight (including its per-shot or skill-specific scaling).
    public static double ReturnMultiplier(int ordinary, int returned)
    {
        int outbound = Math.Max(-99, ordinary);
        return (double)SpeedFactor(Math.Max(-99, outbound + returned)) / SpeedFactor(outbound);
    }

    public static float CellsPerSecond(int worldUnitsPerSecond, Func<string, int> playerFinal,
        Func<string, int> skillRaw, string? testOverride = null) =>
        CellsPerSecond(worldUnitsPerSecond, EffectivePercent(playerFinal(ModifierStat(false)), skillRaw, false,
            playerFinal(MoreMastery), playerFinal(LessMastery)), testOverride);

    public static float CellsPerSecond(int worldUnitsPerSecond, int effectivePercent, string? testOverride = null) =>
        ApplyOverride(BaseSpeed(worldUnitsPerSecond, effectivePercent), testOverride);

    public static float SkillCellsPerSecond(string? effect, int worldUnitsPerSecond, int effectivePercent,
        Func<string, int> get, string? testOverride = null, float motionVariation = 1)
    {
        float speed = BaseSpeed(worldUnitsPerSecond, effectivePercent) * motionVariation;
        if (GatewayProjectileSkillProfiles.IsBallLightningOrb(effect))
        {
            float rate = Factor(get(BallLightningRate));
            speed *= rate;
            // Orbiting stores the scaled speed at launch and passes it through the same callback on each leg.
            if (effect == "BallLightningAltX") speed *= rate;
        }
        return ApplyOverride(speed, testOverride);
    }

    static float BaseSpeed(int worldUnitsPerSecond, int effectivePercent) =>
        Math.Max(0, worldUnitsPerSecond) * .092f * SpeedFactor(effectivePercent);

    static float ApplyOverride(float speed, string? testOverride) =>
        float.TryParse(testOverride, NumberStyles.Float, CultureInfo.InvariantCulture, out float forced)
        && float.IsFinite(forced) && forced > 0 ? forced : speed;

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
