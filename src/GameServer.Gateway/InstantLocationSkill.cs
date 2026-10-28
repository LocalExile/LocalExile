using GameServer.Core.World;

/// <summary>The bounded instant movement path: ordinary Frostblink's distance calculation on a captured grid.
/// Terrain endpoint searches, other movement skills and Frostblink's damage/chilled ground are not modelled here.</summary>
static class InstantLocationSkill
{
    public static bool Supported(SkillCatalog skills, int effect) =>
        skills.EffectId(effect) == "Frostblink" && skills.IsInstant(effect) && skills.IsMovement(effect);

    public static bool ObjectTargetSupported(SkillCatalog skills, int effect) =>
        Supported(skills, effect) || skills.IsInstant(effect) && !skills.IsMovement(effect);

    // 0x141bfe610: the object route extends the target (or its 32-cell minimum) by both Positioned sizes + 2.
    // The returned inclusive trace precedes the ordinary 32/42-cell clamp. Coincident targets use native seeded RNG.
    public static ((int X, int Y) Start, (int X, int Y) End)? ObjectAim(
        (int X, int Y) from, (int X, int Y) target, int playerSize, int targetSize)
    {
        if (playerSize is < 0 or > 127 || targetSize is < 0 or > 127) return null;
        long dx = (long)target.X - from.X, dy = (long)target.Y - from.Y;
        if (dx is < int.MinValue or > int.MaxValue || dy is < int.MinValue or > int.MaxValue) return null;
        float distance = MathF.Sqrt((float)dx * dx + (float)dy * dy);
        if (!(distance > 0) || !float.IsFinite(distance)) return null;
        (int X, int Y)? Offset((int X, int Y) start, long x, long y, float scale)
        {
            double nx = start.X + (double)MathF.Floor(x * scale + .5f);
            double ny = start.Y + (double)MathF.Floor(y * scale + .5f);
            return nx < 0 || ny < 0 || nx > int.MaxValue || ny > int.MaxValue ? null : ((int)nx, (int)ny);
        }
        var start = distance < 32 ? Offset(from, dx, dy, 32 / distance) : target;
        if (start is not { } first) return null;
        dx = (long)first.X - from.X; dy = (long)first.Y - from.Y;
        distance = MathF.Sqrt((float)dx * dx + (float)dy * dy);
        if (Offset(first, dx, dy, (playerSize + targetSize + 2) / distance) is not { } last) return null;
        return (first, last);
    }

    // 0x141bfe240: max distance truncates 42 * (100 + skill_maximum_travel_distance_+%) / 100;
    // min distance is float 32 at 0x1436a3888. 0x140671d00 rounds each normalized component with floor(x + .5).
    public static (int X, int Y)? Endpoint((int X, int Y) from, (uint X, uint Y) aim, int travelDistancePct)
    {
        if (aim.X > int.MaxValue || aim.Y > int.MaxValue) return null;
        long numerator = 42L * (100L + travelDistancePct);
        if (numerator < 0 || numerator > int.MaxValue) return null; // Native signed multiplication must not wrap.
        long maximum = numerator / 100;
        // Do not guess the native min/max interaction for unsupported extreme distance reductions.
        if (maximum < 32 || maximum > int.MaxValue) return null;
        float dx = (long)aim.X - from.X, dy = (long)aim.Y - from.Y;
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        if (!(distance > 0) || !float.IsFinite(distance)) return null;
        if (distance >= 32 && distance <= maximum) return ((int)aim.X, (int)aim.Y);
        float scale = (distance < 32 ? 32 : (float)maximum) / distance;
        double x = from.X + (double)MathF.Floor(dx * scale + .5f);
        double y = from.Y + (double)MathF.Floor(dy * scale + .5f);
        if (x < 0 || y < 0 || x > int.MaxValue || y > int.MaxValue) return null;
        return ((int)x, (int)y);
    }

    // Called again under the send gate: an old request cannot move the player after transfer/death/pause.
    // A fully walkable straight route is deliberately stricter than the client's blink terrain endpoint search.
    public static bool TryResolve(ClientState conn, int epoch, (uint X, uint Y) aim,
        bool inTown, Func<string, int> stat, out (int X, int Y) from, out (int X, int Y) to)
    {
        lock (conn.Monsters)
        {
            from = conn.Walk.Position(Program.ProjectileWorldTime(conn));
            return TryResolveFrom(conn, epoch, from, aim, inTown, stat, out to);
        }
    }

