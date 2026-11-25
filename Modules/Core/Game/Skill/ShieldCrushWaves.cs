internal readonly record struct ShieldCrushWaves((int X, int Y) Origin, double FacingX, double FacingY, int SideRadius, int CentreRadius)
{
    public const ushort Action = 0x0ee3;
    const int BaseRadius = 29;
    const int InnerRadius = 8;
    const double SinThirty = 0.5;
    const double CosThirty = 0.8660254037844386;

    public static ShieldCrushWaves At((int X, int Y) from, (int X, int Y) aim, Func<uint, int> get, double radiusMore)
    {
        var (x, y) = GroundSlamWave.Direction(from, aim);
        var origin = (from.X - (int)Math.Round(x * InnerRadius), from.Y - (int)Math.Round(y * InnerRadius));
        double centreArea = 2 * Math.Max(0, 1 + get(Stat.ShieldCrushHelmetEnchantmentAoePlusPctFinal) / 100d);
        return new(origin, x, y,
            (int)Math.Round(SkillArea.Radius(BaseRadius, get, spell: false) * radiusMore),
            (int)Math.Round(SkillArea.Radius(BaseRadius, get, spell: false, areaMore: centreArea) * radiusMore));
    }

    public bool Covers(int wave, bool reversed, (int X, int Y) target, int body)
    {
        double dx = target.X - Origin.X, dy = target.Y - Origin.Y;
        double squared = dx * dx + dy * dy;
        double inner = Math.Max(0, InnerRadius - body);
        int radius = wave == 1 ? CentreRadius : SideRadius;
        double outer = radius + body;
        if (radius <= 0 || squared < inner * inner || squared > outer * outer) return false;

        double forward = dx * FacingX + dy * FacingY;
        double across = -dx * FacingY + dy * FacingX;
        if (wave != 1)
        {
            double sine = (wave == 0) != reversed ? SinThirty : -SinThirty;
            (forward, across) = (forward * CosThirty + across * sine, across * CosThirty - forward * sine);
        }
        return forward * SinThirty - Math.Abs(across) * CosThirty + body + .5 >= 0;
    }
}

internal sealed class ShieldCrushUse(ShieldCrushWaves waves, bool reversed, double start, double attackSeconds,
    (string Name, int LengthMs, IReadOnlyList<int> Times)? animation)
{
    internal ShieldCrushWaves Waves { get; } = waves;
    internal int Phase { get; private set; }
    internal string? Clip => animation?.Name;
    internal bool FortificationGranted;
    internal bool RageGranted;
    readonly HashSet<uint> _firstSideTargets = [];

    internal double Fraction(double fallback) => animation is { Times.Count: 3 } clip
        ? (double)clip.Times[Phase] / clip.LengthMs : fallback;

    internal double DueAt => start + attackSeconds * Fraction(0);

    internal bool TryAdvance()
    {
        if (Phase == 2) return false;
        Phase++;
        return true;
    }

    internal bool Includes(uint id, (int X, int Y) target, int body)
    {
        if (Phase == 2 && _firstSideTargets.Contains(id) || !Waves.Covers(Phase, reversed, target, body)) return false;
        if (Phase == 0) _firstSideTargets.Add(id);
        return true;
    }
}
