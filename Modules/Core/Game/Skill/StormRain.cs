using GameServer.Core.Diagnostics;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const string StormRainConduit = "StormRainAltX";
    const string StormRainFence = "StormRainAltY";
    const ushort StormRainBeamRow = 456;
    const string StormRainArrowType = "Metadata/MiscellaneousObjects/PrismaticRainLightningArrow";
    const double StormRainBeamSeconds = .325;
    const int StormRainLinkRange = 60;

    static bool IsStormRain(string? id) => id is "StormRain" or StormRainConduit or StormRainFence;

    static int StormRainArrowCount(Func<uint, int> get, int count, bool mirage = false) =>
        get(Stat.ModifiersToProjectileCountDoNotApply) != 0 || get(Stat.MaximumNumberOfProjectilesToFireIs1) != 0
            ? 1 : Math.Clamp(count
            + get(Stat.StormRainNumAdditionalArrows) + (mirage ? get(Stat.MirageArcherNumberOfAdditionalProjectiles) : 0), 1, 256);

    static int StormRainBeamInterval(Func<uint, int> get)
    {
        double frequency = Math.Max(.01, 1 + get(Stat.PrismaticRainBeamFrequencyPlusPct) / 100d);
        return (int)Math.Clamp(Math.Truncate(get(Stat.PrismaticRainBeamBaseFrequencyMs) / frequency), 1, int.MaxValue);
    }

    static void ReleaseStormRain(ProjectileSession session, ProjectileUse use, string effect, ushort skill, int effectRow,
        IReadOnlyDictionary<uint, int> own, IReadOnlyDictionary<uint, int> playerStats, double? actionStart)
    {
        var player = session.Player;
        var shot = (ProjectileOffense)use.Payload!;
        int Get(uint stat) => own.GetValueOrDefault(stat) + playerStats.GetValueOrDefault(stat);
        int count = StormRainArrowCount(Get, use.Profile.Count, shot.Mirage);
        int limit = Math.Clamp(Get(Stat.NumberOfAllowedStormArrows), 1, 100);
        int pulses = Math.Clamp(Get(Stat.StormRainPulseCount), 1, 100);
        long period = ProjectileRuntime.Time(StormRainBeamInterval(Get) / 1000d);
        var area = SkillAreaStats(player, own, effectRow, playerStats);
        int Radius(int basis) => SkillArea.Radius(basis, area.GetValueOrDefault, spell: false);
        int radius = Radius(10);
        int halfWidth = Radius(6);
        int speed = RainOfArrowsFallSpeed(player, skill, playerStats, own, shot.FromTotem);
        double fallTime = 100d / (100 + Math.Max(-99, speed));
        long now = session.Runtime.Now;
        // 3.29.3c PrismaticRain uses a .30 s delay before the first arrow falls, a .25 s fall
        // animation, and a .07 s placement interval. Fall speed scales the delay and interval.
        double start = actionStart ?? now / (double)ProjectileRuntime.QuantaPerSecond;
        var source = shot.FromTotem || shot.Mirage || shot.FromTrapOrMine
            ? use.Origin : ProjectileSession.Vector(player.Walk.Position(start));
        var offset = use.Aim - source;
        double reach = Math.Max(0, WeaponFor(player).Range + Get(Stat.AttackMaximumActionDistancePlus));
        var requested = offset.Length > reach ? source + offset.Unit * reach : use.Aim;
        var aim = ((int)Math.Round(requested.X), (int)Math.Round(requested.Y));
        var origin = ((int)source.X, (int)source.Y);
        var layout = player.Layout;
        var sight = layout.Sight;
        var barriers = player.SightBarrier;
        bool Walkable((int X, int Y) p) => p.X >= 0 && p.Y >= 0
            && (layout.GridW <= 0 || p.X < layout.GridW) && (layout.GridH <= 0 || p.Y < layout.GridH)
            && layout.WalkableFor(p.X, p.Y, 0) && !player.Barrier.Contains(p);
        bool Visible((int X, int Y) from, (int X, int Y) to) => (sight is null || !ClientPathfinder.LineBelow(from, to, sight, 1))
            && (barriers.Count == 0 || !ClientPathfinder.Line(from, to).Any(barriers.Contains));
        if (!Walkable(aim) || !Visible(origin, aim))
        {
            var last = origin;
            foreach (var cell in ClientPathfinder.Line(origin, aim))
            {
                if (!Walkable(cell) || barriers.Contains(cell)) break;
                // The native action backs up to a cell with three units of clearance when the aim is outside the floor.
                if (layout.WalkableFor(cell.X, cell.Y, 2)) last = cell;
            }
            aim = last;
        }
        use = use with { Origin = source, Aim = ProjectileSession.Vector(aim) };
        long landAt = Math.Max(now, ProjectileRuntime.Time(start + .25 + .3 * Math.Min(2.4, fallTime)));
        long spacing = ProjectileRuntime.Time(.07 * Math.Clamp(fallTime, .5, 2));
        double facing = Math.Atan2(use.Aim.X - source.X, source.Y - use.Aim.Y);
        var plan = new StormRainPlan(effect, limit, pulses, period, halfWidth, shot.FromTotem ? use.Emitter : use.Owner);
        int spread = Radius(7);
        var shift = new AnimationVariantSelector.NativeRandom([use.Sequence, 0, use.Emitter.Id, use.RepeatsLeft, 0, 0, 0, 0]);
        foreach (var (point, step) in StormRainLandings(use, count, spread, Clear))
        {
            var landing = session.StormRainArrowNear(point) ? Separate(point, shift.Range(0, MathF.Tau)) : point;
            session.Runtime.Schedule(landAt + step * spacing, () =>
            {
                if (!session.Active || player.PlayerLife == 0) return;
                session.RainImpact(use, landing, new("Storm Rain landing", radius));
                if (shot.Mirage || shot.FromTrapOrMine || shot.FromTotem)
                    session.RainArt(landing, Art.Animated("storm_rain_impact", 0), .75, radius);
                session.PlantStormRain(landing, (float)facing, use, plan);
                if (ServerTrace.Projectiles)
                    Console.WriteLine($"[storm-rain] plant {effect} seq={use.Sequence} aim={use.Aim} at={landing} t={session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond:F6}");
            });
        }

        bool Clear(ProjectileVector p)
        {
            var cell = ((int)p.X, (int)p.Y);
            return Walkable(cell) && Visible(cell, aim);
        }

        ProjectileVector Separate(ProjectileVector point, float angle)
        {
            var from = ((int)point.X, (int)point.Y);
            var to = ((int)point.X + (int)MathF.Floor(MathF.Sin(angle) * 7 + .5f),
                (int)point.Y + (int)MathF.Floor(-MathF.Cos(angle) * 7 + .5f));
            var last = from;
            foreach (var candidate in ClientPathfinder.Line(from, to))
            {
                if (sight?.Invoke(candidate.X, candidate.Y) == 0 || barriers.Contains(candidate)) break;
                if (Walkable(candidate)) last = candidate;
            }
            return ProjectileSession.Vector(last);
        }
    }

    // PrismaticRain samples expanding annuli using the action's sequence and emitting actor.
    static IEnumerable<(ProjectileVector Point, int Step)> StormRainLandings(ProjectileUse use, int count, int radius,
        Func<ProjectileVector, bool> clear)
    {
        var random = new AnimationVariantSelector.NativeRandom([use.Sequence, 0, use.Emitter.Id, 0, 0, 0, 0, 0]);
        int inner = 0, step = 0;
        for (int i = 0; i < count; i++)
        {
            for (int attempt = 0; attempt < 9; attempt++)
            {
                float angle = random.Range(0, MathF.Tau);
                float distance = inner + MathF.Sqrt(random.Range(0, radius * radius));
                var point = use.Aim + new ProjectileVector((int)(MathF.Sin(angle) * distance), (int)(-MathF.Cos(angle) * distance));
                if (!clear(point)) continue;
                yield return (point, step++);
                inner += Math.Max(2, radius - Math.Max(0, i - 3));
                break;
            }
        }
    }

    internal readonly record struct StormRainPlan(string Effect, int Limit, int Pulses, long Period, double HalfWidth, ProjectileActorKey Caster);

    public sealed partial class ProjectileSession
    {
        readonly Dictionary<string, LinkedList<StormRainArrow>> stormRainArrows = [];
        readonly HashSet<uint> stormRainObjects = [];

        sealed class StormRainArrow(ProjectileVector at, uint id, int pulses)
        {
            public ProjectileVector At { get; } = at;
            public uint Id { get; } = id;
            public int Remaining { get; set; } = pulses;
        }

        void ClearStormRain()
        {
            foreach (uint id in stormRainObjects) RetireSkillEffectObject(id);
            stormRainObjects.Clear();
            stormRainArrows.Clear();
        }

        internal bool StormRainArrowNear(ProjectileVector at)
        {
            foreach (var group in stormRainArrows.Values)
                foreach (var arrow in group)
                {
                    var delta = arrow.At - at;
                    if (delta.Dot(delta) <= 4) return true;
                }
            return false;
        }

        internal void PlantStormRain(ProjectileVector at, float facing, ProjectileUse use, StormRainPlan plan)
        {
            if (!stormRainArrows.TryGetValue(plan.Effect, out var arrows))
                stormRainArrows.Add(plan.Effect, arrows = new());
            while (arrows.Count >= plan.Limit) Remove(arrows.First!);
            uint id = Player.TakeObjectId();
            var arrow = arrows.AddLast(new StormRainArrow(at, id, plan.Pulses));
            stormRainObjects.Add(id);
            Player.TrackProjectileObject(id);
            Create(id, () =>
            {
                return InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(StormRainArrowType),
                    new GroundStormArrow((int)at.X, (int)at.Y, facing).Blob());
            }, wake: true);
            Runtime.Schedule(Runtime.Now + plan.Period, Pulse);

            void Pulse()
            {
                if (arrow.List is null) return;
                if (!Active || Player.PlayerLife == 0)
                {
                    Remove(arrow);
                    return;
                }
                ProjectileVector end = default;
                uint endId = 0;
                if (plan.Effect == StormRainConduit)
                {
                    if (Actor(plan.Caster, Runtime.Now) is { } caster)
                    {
                        end = caster.Position(Runtime.Now);
                        endId = caster.Key.Id;
                    }
                }
                else
                {
                    double nearest = StormRainLinkRange * StormRainLinkRange + 1;
                    foreach (var other in arrows)
                    {
                        if (other.Id == id) continue;
                        var offset = other.At - at;
                        double squared = offset.Dot(offset);
                        if (squared >= nearest) continue;
                        nearest = squared;
                        end = other.At;
                        endId = other.Id;
                    }
                }
                if (endId != 0)
                {
                    Runtime.ImpactAlong(use, at, end, plan.HalfWidth, "Storm Rain beam");
                    ShowStormRainBeam(at, id, end, endId);
                }
                if (--arrow.Value.Remaining > 0) Runtime.Schedule(Runtime.Now + plan.Period, Pulse);
                else Remove(arrow);
            }

            void Remove(LinkedListNode<StormRainArrow> node)
            {
                arrows.Remove(node);
                // Leave the visual endpoints in place until shots already in flight have finished playing.
                long removeAt = Runtime.Now + ProjectileRuntime.Time(StormRainBeamSeconds);
                Runtime.Schedule(removeAt, () => RemoveStormRainObject(node.Value.Id));
            }
        }

        void ShowStormRainBeam(ProjectileVector from, uint fromId, ProjectileVector to, uint toId)
        {
            uint id = Player.TakeObjectId();
            var beam = new BeamEffectObject((int)from.X, (int)from.Y, fromId,
                (float)to.X * BeamEffectObject.WorldPerCell, (float)to.Y * BeamEffectObject.WorldPerCell, toId, StormRainBeamRow);
            stormRainObjects.Add(id);
            Player.TrackProjectileObject(id);
            Create(id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(BeamEffectObject.TypePath), beam.Blob()),
                wake: true);
            if (ServerTrace.Projectiles)
                Console.WriteLine($"[storm-rain] beam {fromId} {from} -> {toId} {to} t={Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond:F6}");
            Runtime.Schedule(Runtime.Now + ProjectileRuntime.Time(StormRainBeamSeconds), () => RemoveStormRainObject(id));
        }

        void RemoveStormRainObject(uint id)
        {
            WorldRecords(id, () =>
            {
                stormRainObjects.Remove(id);
                Player.UntrackProjectileObject(id);
                return [InstanceProtocol.RemoveObject(id)];
            }, remove: true);
        }
    }

    // Positioned, BaseEvents, StateMachine (no shared states), Animated, Transitionable, LimitedLifespan.
    readonly record struct GroundStormArrow(int X, int Y, float Rotation)
    {
        public byte[] Blob()
        {
            var w = new BlobWriter();
            w.U8(0).U8(0);
            w.Positioned(X, Y, Rotation, team: 1);
            w.U8(0).U8(0);
            w.U8(0).U8(1);
            w.U8(0);
            return w.ToArray();
        }
    }
}
