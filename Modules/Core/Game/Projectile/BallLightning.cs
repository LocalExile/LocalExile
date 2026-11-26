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

    internal static ProjectileProfile BallLightningPulses(ProjectileProfile profile, Func<uint, int> get)
    {
        int frequency = get(Stat.BallLightningProjectileSpeedAndHitFrequencyPlusPctFinal);
        long gap = BallLightningHitGap(get(Stat.BallLightningTimeBetweenHitsMs), frequency);
        return profile with
        {
            Speed = profile.Speed * BallLightningFrequency(frequency),
            DirectDamage = false,
            HitRule = new(ProjectileHitScope.Emission, ProjectileRepeat.Interval, gap),
            Pulse = new("ball-lightning-bolt", 0, ProjectileRuntime.TickQuanta) { AreaDamage = false },
        };
    }
}
