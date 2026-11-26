using GameServer.Core.Projectiles;

public static partial class Game
{
    internal const string PlayerBallLightningProjectile = "Metadata/Projectiles/BallLightningPlayer";

    internal static bool MovingBallLightning(string? effect) =>
        effect is "BallLightning" or "BallLightningAltX" or "BallLightningRoyale";

    static double BallLightningFrequency(int percent) => Math.Max(.01, 1 + percent / 100d);

    // 3.29.3c 0x141a38a30 uses a 200 ms fallback, divided by the same factor
    // that 0x141a38e10 multiplies the projectile's speed by.
    static long BallLightningHitGap(int milliseconds, int frequency) =>
        Math.Max(1, ProjectileRuntime.Time((milliseconds > 0 ? milliseconds : 200) / 1000d
            / BallLightningFrequency(frequency)));

    internal static ProjectileProfile BallLightningPulses(string effect, ProjectileProfile profile, Func<uint, int> get)
    {
        int frequency = get(Stat.BallLightningProjectileSpeedAndHitFrequencyPlusPctFinal);
        long gap = BallLightningHitGap(get(Stat.BallLightningTimeBetweenHitsMs), frequency);
        double speed = profile.Speed * BallLightningFrequency(frequency);
        bool orbiting = effect == "BallLightningAltX";
        return profile with
        {
            Speed = speed,
            Count = orbiting ? 1 : profile.Count,
            DirectDamage = false,
            HitRule = new(ProjectileHitScope.Emission, ProjectileRepeat.Interval, gap),
            Pulse = new("ball-lightning-bolt", 0, ProjectileRuntime.TickQuanta) { AreaDamage = false },
            Legs = orbiting ? _ => new BallLightningOrbit(speed) : null,
        };
    }

    static ProjectileUse CentreBallLightningOrbit(ProjectileUse use)
    {
        var centre = new ProjectileVector(Math.Round(use.Origin.X), Math.Round(use.Origin.Y));
        return use with { Origin = centre, Aim = centre + (use.Aim - use.Origin) };
    }

    sealed class BallLightningOrbit(double nominalSpeed) : IProjectileLegs
    {
        // 3.29.3c 0x141a38fd0 advances on each completed move, including duplicate grid cells.
        const int Steps = 216;
        const float Radius = 48, Sweep = 26.703516f;
        ProjectileVector centre;
        float startAngle;
        double speed = nominalSpeed;
        int step;

        // The turns end the flight. The safety deadline covers the spiral and the rounding at every waypoint.
        public double Lifetime => Math.Min(long.MaxValue / (2d * ProjectileRuntime.QuantaPerSecond),
            1 + (Radius * (1d + Sweep) + 3 * Steps) / speed);
        public bool EndsAfterLegs => true;

        public ProjectileLeg First(ProjectileVector from, ProjectileVector aim) => First(from, aim, speed);

        public ProjectileLeg First(ProjectileVector from, ProjectileVector aim, double launchSpeed)
        {
            centre = from;
            var delta = aim - from;
            startAngle = MathF.Atan2((float)delta.Y, (float)delta.X) + MathF.PI / 2;
            if (startAngle < 0) startAngle += MathF.Tau;
            speed = launchSpeed;
            return Next(from, blocked: false);
        }

        public ProjectileLeg Next(ProjectileVector from, bool blocked) =>
            NextTurn(from, blocked)?.Leg ?? new(from, speed);

        public ProjectileLegTurn? NextTurn(ProjectileVector from, bool blocked)
        {
            if (blocked) return null;
            while (step < Steps)
            {
                float progress = ++step / (float)Steps;
                var (sin, cos) = MathF.SinCos(startAngle + progress * Sweep);
                float radius = Radius * progress;
                var target = centre + new ProjectileVector((int)(sin * radius), (int)(cos * radius));
                if ((target - from).Length > 1e-6) return new(new ProjectileLeg(target, speed));
            }
            return null;
        }
    }
}
