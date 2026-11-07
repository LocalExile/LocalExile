using GameServer.Core.World;

static partial class Game
{
    internal const double ControlledFollowerLeash = 60;
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ClientState, ControlledFollowing> FollowerPlans = new();
    sealed class ControlledFollowing
    {
        public bool Following;
        public double RepathAt, AttackUntil, IdleAt = double.NaN, FarSince = double.NaN;
        public (int X, int Y) LeaderAt, LeaderGoal;
        public uint IdleTurn;
        public uint Target;
    }

    internal static (int X, int Y) ControlledFollowerPoint(ClientState conn, ClientState leader, bool arrival = false, double? atTime = null)
    {
        double now = atTime ?? conn.Clock.Elapsed.TotalSeconds;
        var origin = leader.Walk.Position(now);
        var destination = leader.Walk.Destination;
        double dx = destination.X - origin.X, dy = destination.Y - origin.Y;
        if (!leader.Walk.Moving(now) && arrival)
        {
            var entry = conn.Layout.AreaTransitions.Select(p => p.Transition)
                .OrderBy(t => Dist(origin, (t.GridX, t.GridY))).FirstOrDefault();
            if (entry is not null && Dist(origin, (entry.GridX, entry.GridY)) < 50)
                (dx, dy) = (origin.X - entry.GridX, origin.Y - entry.GridY);
        }
        bool forward = arrival || leader.Walk.Moving(now);
        double heading = Math.Atan2(dy, dx) + (forward ? 0 : Math.PI);
        // Try the front/behind formation first, then the other side of a narrow passage. Bodies are live inputs.
        double side = (conn.PeerId % 5 - 2.0) * .22;
        var bodies = conn.Blockers;
        bool Floor(int x, int y) => x >= 0 && y >= 0 && x < conn.Layout.GridW && y < conn.Layout.GridH
            && conn.Layout.WalkableFor(x, y, 2) && !conn.Barrier.Contains((x, y));
        foreach (int radius in forward ? new[] { 30, 24, 18 } : new[] { 16, 22, 12 })
            foreach (double turn in new[] { 0.0, .5, -.5, 1.0, -1.0, 1.5, -1.5, Math.PI })
            {
                double angle = heading + side + turn;
                var at = (X: origin.X + (int)Math.Round(Math.Cos(angle) * radius),
                    Y: origin.Y + (int)Math.Round(Math.Sin(angle) * radius));
                if (!Floor(at.X, at.Y) || ClientPathfinder.ObjectBlocks(bodies, at.X, at.Y, 3, 6)) continue;
                var path = ClientPathfinder.FindPath(origin, at, Floor, conn.Layout.GridW, conn.Layout.GridH, [], 2);
                if (path.Count > 1 && path[^1] == at) return at;
            }
        // No room to form up: stay in the leader's reachable pocket rather than teleport across a wall.
        return origin;
    }

    internal static bool KeepControlledFollowerNear(ClientState conn, ClientState leader, double now, bool externalMovement = false)
    {
        var plan = FollowerPlans.GetOrCreateValue(conn);
        var here = conn.Walk.Position(now);
        var follow = leader.Walk.Position(now);
        double distance = Dist(here, follow);
        if (distance > ControlledFollowerLeash && double.IsNaN(plan.FarSince)) plan.FarSince = now;
        if (distance <= ControlledFollowerLeash) plan.FarSince = double.NaN;
        // Brief gaps are caught on foot; a blink or an obstructed follower cannot stay off screen.
        if (distance > 100 || distance > ControlledFollowerLeash && now - plan.FarSince >= 1.25)
        {
            var at = ControlledFollowerPoint(conn, leader, atTime: now);
            lock (conn.Monsters)
            {
                Interlocked.Increment(ref conn.ActionCount);
                Interlocked.Increment(ref conn.ChannelGeneration);
                conn.ChannelSkill = 0;
                conn.Walk.Teleport(at.X, at.Y, now);
                conn.ControlledNextAction = now + .35;
            }
            plan.Following = false;
            plan.AttackUntil = 0;
            plan.Target = 0;
            plan.FarSince = double.NaN;
            ScheduleControlledIdle(conn, plan, now);
            PublishPlayerPose(conn);
            Console.WriteLine($"[bots] {CharacterName(conn)} returns to leader: {distance:0.0} cells -> ({at.X}, {at.Y})");
            return true;
        }
        // Hysteresis: a moving leader is followed in legs, not by constantly finding a new formation cell.
        bool moving = leader.Walk.Moving(now);
        double ahead = ControlledAhead(here, follow, leader.Walk.Destination);
        if (moving && ahead >= 20 && distance <= ControlledFollowerLeash
            && Math.Max(0, distance * distance - ahead * ahead) <= 20 * 20)
        {
            // Already ahead on the route: wait for the leader rather than running backwards to a formation point.
            if (plan.Following) ScheduleControlledIdle(conn, plan, now);
            plan.Following = false;
            return false;
        }
        if (plan.Following && !moving && distance <= 30)
        {
            plan.Following = false;
            ScheduleControlledIdle(conn, plan, now);
        }
        if (moving || distance > ControlledFollowerLeash) plan.Following = true;
        if (externalMovement && distance <= ControlledFollowerLeash) return plan.Following;
        if (!plan.Following) return false;
        if (now < plan.AttackUntil) return true;
        if (now >= plan.RepathAt && (!conn.Walk.Moving(now) || Dist(plan.LeaderAt, follow) >= 12
            || Dist(plan.LeaderGoal, leader.Walk.Destination) >= 12))
        {
            MoveControlledFollower(conn, ControlledFollowerPoint(conn, leader, atTime: now), now);
            plan.LeaderAt = follow;
            plan.LeaderGoal = leader.Walk.Destination;
            plan.RepathAt = now + .75;
        }
        return true;
    }

