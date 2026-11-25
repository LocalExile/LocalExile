using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const double SnakingFissureSpeed = 150;
    const int SnakingFissureRange = 60;
    const double SnakingFissureContinuation = .33;
    // The VolcanicSlam contact creates the travelling action as the second detached action.
    const byte SnakingFissureDetachId = 2;

    static void ScheduleSnakingFissureLocked(Player player, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, RetainedAreaAttack source,
        (int X, int Y) from, (int X, int Y) aim, double now, ushort actionHash, ushort sequence, int repeat,
        double radiusMore, SkillCriticalRoll criticalRoll, double ruthlessMore, bool cryExerted)
    {
        var session = ProjectilesFor(player);
        var stats = SkillAreaStats(player, source.Gem.Stats, gem.Effect);
        int Get(uint stat) => stats.GetValueOrDefault(stat);
        int body = (int)Math.Floor(SkillArea.Radius(10, Get, spell: false) * radiusMore);
        int blast = (int)Math.Floor(SkillArea.Radius(14, Get, spell: false) * radiusMore);
        double speed = SnakingFissureSpeed * Math.Max(.01, 1 + Get(Stat.VolcanicFissureSpeedPlusPct) / 100d);
        int additional = Math.Max(0, Get(Stat.VaalVolcanicFissureCrackRepeatCount));
        bool Current() => session.Active && player.PlayerLife != 0 && !player.LeavingArea && !player.Closed;
        bool Walkable((int X, int Y) point) =>
            player.Layout.WalkableFor(point.X, point.Y, 0) && !player.Barrier.Contains(point);
        bool CanReach((int X, int Y) from, (int X, int Y) target) =>
            player.Layout.Sight is not { } sight || !ClientPathfinder.LineBelow(from, target, sight, 1);
        bool Eligible(SpawnedMonster monster) =>
            monster is { Hostile: true, Dead: false, Life: > 0, OwnerId: null, WorldHidden: false };

        (int X, int Y) End((int X, int Y) start, (int X, int Y) target)
        {
            var direction = GroundSlamWave.Direction(start, target);
            if (Dist(start, target) > SnakingFissureRange)
                target = ((int)Math.Round(start.X + direction.X * SnakingFissureRange),
                    (int)Math.Round(start.Y + direction.Y * SnakingFissureRange));

            var end = start;
            foreach (var point in ClientPathfinder.Line(start, target).Skip(1))
            {
                if (!Walkable(point)) break;
                end = point;
            }
            return end;
        }

        void Hit((int X, int Y) at, int radius, List<uint> targets, Action<uint>? onHit, string phase)
        {
            if (targets.Count == 0) return;
            var hit = AreaSkillHitStrike(player, skillId, gem, at, at, immediate: true, sequence,
                actionToken: null, cooldownUse: null, melee: true, damageMore: 1, excludeMonster: 0, criticalRoll,
                channelGeneration: null, targetId: 0, repeat, radiusOverride: radius, once: null, repeatDelay: 0,
                radiusMore: 1, sacrifice: null, swingAt: null, triggeredCurrent: Current, discharge: null,
                strikeIndex: 0, firstContactAt: null, ruthlessMore, priorTargets: null, scaledBlastChaos: 0,
                singleStrikeTarget: null, onHit, scheduledSession: session, cryExerted, prepaidRawExtra: 0, baseChaos: 0,
                hammer: null, retainedAttack: source with { Phase = phase }, allowMeleeSplash: false, contactTargets: targets);
            if (!hit.IsCompleted) throw new InvalidOperationException("A scheduled fissure contact must not yield.");
            hit.GetAwaiter().GetResult();
        }

        void Update(IReadOnlyList<(int X, int Y)> path, double length)
        {
            var writer = new BlobWriter().U8((byte)path.Count);
            foreach (var point in path)
                writer.F32((float)((point.X + .5) * WorldUnits.PerCell)).F32((float)((point.Y + .5) * WorldUnits.PerCell));
            if (path.Count != 0) writer.F32((float)(length * WorldUnits.PerCell));
            session.World(player.Id, InstanceProtocol.ActionUpdate(player.Id, actionHash, skillId, sequence, writer.ToArray(),
                detachId: SnakingFissureDetachId),
                $"0x02c8 Snaking Fissure at {(path.Count == 0 ? "end" : path[^1].ToString())}");
        }

        void Continue((int X, int Y) at, int remaining, HashSet<uint> lastBlast)
        {
            if (!Current()) return;
            double time = ProjectileWorldTime(player);
            var end = at;
            double best = SnakingFissureRange + 1;
            foreach (var (id, monster) in player.Area.MonstersNear(at, SnakingFissureRange))
            {
                if (!Eligible(monster) || monster.Invulnerable || lastBlast.Contains(id)) continue;
                var position = monster.Walk.Position(time);
                double distance = Dist(at, position);
                if (distance == 0 || distance >= best || distance > SnakingFissureRange || End(at, position) != position) continue;
                end = position;
                best = distance;
            }
            // Where the chain finds no enemy, it still spends its remaining eruptions in the surrounding terrain.
            if (end == at)
            {
                double angle = AreaRng(player).NextDouble() * Math.Tau;
                double distance = Math.Min(SnakingFissureRange, Math.Max(18, blast + 8));
                for (int direction = 0; direction < 8 && end == at; direction++, angle += Math.Tau / 8)
                    end = End(at, (at.X + (int)Math.Round(Math.Cos(angle) * distance),
                        at.Y + (int)Math.Round(Math.Sin(angle) * distance)));
            }
            if (end == at)
            {
                Update([], 0);
                return;
            }
            Begin(at, end, remaining, first: false);
        }

        void Begin((int X, int Y) start, (int X, int Y) end, int remaining, bool first)
        {
            double direct = Dist(start, end);
            var points = new List<(int X, int Y)>(3) { start };
            if (direct >= 12)
            {
                var direction = GroundSlamWave.Direction(start, end);
                double bend = Math.Min(5, direct / 8) * (AreaRng(player).Next(2) == 0 ? 1 : -1);
                var middle = ((int)Math.Round((start.X + end.X) / 2d - direction.Y * bend),
                    (int)Math.Round((start.Y + end.Y) / 2d + direction.X * bend));
                if (End(start, middle) == middle && End(middle, end) == end) points.Add(middle);
            }
            points.Add(end);
            var vertices = points.Select(ProjectileSession.Vector).ToArray();
            double length = 0;
            for (int i = 1; i < vertices.Length; i++) length += (vertices[i] - vertices[i - 1]).Length;
            if (!first) Update(points, length);

            var contacted = new HashSet<uint>();
            var hitOnPath = new HashSet<uint>();
            long startTime = Math.Max(session.Runtime.Now, ProjectileRuntime.Time(now));
            long legStart = startTime;
            var previous = vertices[0];
            int leg = 1;

            void Advance()
            {
                if (!Current()) return;
                var legVector = vertices[leg] - vertices[leg - 1];
                double legLength = legVector.Length;
                long legEnd = legStart + ProjectileRuntime.Time(legLength / speed);
                long at = session.Runtime.Now;
                double fraction = legEnd <= legStart ? 1 : Math.Clamp((double)(at - legStart) / (legEnd - legStart), 0, 1);
                var next = vertices[leg - 1] + legVector * fraction;
                double time = ProjectileWorldTime(player);
                var centre = (previous + next) * .5;
                var direction = (next - previous).Unit;
                double travel = (next - previous).Length;
                var atCell = ((int)Math.Round(next.X), (int)Math.Round(next.Y));
                List<uint> hits = [];
                foreach (var (id, monster) in player.Area.MonstersNear((centre.X, centre.Y), body + travel / 2))
                {
                    if (!Eligible(monster) || contacted.Contains(id)) continue;
                    var position = monster.Walk.Position(time);
                    var local = ProjectileSession.Vector(position) - previous;
                    double along = Math.Clamp(local.Dot(direction), 0, travel);
                    if ((local - direction * along).Length <= body + Math.Max(0, LivingCollision.Size(monster))
                        && CanReach(atCell, position) && contacted.Add(id))
                        hits.Add(id);
                }
                Hit(atCell, body, hits, id => hitOnPath.Add(id), "fissure path");
                previous = next;

                if (at < legEnd)
                {
                    session.Runtime.Schedule(Math.Min(legEnd, at + ProjectileRuntime.TickQuanta), Advance);
                    return;
                }
                if (++leg < vertices.Length)
                {
                    legStart = at;
                    session.Runtime.Schedule(at + Math.Min(ProjectileRuntime.TickQuanta,
                        ProjectileRuntime.Time((vertices[leg] - next).Length / speed)), Advance);
                    return;
                }
                var lastBlast = new HashSet<uint>();
                List<uint> targets = [];
                foreach (var (id, monster) in player.Area.MonstersNear(end, blast))
                {
                    if (!Eligible(monster)) continue;
                    var position = monster.Walk.Position(time);
                    if (Dist(end, position) > blast + Math.Max(0, LivingCollision.Size(monster)) || !CanReach(end, position)) continue;
                    // The path and its eruption share the leg's contacts. A path hit still inside the
                    // eruption belongs to its exclusion set for the next target search.
                    if (hitOnPath.Contains(id)) lastBlast.Add(id);
                    if (contacted.Add(id)) targets.Add(id);
                }
                Hit(end, blast, targets, id => lastBlast.Add(id), "fissure eruption");
                if (remaining > 0)
                    session.Runtime.Schedule(at + ProjectileRuntime.Time(SnakingFissureContinuation),
                        () => Continue(end, remaining - 1, lastBlast));
            }

            session.Runtime.Schedule(startTime + Math.Min(ProjectileRuntime.TickQuanta,
                ProjectileRuntime.Time((vertices[1] - vertices[0]).Length / speed)), Advance);
        }

        Begin(from, End(from, aim), additional, first: true);
    }
}
