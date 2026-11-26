public static partial class Game
{
    enum CascadeBurst { None, Early, Final }

    sealed class CascadeKnockback
    {
        public double ReadyAt;
    }

    static readonly RuleTable<SpawnedMonster, CascadeKnockback> CascadeKnockbacks = new();

    static bool GlacialCascadeEffect(string? effect) => effect is "GlacialCascade" or "GlacialCascadeAltX" or "GlacialCascadeRoyale";
    static bool GlacialCascadeHasFinalBurst(string? effect) => effect is "GlacialCascade" or "GlacialCascadeRoyale";
}