    static void ScheduleControlledIdle(ClientState conn, ControlledFollowing plan, double now)
    {
        uint seed = unchecked(conn.PeerId * 2654435761u + plan.IdleTurn * 2246822519u);
        plan.IdleAt = now + 14 + seed % 18;
    }

    internal static void IdleControlledFollower(ClientState conn, ClientState leader, double now)
    {
        var plan = FollowerPlans.GetOrCreateValue(conn);
        plan.Target = 0;
        if (double.IsNaN(plan.IdleAt)) ScheduleControlledIdle(conn, plan, now);
        if (now < plan.IdleAt || now < plan.AttackUntil || conn.Walk.Moving(now) || leader.Walk.Moving(now)) return;
        uint turn = ++plan.IdleTurn;
        ScheduleControlledIdle(conn, plan, now);
        // Most idle decisions leave the bot standing. A rare adjustment stays by its own feet.
        if (turn % 3 != 1) return;
        uint seed = unchecked(conn.PeerId * 2654435761u + turn * 2246822519u);
        var here = conn.Walk.Position(now);
        var bodies = conn.Blockers;
        bool Floor(int x, int y) => x >= 0 && y >= 0 && x < conn.Layout.GridW && y < conn.Layout.GridH
            && conn.Layout.WalkableFor(x, y, 2) && !conn.Barrier.Contains((x, y));
        for (int trial = 0; trial < 8; trial++)
        {
            double angle = (seed % 360) * Math.PI / 180 + trial * Math.PI / 4;
            int radius = 3 + (int)(seed % 5);
            var at = (X: here.X + (int)Math.Round(Math.Cos(angle) * radius),
                Y: here.Y + (int)Math.Round(Math.Sin(angle) * radius));
            if (Dist(at, leader.Walk.Position(now)) > ControlledFollowerLeash || !Floor(at.X, at.Y)
                || ClientPathfinder.ObjectBlocks(bodies, at.X, at.Y, 3, 6)) continue;
            var path = ClientPathfinder.FindPath(here, at, Floor, conn.Layout.GridW, conn.Layout.GridH, bodies, 2);
            if (path.Count < 2 || path[^1] != at) continue;
            // Rounded path cells can briefly leave the circle even with both endpoints inside it.
            var origin = leader.Walk.Position(now);
            if (path.Zip(path.Skip(1)).Any(p => ClientPathfinder.Line(p.First, p.Second)
                .Any(cell => Dist(cell, origin) > ControlledFollowerLeash))) continue;
            MoveControlledFollower(conn, at, now);
            break;
        }
    }

    internal static double ControlledAhead((int X, int Y) bot, (int X, int Y) leader, (int X, int Y) goal)
    {
        double dx = goal.X - leader.X, dy = goal.Y - leader.Y, length = Math.Sqrt(dx * dx + dy * dy);
        return length > 0 ? ((bot.X - leader.X) * dx + (bot.Y - leader.Y) * dy) / length : 0;
    }

    internal static bool ControlledFollowAttackFits(double gap, double leaderSpeed, double seconds, double ahead = 0)
    {
        double after = ahead - Math.Max(0, leaderSpeed) * seconds;
        return gap <= 48 && Math.Max(0, gap * gap - ahead * ahead) + after * after <= 55 * 55;
    }

    internal static void HoldControlledFollowerAttack(ClientState conn, double now, double seconds, uint target)
    {
        var plan = FollowerPlans.GetOrCreateValue(conn);
        plan.Target = target;
        plan.AttackUntil = now + Math.Max(.35, seconds) + .1;
        conn.ControlledNextAction = plan.AttackUntil;
        plan.RepathAt = plan.AttackUntil;
        ScheduleControlledIdle(conn, plan, plan.AttackUntil);
    }

    static void MoveControlledFollower(ClientState conn, (int X, int Y) at, double now)
    {
        lock (conn.Monsters)
        {
            if (conn.Walk.Moving(now) && Dist(conn.Walk.Destination, at) < 6) return;
            if (Dist(conn.Walk.Position(now), at) < 3) return;
            Interlocked.Increment(ref conn.ActionCount);
            Interlocked.Increment(ref conn.ChannelGeneration);
            conn.ChannelSkill = 0;
            conn.Walk.Order(at.X, at.Y, now);
        }
        PublishPlayerPose(conn);
    }
}
