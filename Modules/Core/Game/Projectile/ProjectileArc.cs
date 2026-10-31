

/// <summary>Provisional presentation parabola. Heights are Positioned markers, increasing upward.
/// The runtime owns XY travel/contact; eight native linear segments approximate this visual-only curve.</summary>
readonly record struct ProjectileArc(double StartDistance, double Distance, int StartMarker, int EndMarker)
{
    public const int Segments = 8;
    public double Fraction(double distance) => Math.Clamp((distance - StartDistance) / Distance, 0, 1);
    public ProjectileHeight Height(double fraction) => new((int)Math.Round(
        StartMarker + (EndMarker - StartMarker) * fraction + 40 * fraction * (1 - fraction)));
}
