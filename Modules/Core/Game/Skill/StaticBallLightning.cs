using GameServer.Core.Projectiles;

public static partial class Game
{
    public sealed partial class ProjectileSession
    {
        const string StaticBallLightningArt = "Metadata/Effects/Spells/lightning_orb/lightning_orb_orb.ao";
        const double BallLightningLifetime = 2.0;
        const double BallLightningBaseInterval = .15;

        internal void SchedulePlacedStaticBall(PlacedAreaHit placement, Func<bool> current)
        {
            var device = placement.Device;
            Runtime.Schedule(Runtime.Now, () =>
            {
                if (Active && current())
                    ReleaseStaticBall(device.Effect, device.Own, device.At, current,
                        (at, radius, now) => placement.Apply(Player, at, radius, now));
            });
        }

        void ReleaseStaticBall(int effect, IReadOnlyDictionary<uint, int> own, (int X, int Y) at, Func<bool> current,
            Action<(int X, int Y), int, double> hit)
        {
            var owner = CharacterPlayerStats(Player);
            double frequency = (double)own.GetValueOrDefault(Stat.BallLightningProjectileSpeedAndHitFrequencyPlusPctFinal)
                + owner.GetValueOrDefault(Stat.BallLightningProjectileSpeedAndHitFrequencyPlusPctFinal);
            int interval = own.GetValueOrDefault(Stat.BallLightningTimeBetweenHitsMs);
            double baseInterval = interval > 0 ? interval / 1000d : BallLightningBaseInterval;
            long gap = Math.Max(1, ProjectileRuntime.Time(baseInterval / Math.Max(.01, 1 + frequency / 100)));
            long life = ProjectileRuntime.Time(BallLightningLifetime);
            int radius = SkillRadius(Player, own, spell: true, effect);
            Effect(at.X, at.Y, StaticBallLightningArt, life);
            long end = Runtime.Now + life;
            PulseAt(Runtime.Now + gap);

            void PulseAt(long due)
            {
                if (due > end) return;
                Runtime.Schedule(due, () =>
                {
                    if (!Active || !current()) return;
                    hit(at, radius, ProjectileWorldTime(Player));
                    PulseAt(due + gap);
                });
            }
        }
    }
}
