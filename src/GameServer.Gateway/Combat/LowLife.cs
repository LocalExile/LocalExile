/// <summary>
/// The game's Low Life boundary: 50% of maximum Life or less by default (the 3.29.3b
/// <c>ReminderTextLowLife</c> text), replaced by a positive <c>low_life_threshold_%_override</c>, then by
/// <c>low_life_threshold_%</c>. The critical policy and Petrified Blood share this rule.
/// </summary>
public static class LowLife
{
    public const int DefaultThresholdPct = 50;

    public static int Threshold(int thresholdOverride = 0, int thresholdPercent = 0) =>
        Math.Clamp(thresholdOverride > 0 ? thresholdOverride
            : thresholdPercent > 0 ? thresholdPercent : DefaultThresholdPct, 0, 100);

    public static bool Is(uint currentLife, uint maximumLife, int thresholdPct, bool countsAsLowLife = false)
    {
        if (currentLife == 0 || maximumLife == 0) return false;
        if (countsAsLowLife) return true;
        return (long)currentLife * 100 <= (long)maximumLife * Math.Clamp(thresholdPct, 0, 100);
    }
}
