using GameServer.Core.Diagnostics;
using GameServer.Core.Projectiles;

public static partial class Game
{
    const string KineticFusilladeDetonation = "KineticFusilladeAltX";
    const string FusilladeBolt = "Metadata/Projectiles/KineticFusilladeProjectile";
    const string FusilladeHover = "Metadata/Effects/Spells/kinetic_fusillade/projectile.ao";
    const string FusilladeExplosion = "Metadata/Effects/Spells/kinetic_fusillade/explode.ao";

    static readonly ProjectileSkillDefinition KineticFusilladeProjectile = new(FusilladeBolt,
        new((get, speed, _) => GatewayProjectileDefaults.Resolve(FusilladeBolt, get, speed, attack: true)))
    {
        SpecializeRelease = (context, use) =>
        {
            var shot = (ProjectileOffense)use.Payload!;
            bool detonates = SkillsFor(context.Player).EffectId(shot.Effect) == KineticFusilladeDetonation;
            int interval = context.Get(Stat.KineticFusilladeDelayBetweenProjectilesMs);
            if (interval <= 0) interval = context.Get(Stat.KineticFusilladeBaseDelayBetweenProjectilesMs);
            int targetingRadius = Math.Max(0, context.Get(Stat.KineticFusilladeSubsequentTargetingRadius));
            var area = new ProjectileAreaRecipe(detonates ? "fusillade-detonation" : "fusillade-impact",
                SkillRadius(context.Player, shot.SkillStats, spell: false, shot.Effect))
            {
                CentreOnContactTarget = true,
                ExcludeContactTarget = true,
                RequiresEnemyHit = true
            };
            var cast = new FusilladeCast(detonates, Math.Clamp(context.Get(Stat.KineticFusilladeMaximumFloatingProjectiles), 0, 256),
                ProjectileRuntime.Time(SkillDurationModifiers.Seconds(context.Own(Stat.BaseSkillEffectDuration), context.PlayerStat, context.Own)),
                ProjectileRuntime.Time(Math.Max(0, interval) / 1000d), targetingRadius,
                context.Get(Stat.KineticFusilladeDamagePlusPctFinalPerProjectileFired), area);
            return use with
            {
                Payload = shot with
                {
                    Fusillade = cast, ClientPredicted = false,
                    AreaVisual = Art.AnimatedInPlaceOf(FusilladeExplosion, context.Variation)
                }
            };
        }
    };

    internal sealed record FusilladeCast(bool Detonates, int Maximum, long Duration, long Interval,
        int TargetingRadius, int DamageMorePerShot, ProjectileAreaRecipe Area);

    public sealed partial class ProjectileSession
    {
        readonly record struct FloatingBolt(ProjectileUse Use, ProjectileOffense Shot, ProjectileVector Position, Action? Remove);

        sealed class FusilladeQueue
        {
            public readonly Queue<FloatingBolt> Bolts = new();
            public readonly HashSet<ProjectileActorKey> Targets = [];
            public ProjectileVector Aim;
            public int Fired, Generation;
        }

        Dictionary<(ProjectileActorKey Emitter, int Effect), FusilladeQueue>? fusillades;

