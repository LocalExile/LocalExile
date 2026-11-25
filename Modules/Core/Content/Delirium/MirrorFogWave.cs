using GameServer.Core;

namespace GameServer.Modules.Delirium;

/// <summary>Normal map Mirror wave radii in terrain cells, from AfflictionBalancePerLevel and the area's modifiers.</summary>
public readonly record struct MirrorFogWave(float InitialSpeed, float TravelSpeed, float RampSeconds,
    float TrailingDelay, float TrailingSpeed, float TrailingAcceleration, bool NeverDissipates)
{
    public static MirrorFogWave For(int level, IReadOnlyDictionary<uint, int> stats)
    {
        var row = ClientTables.Table("AfflictionBalancePerLevel").Last(r => r.Int("__col0") <= Math.Max(1, level));
        float F(string key) => (float)row.Real(key);
        return new(F("__col14"), F("__col1"), F("__col13"),
            F("__col3") + stats.GetValueOrDefault(Stat.MapAfflictionSecondaryWaveDelayMsPlus) / 1000f, F("__col2"),
            F("__col4") * (1 + stats.GetValueOrDefault(Stat.MapAfflictionSecondaryWaveAccelerationPlusPct) / 100f),
            stats.GetValueOrDefault(Stat.MapDeliriumFogNeverDissipates) > 0);
    }

    public float OuterRadius(float elapsed)
    {
        if (elapsed <= 0) return 0;
        if (RampSeconds <= 0) return TravelSpeed * elapsed;
        float ramp = Math.Min(elapsed, RampSeconds);
        return InitialSpeed * ramp + (TravelSpeed - InitialSpeed) * ramp * ramp / (2 * RampSeconds)
            + TravelSpeed * (elapsed - ramp);
    }

    public float InnerRadius(float elapsed)
    {
        if (NeverDissipates || elapsed <= TrailingDelay) return 0;
        float t = elapsed - TrailingDelay;
        return Math.Clamp(TrailingSpeed * t + TrailingAcceleration * t * t / 2, 0, OuterRadius(elapsed));
    }
}
