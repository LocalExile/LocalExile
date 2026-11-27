using GameServer.Core.Diagnostics;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const string ConflagrationEffect = "Conflagration";
    const string ConflagrationArrowType = "Metadata/Effects/Spells/Napalm_Arrows/BloomPod";
    const ushort ConflagrationBurnBuff = 2612;
    const int ConflagrationTriggerRadius = 10;
    const byte ConflagrationTransition = 5;
    static readonly RuleTable<GameArea, HashSet<uint>> ConflagrationTargets = new();
    static readonly RuleTable<SpawnedMonster, MonsterIgnites> ConflagrationBurns = new();

    static bool HasConflagrationBurn(GameArea area) =>
        ConflagrationTargets.TryGetValue(area, out var targets) && targets.Count > 0;

    static bool ConflagrationBurning(SpawnedMonster monster, double now) =>
        ConflagrationBurns.TryGetValue(monster, out var burns) && burns.IgnitedAt(now);

    static Task TickConflagrationBurn(Player player) => ConflagrationTargets.TryGetValue(player.Area, out var targets)
        ? TickStrongestDots(player, targets, ConflagrationBurns.GetOrCreateValue,
            (monster, now) => MonsterDotTaken(monster, DamageType.Fire, now), "burnt by Conflagration")
        : Task.CompletedTask;

    static void ApplyConflagrationBurn(Player player, uint id, SpawnedMonster monster, DamageOverTime burn, double now)
    {
        var burns = ConflagrationBurns.GetOrCreateValue(monster);
        burns.SetExpiryRate(now, monster.RecipientDebuffRate);
        if (!burns.Add(now, burn.DamagePerSecond, burn.RemainingSeconds)) return;
        ConflagrationTargets.GetOrCreateValue(player.Area).Add(id);
        ShowDotBuffLocked(player, id, burns, ConflagrationBurnBuff, now);
        if (ServerTrace.Projectiles)
            Console.WriteLine($"[conflagration] burn {id} t={now:F6} dps={burn.DamagePerSecond:F2} duration={burn.RemainingSeconds:F3}");
    }

    static int ConflagrationArrowCount(Func<uint, int> get, bool mirage = false) =>
        get(Stat.MaximumNumberOfProjectilesToFireIs1) != 0 ? 1 : Math.Clamp(
            GatewayProjectileDefaults.Resolve(GroundArrow.Metadata, get, 1000, true, true).Count
                + (mirage && get(Stat.ModifiersToProjectileCountDoNotApply) == 0
                    ? get(Stat.MirageArcherNumberOfAdditionalProjectiles) : 0), 1, 256);

    static void ReleaseConflagration(ProjectileSession session, ProjectileUse use, ushort skill, int effect,
        IReadOnlyDictionary<uint, int> own, IReadOnlyDictionary<uint, int> playerStats, double damageFactor,
        bool trap, bool mine, double? actionStart)
    {
        var player = session.Player;
        var shot = (ProjectileOffense)use.Payload!;
        var caster = shot.ActualCaster ?? playerStats;
        int Own(uint id) => own.GetValueOrDefault(id);
        int Get(uint id) => Own(id) + caster.GetValueOrDefault(id);
        var area = SkillAreaStats(player, own, effect, caster);
        int Radius(uint stat) => SkillArea.Radius(Own(stat), area.GetValueOrDefault, spell: false);
        var kinds = DurationKinds(player, effect);
        double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), caster.GetValueOrDefault, Own, kinds);
        double burnDuration = SkillDurationModifiers.Seconds(Own(Stat.BaseSecondarySkillEffectDuration),
            caster.GetValueOrDefault, Own, kinds | SkillDurationModifiers.Kinds.Secondary);
        var burn = BowRainBurnDamage(shot, own, caster, burnDuration, trap, mine,
            mine && player.RecentEvents.Happened(StatConditions.DetonatedMine, AreaTime(player)), damageFactor);
        double explosionMore = Math.Max(0, 1 + Get(Stat.NapalmArrowDetonationHitDamagePlusPctFinal) / 100d);
        var explosion = shot.ScaleHitAndAilmentDamage(explosionMore) with
        {
            ConflagrationBurn = burn.DamagePerSecond > 0 && burn.RemainingSeconds > 0 ? burn : null,
            MirageSource = null,
            ActionSequence = null,
        };
        int radius = Radius(Stat.ActiveSkillBaseSecondaryAreaOfEffectRadius);
        int explosionRadius = Radius(Stat.ActiveSkillBaseTertiaryAreaOfEffectRadius);
        int spread = Radius(Stat.ActiveSkillBaseAreaOfEffectRadius);
        int limit = Math.Clamp(Get(Stat.NapalmArrowMaximumNumberOfUnprimedArrowsAllowed), 0, 20);
        int count = ConflagrationArrowCount(Get, shot.Mirage);
        long now = session.Runtime.Now;
        double start = actionStart ?? now / (double)ProjectileRuntime.QuantaPerSecond;
        int speed = RainOfArrowsFallSpeed(player, skill, playerStats, own, shot.FromTotem);
        // NapalmArrows (3.29.3c): shared pre-fall speed calculation with a .325 s base, .33 s fall,
        // then the gem's BlastRainArrowDelayMs for each additional arrow.
        double preFall = .325 * Math.Min(2.4, 100d / (100 + Math.Max(-99, speed)));
        long first = Math.Max(now, ProjectileRuntime.Time(start + preFall + .33));
        long interval = ProjectileRuntime.Time(Math.Max(1, Get(Stat.BlastRainArrowDelayMs)) / 1000d);
        var source = shot.FromTotem || shot.Mirage || shot.FromTrapOrMine
            ? use.Origin : ProjectileSession.Vector(player.Walk.Position(start));
        var origin = ((int)Math.Round(source.X), (int)Math.Round(source.Y));
        var target = ((int)use.Aim.X, (int)use.Aim.Y);
        var layout = player.Layout;
        var sight = layout.Sight;
        bool InBounds((int X, int Y) cell) => cell.X >= 0 && cell.Y >= 0
            && (layout.GridW <= 0 || cell.X < layout.GridW) && (layout.GridH <= 0 || cell.Y < layout.GridH);
        bool Clear((int X, int Y) from, (int X, int Y) to) => InBounds(to)
            && (sight is null || !ClientPathfinder.LineBelow(from, to, sight, 1))
            && (player.SightBarrier.Count == 0 || !ClientPathfinder.Line(from, to).Any(player.SightBarrier.Contains));
        if (!layout.WalkableFor(target.Item1, target.Item2, 0) || !Clear(origin, target))
        {
            var last = origin;
            foreach (var cell in ClientPathfinder.Line(origin, target))
            {
                if (!InBounds(cell) || !layout.WalkableFor(cell.X, cell.Y, 0)
                    || player.Barrier.Contains(cell) || player.SightBarrier.Contains(cell)) break;
                if (layout.WalkableFor(cell.X, cell.Y, 2)) last = cell;
            }
            target = last;
        }
        use = use with { Origin = ProjectileSession.Vector(origin), Aim = ProjectileSession.Vector(target) };
        // The planner runs on the first detached action, whose detail byte is one.
        var random = new AnimationVariantSelector.NativeRandom([use.Sequence, 1, use.Emitter.Id, use.RepeatsLeft, 0, 0, 0, 0]);
        var detonation = use with { Payload = explosion };
        bool proxy = shot.Mirage || shot.FromTrapOrMine || shot.FromTotem;
        var trigger = shot.FromTotem ? use.Emitter : use.Owner;
        for (int i = 0; i < count; i++)
        {
            // The native planner uses a uniform radial distance and consumes both draws even for the aimed first arrow.
            float distance = random.Range(0, spread), angle = random.Range(0, 6.28318f);
            var cell = i == 0 ? target : (target.Item1 + (int)(MathF.Sin(angle) * distance),
                target.Item2 + (int)(MathF.Cos(angle) * distance));
            if (!Clear(target, cell)) continue;
            var at = ProjectileSession.Vector(cell);
            session.Runtime.Schedule(first + interval * i, () =>
            {
                if (!session.Active || player.PlayerLife == 0) return;
                session.Runtime.Impact(use, at, new("Conflagration landing", radius));
                if (proxy) session.RainArt(at, Art.Animated("NapalmArrowsLandingScaled", 0), .75, radius);
                if (duration > 0 && limit != 0)
                    session.PlantConflagration(at, detonation, trigger, explosionRadius, ProjectileRuntime.Time(duration), limit);
                if (ServerTrace.Projectiles)
                    Console.WriteLine($"[conflagration] landing seq={use.Sequence} at={at} t={session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond:F6}");
            });
        }
    }

    public sealed partial class ProjectileSession
    {
        sealed record ConflagrationArrow(uint Id, ProjectileVector At, ProjectileUse Use, ProjectileActorKey Trigger,
            int Radius, long ExpiresAt);

        readonly List<ConflagrationArrow> conflagrationArrows = [];
        readonly HashSet<uint> conflagrationObjects = [];
        bool pollingConflagration;

        void ClearConflagration()
        {
            foreach (uint id in conflagrationObjects) RetireSkillEffectObject(id);
            conflagrationObjects.Clear();
            conflagrationArrows.Clear();
            pollingConflagration = false;
        }

        internal void PlantConflagration(ProjectileVector at, ProjectileUse use, ProjectileActorKey trigger,
            int radius, long duration, int limit)
        {
            while (conflagrationArrows.Count >= limit) RemoveConflagrationArrow(0);
            uint id = Player.TakeObjectId();
            conflagrationArrows.Add(new(id, at, use, trigger, radius, Runtime.Now + duration));
            conflagrationObjects.Add(id);
            Player.TrackProjectileObject(id);
            Create(id, () =>
            {
                var w = new BlobWriter();
                w.U8(0).U8(0);
                w.Positioned((int)at.X, (int)at.Y, team: 1);
                w.U8(0); // Animated. LimitedLifespan reads no bytes.
                w.U16(0).U8(1).U8(0); // Empty Buffs, Transitionable state 1, no tasks.
                return InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(ConflagrationArrowType), w.ToArray());
            }, wake: true);
            if (!pollingConflagration)
            {
                pollingConflagration = true;
                Runtime.Schedule(Runtime.Now + ProjectileRuntime.TickQuanta, TickConflagrationArrows);
            }
        }

        void TickConflagrationArrows()
        {
            long now = Runtime.Now;
            for (int i = conflagrationArrows.Count - 1; i >= 0; i--)
                if (conflagrationArrows[i].ExpiresAt <= now || Player.PlayerLife == 0)
                    RemoveConflagrationArrow(i);

            for (int i = 0; i < conflagrationArrows.Count;)
            {
                var arrow = conflagrationArrows[i];
                if (Actor(arrow.Trigger, now)?.Position(now) is { } at && InRange(at, arrow.At, ConflagrationTriggerRadius))
                    DetonateConflagration(i);
                else i++;
            }
            pollingConflagration = conflagrationArrows.Count != 0;
            if (pollingConflagration) Runtime.Schedule(now + ProjectileRuntime.TickQuanta, TickConflagrationArrows);
        }

        static bool InRange(ProjectileVector a, ProjectileVector b, double radius)
        {
            var delta = a - b;
            return delta.Dot(delta) <= radius * radius;
        }

        void DetonateConflagration(int first)
        {
            var chain = new Queue<ConflagrationArrow>();
            Consume(first);
            while (chain.TryDequeue(out var arrow))
            {
                Runtime.Impact(arrow.Use, arrow.At, new("Conflagration detonation", arrow.Radius));
                RainArt(arrow.At, Art.Animated("NapalmArrowsExplosion", 0), .75, arrow.Radius);
                if (ServerTrace.Projectiles)
                    Console.WriteLine($"[conflagration] detonate {arrow.Id} at={arrow.At} t={Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond:F6}");
                for (int i = 0; i < conflagrationArrows.Count;)
                {
                    if (InRange(arrow.At, conflagrationArrows[i].At, arrow.Radius)) Consume(i);
                    else i++;
                }
            }

            void Consume(int i)
            {
                var arrow = conflagrationArrows[i];
                conflagrationArrows.RemoveAt(i);
                World(arrow.Id, InstanceEntities.TransitionableUpdate(arrow.Id, ConflagrationTransition, 3));
                Runtime.Schedule(Runtime.Now + ProjectileRuntime.Time(.35), () => RemoveConflagrationObject(arrow.Id));
                chain.Enqueue(arrow);
            }
        }

        void RemoveConflagrationArrow(int i)
        {
            uint id = conflagrationArrows[i].Id;
            conflagrationArrows.RemoveAt(i);
            RemoveConflagrationObject(id);
        }

        void RemoveConflagrationObject(uint id)
        {
            WorldRecords(id, () =>
            {
                conflagrationObjects.Remove(id);
                Player.UntrackProjectileObject(id);
                return [InstanceProtocol.RemoveObject(id)];
            }, remove: true);
        }
    }
}