    static bool TryResolveFrom(ClientState conn, int epoch, (int X, int Y) from, (uint X, uint Y) aim,
        bool inTown, Func<string, int> stat, out (int X, int Y) to)
    {
        to = default;
        if (!conn.ProjectileActive(epoch) || !conn.PlayerSent || conn.PlayerLife == 0 || conn.Paused
            || conn.PendingTransfer is not null || inTown || stat("your_movement_skills_are_disabled") != 0
            || conn.Layout.Walkable is not { } walkable || conn.Layout.GridW <= 0 || conn.Layout.GridH <= 0)
            return false;
        if (aim.X >= conn.Layout.GridW || aim.Y >= conn.Layout.GridH) return false;
        if (Endpoint(from, aim, stat("skill_maximum_travel_distance_+%")) is not { } endpoint) return false;
        bool Allowed(int x, int y) => x >= 0 && y >= 0 && x < conn.Layout.GridW && y < conn.Layout.GridH
            && walkable(x, y) && !conn.Barrier.Contains((x, y));
        if (!Allowed(from.X, from.Y) || !Allowed(endpoint.X, endpoint.Y)) return false;
        if (conn.Blockers.Any(b => Math.Max(Math.Abs((long)endpoint.X - b.X), Math.Abs((long)endpoint.Y - b.Y))
            - b.Size < 2)) return false; // Player Positioned size 1: retain its ordinary placement clearance.
        var previous = from;
        foreach (var cell in ClientPathfinder.Line(from, endpoint))
        {
            if (!Allowed(cell.X, cell.Y)) return false;
            if (cell.X != previous.X && cell.Y != previous.Y
                && (!Allowed(previous.X, cell.Y) || !Allowed(cell.X, previous.Y))) return false;
            previous = cell;
        }
        to = endpoint;
        return true;
    }

    // Object 0x1ce binds the entity again on delivery. Reject active target walks rather than claim moving-target parity.
    // Only the native clear-grid trace is supported: partial terrain searches and coincident RNG fail closed.
    public static bool TryResolveObject(ClientState conn, int epoch, uint targetId, bool inTown,
        Func<string, int> stat, out (int X, int Y) from, out (int X, int Y) to, out (int X, int Y) aim)
    {
        lock (conn.Monsters)
        {
            double now = Program.ProjectileWorldTime(conn);
            from = conn.Walk.Position(now); to = aim = default;
            if (!conn.Monsters.TryGetValue(targetId, out var target) || target.Dead || target.Life == 0
                || target.Variety is not { ObjectSize: >= 1 and <= 128 } variety || target.Walk.ArrivesAt > now) return false;
            aim = target.Walk.Position(now);
            if (ObjectAim(from, aim, 1, variety.ObjectSize - 1) is not { } trace
                || !TryResolveFrom(conn, epoch, from, ((uint)trace.End.X, (uint)trace.End.Y), inTown, stat, out to)) return false;
            // 0x141c02050 retains the last valid Bresenham candidate. Requiring a wholly clear extension avoids
            // guessing its two terrain-layer thresholds or the outer native terrain fallback on partial traces.
            foreach (var cell in ClientPathfinder.Line(trace.Start, trace.End))
                if (cell.X < 0 || cell.Y < 0 || cell.X >= conn.Layout.GridW || cell.Y >= conn.Layout.GridH
                    || conn.Layout.Walkable!(cell.X, cell.Y) == false || conn.Barrier.Contains(cell)) return false;
            return Math.Max(Math.Abs((long)trace.End.X - aim.X), Math.Abs((long)trace.End.Y - aim.Y))
                - (variety.ObjectSize - 1) >= 1; // Native candidate must be outside the target's footprint.
        }
    }

    public static bool TryCommitObject(ClientState conn, int epoch, uint targetId, bool inTown,
        Func<string, int> stat, out (int X, int Y) aim, out (int X, int Y) to)
    {
        using (Program.ProjectileMutation(conn))
        {
            if (!TryResolveObject(conn, epoch, targetId, inTown, stat, out _, out to, out aim)) return false;
            conn.Walk.Teleport(to.X, to.Y, Program.ProjectileWorldTime(conn));
            return true;
        }
    }

    // Native ground 0x1ce keeps the current/predicted action at entry, then Frostblink's reposition ends its old walk.
    // Mirror that position change without incrementing ActionCount or cancelling a cast/channel's pending release.
    public static bool TryCommit(ClientState conn, int epoch, (uint X, uint Y) aim, bool inTown,
        Func<string, int> stat, out (int X, int Y) to)
    {
        using (Program.ProjectileMutation(conn))
        {
            if (!TryResolve(conn, epoch, aim, inTown, stat, out _, out to)) return false;
            conn.Walk.Teleport(to.X, to.Y, Program.ProjectileWorldTime(conn));
            return true;
        }
    }
}
