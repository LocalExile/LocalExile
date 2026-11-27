using GameServer.Core.Projectiles;

public static partial class Game
{
    const string SpectralHelixEffect = "SpectralHelix", SpectralHelixTrapEffect = "SpectralHelixAltY";

    internal static bool IsSpectralHelix(string? effect) => effect is SpectralHelixEffect or SpectralHelixTrapEffect;

    internal static ProjectileProfile SpectralHelixProfile(ProjectileProfile profile, Func<uint, int> get)
    {
        int bounces = Math.Max(0, get(Stat.SpectralSpiralWeaponBaseNumberOfBounces) + profile.Count - 1);
        return profile with
        {
            Count = Math.Min(1, profile.Count), ParallelOrigins = null, NativeVolley = null,
            TerrainBounces = bounces,
            Pierce = -1,
            HitRule = new(ProjectileHitScope.Projectile, ProjectileRepeat.Window, ProjectileRuntime.Time(.3)),
        };
    }

    static ProjectileUse ReleaseSpectralHelix(ProjectileReleaseContext context, ProjectileUse use)
    {
        int bounces = use.Profile.TerrainBounces;
        int rotations = context.Own(Stat.SpectralHelixRotationsPct);
        int winding = use.Payload is ProjectileOffense { OffHandWeapon: true } ? 1 : -1;
        double speed = use.Profile.Speed;
        return use with
        {
            Origin = new(Math.Round(use.Origin.X), Math.Round(use.Origin.Y)),
            Profile = use.Profile with
            {
                // The steered flight spends its own bounces. The generic reflected-line continuation must not spend them again.
                TerrainBounces = 0,
                Legs = _ => new SpectralHelixLegs(speed, rotations, bounces, winding),
                TerrainSteps = new(3, 11),
            },
        };
    }

    sealed class SpectralHelixLegs(double speed, int rotations, int bounceBudget, int initialWinding) : IProjectileLegs
    {
        const int Steps = 216;
        const float Radius = 48;
        readonly float sweep = (rotations > 0 ? rotations : 400) / 100f * 6.28318f;
        ProjectileVector centre;
        float angle;
        int step, bounced, lastBounce;
        int winding = initialWinding;
        double stepSeconds;

        public double Lifetime => 1 + Steps * stepSeconds + bounceBudget / 30d;
        public bool EndsAfterLegs => true;

        public ProjectileLeg First(ProjectileVector from, ProjectileVector aim) => First(from, aim, speed);

        public ProjectileLeg First(ProjectileVector from, ProjectileVector aim, double launchSpeed)
        {
            centre = from;
            var delta = aim - from;
            angle = MathF.Atan2((float)delta.Y, (float)delta.X) + MathF.PI / 2;
            if (angle < 0) angle += MathF.Tau;
            // 3.29.3c 0x141a88c30 and 0x141a88d90 scale each step's speed by its length over three cells.
            stepSeconds = 3 / launchSpeed;
            return Next(from, blocked: false);
        }

        float AngleAt(int at) => angle + (at - lastBounce) / (float)Steps * sweep * winding;

        public ProjectileLeg Next(ProjectileVector from, bool blocked) =>
            NextTurn(from, blocked)?.Leg ?? new(from, speed);

        public ProjectileLegTurn? NextTurn(ProjectileVector from, bool blocked)
        {
            if (blocked)
            {
                if (step >= Steps || bounced >= bounceBudget) return null;
                // 0x141a88fd0 keeps the launch centre, remembers the angle at collision, and reverses the winding.
                angle = AngleAt(step);
                lastBounce = step;
                winding = -winding;
                bounced++;
            }
            while (step < Steps)
            {
                float radial = (++step - bounced) / (float)Steps * Radius;
                var (sin, cos) = MathF.SinCos(AngleAt(step));
                var target = centre + new ProjectileVector((int)(sin * radial), (int)(cos * radial));
                double length = (target - from).Length;
                if (length > 1e-6) return new(new ProjectileLeg(target, Math.Max(.01, length) / stepSeconds));
            }
            return null;
        }
    }
}