        void QueueFusillade(ProjectileUse use, ProjectileOffense shot, FusilladeCast cast)
        {
            if (cast.Maximum == 0 || use.CanEmit?.Invoke() == false) return;
            var key = (use.Emitter, shot.Effect);
            var queues = fusillades ??= [];
            if (!queues.TryGetValue(key, out var queue)) queues[key] = queue = new();
            int count = Math.Clamp(use.Profile.Count, 0, cast.Maximum);
            while (queue.Bolts.Count + count > cast.Maximum) queue.Bolts.Dequeue().Remove?.Invoke();
            if (queue.Bolts.Count == 0 && count == 0) { queues.Remove(key); return; }

            var rng = new Random(++seed);
            var caster = Actor(use.Emitter, Runtime.Now)?.Position(Runtime.Now) ?? use.Origin;
            for (int i = 0; i < count; i++)
            {
                // The native cast distributes floating bolts 45–90 world units around their caster.
                double angle = rng.NextDouble() * Math.Tau;
                var offset = new ProjectileVector(Math.Cos(angle), Math.Sin(angle)) * (45 * (1 + rng.NextDouble()) * 23 / 250);
                var centre = caster + offset;
                Action? remove = null;
                Effect((int)Math.Round(centre.X), (int)Math.Round(centre.Y), FusilladeHover, lifetime: null,
                    startClip: 0, captureRemoval: action => remove = action);
                queue.Bolts.Enqueue(new(use, shot, centre, remove));
            }
            queue.Aim = use.Aim;
            queue.Fired = 0;
            queue.Targets.Clear();
            int generation = ++queue.Generation;
            Runtime.Schedule(Runtime.Now + cast.Duration, Fire);
            if (ServerTrace.Projectiles)
                Console.WriteLine($"[gateway] {Player.Connection.Remote} fusillade hold effect={shot.Effect} "
                    + $"count={queue.Bolts.Count} time={Runtime.Now} due={Runtime.Now + cast.Duration}");

            void Fire()
            {
                if (queue.Generation != generation) return;
                if (!Active || Actor(key.Emitter, Runtime.Now) is null)
                {
                    while (queue.Bolts.TryDequeue(out var expired)) expired.Remove?.Invoke();
                    queues.Remove(key);
                    return;
                }
                var bolt = queue.Bolts.Dequeue();
                bolt.Remove?.Invoke();
                if (queue.Bolts.Count > 0) Runtime.Schedule(Runtime.Now + cast.Interval, Fire);
                else queues.Remove(key);
                if (bolt.Use.CanEmit?.Invoke() == false) return;

                FireFusilladeBolt(queue, bolt);
            }
        }

        void FireFusilladeBolt(FusilladeQueue queue, FloatingBolt bolt)
        {
            var cast = bolt.Shot.Fusillade!;
            double more = Math.Max(0, 1 + queue.Fired * (cast.DamageMorePerShot / 100d));
            var shot = bolt.Shot.ScaleHitAndAilmentDamage(more) with { Fusillade = null };
            var use = bolt.Use with
            {
                Origin = bolt.Position, Payload = shot, RadialLaunchOffset = 0,
                ProjectilePayloads = null, ProjectileSpeeds = null, ProjectileTargets = null,
                Profile = bolt.Use.Profile with { Count = 1, NativeVolley = null, SequentialDelay = 0 }
            };
            if (ServerTrace.Projectiles)
                Console.WriteLine($"[gateway] {Player.Connection.Remote} fusillade fire effect={shot.Effect} "
                    + $"index={queue.Fired} time={Runtime.Now} multiplier={more:0.###}");
            queue.Fired++;

            if (cast.Detonates)
            {
                Runtime.Impact(use with { Seed = ++seed }, bolt.Position, cast.Area);
                return;
            }
            var target = FusilladeTarget(queue, use, cast.TargetingRadius);
            if (target is { } enemy)
            {
                queue.Targets.Add(enemy.Key);
                queue.Aim = enemy.Position(Runtime.Now);
            }
            ReleaseFlight(use with
            {
                Aim = queue.Aim,
                Profile = use.Profile with { ContactArea = cast.Area, ContactAreaReplacesDirectContact = true }
            }, notifyWisps: false);
        }

        ProjectileActor? FusilladeTarget(FusilladeQueue queue, ProjectileUse use, int radius)
        {
            long now = Runtime.Now;
            ProjectileActor? first = null, fresh = null;
            double nearest = double.MaxValue, nearestFresh = double.MaxValue;
            foreach (var actor in ActorsNear(queue.Aim, radius, now))
            {
                if (actor.Team == use.Team || actor.Key == use.Owner) continue;
                var at = actor.Position(now);
                var delta = at - queue.Aim;
                double distance = delta.Dot(delta);
                if (distance > (double)radius * radius || (at - use.Origin).Length > use.Profile.Range
                    || TraceTerrain(use.Origin, at) is not null) continue;
                if (distance < nearest) (first, nearest) = (actor, distance);
                if (distance < nearestFresh && !queue.Targets.Contains(actor.Key))
                    (fresh, nearestFresh) = (actor, distance);
            }
            if (fresh is not null) return fresh;
            if (first is not null) queue.Targets.Clear();
            return first;
        }

        void ClearFusillades()
        {
            if (fusillades is null) return;
            foreach (var queue in fusillades.Values)
            {
                queue.Generation++;
                while (queue.Bolts.TryDequeue(out var bolt)) bolt.Remove?.Invoke();
            }
            fusillades.Clear();
        }
    }
}
