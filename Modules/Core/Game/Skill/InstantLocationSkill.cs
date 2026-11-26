using GameServer.Core.World;


/// <summary>Ordinary Frostblink's distance calculation and clear endpoint admission.
/// Invalid endpoints still fail closed instead of guessing the native fallback search.</summary>
public static class InstantLocationSkill
{
    public static bool Supported(SkillCatalog skills, int effect) =>
        skills.EffectId(effect) is "Frostblink" or "FrostblinkHardMode" && skills.IsInstant(effect) && skills.IsMovement(effect);

    public static bool ObjectTargetSupported(SkillCatalog skills, int effect) =>
        Supported(skills, effect) || skills.IsInstant(effect) && !skills.IsMovement(effect);

    // 0x141c010a0: the object route extends the target (or its 32-cell minimum) by both Positioned sizes + 2.
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

    // 0x141c00cd0 (IceDash: 32/42) / 0x141d39ea0 (FlameDash: 25/60): max distance truncates
    // baseMaxRange * (100 + travelDistancePct) / 100; 0x1406722c0 rounds each normalized component with floor(x + .5).
    public static (int X, int Y)? Endpoint((int X, int Y) from, (uint X, uint Y) aim, int travelDistancePct,
        int minRange = 32, int baseMaxRange = 42)
    {
        if (aim.X > int.MaxValue || aim.Y > int.MaxValue) return null;
        long numerator = (long)baseMaxRange * (100L + travelDistancePct);
        if (numerator < 0 || numerator > int.MaxValue) return null; // Native signed multiplication must not wrap.
        long maximum = numerator / 100;
        // Do not guess the native min/max interaction for unsupported extreme distance reductions.
        if (maximum < minRange || maximum > int.MaxValue) return null;
        float dx = (long)aim.X - from.X, dy = (long)aim.Y - from.Y;
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        if (!(distance > 0) || !float.IsFinite(distance)) return null;
        if (distance >= minRange && distance <= maximum) return ((int)aim.X, (int)aim.Y);
        float scale = (distance < minRange ? minRange : (float)maximum) / distance;
        double x = from.X + (double)MathF.Floor(dx * scale + .5f);
        double y = from.Y + (double)MathF.Floor(dy * scale + .5f);
        if (x < 0 || y < 0 || x > int.MaxValue || y > int.MaxValue) return null;
        return ((int)x, (int)y);
    }

    // Called again under the send gate: an old request cannot move the player after transfer/death/pause.
    // 0x141c00cd0 accepts a valid endpoint via 0x141c006d0 before running its fallback terrain trace.
    public static bool TryResolve(Player player, int epoch, (uint X, uint Y) aim,
        bool inTown, Func<uint, int> stat, out (int X, int Y) from, out (int X, int Y) to)
    {
        from = player.Walk.Position(Game.ProjectileWorldTime(player));
        return TryResolveFrom(player, epoch, from, aim, inTown, stat, out to);
    }

    static bool TryResolveFrom(Player player, int epoch, (int X, int Y) from, (uint X, uint Y) aim,
        bool inTown, Func<uint, int> stat, out (int X, int Y) to)
    {
        to = default;
        if (!player.ProjectileActive(epoch) || !player.Connection.PlayerSent || player.PlayerLife == 0 || player.Paused
            || player.PendingTransfer is not null || inTown || stat(Stat.YourMovementSkillsAreDisabled) != 0
            || player.Layout.Walkable is not { } walkable || player.Layout.GridW <= 0 || player.Layout.GridH <= 0)
            return false;
        if (aim.X >= player.Layout.GridW || aim.Y >= player.Layout.GridH) return false;
        return TryResolveLanding(player, from, ((int)aim.X, (int)aim.Y),
            stat(Stat.SkillMaximumTravelDistancePlusPct), allowTerrainFallback: false, out to);
    }

    /// <summary>Resolves an IceDash (<c>0x141c00cd0</c> / <c>0x141c006d0</c> / <c>0x141c04830</c> / <c>0x141c00440</c>,
    /// 32/42-cell clamp) or FlameDash (<c>0x141d39ea0</c> / <c>0x141d399a0</c> / <c>0x141d3b790</c> / <c>0x141d39830</c>,
    /// 25/60-cell clamp) landing from <paramref name="from"/> toward <paramref name="aim"/>: range clamp, layer-1 sight
    /// and solid-barrier line validation (crossing walk-only doors like prison grates while refusing solid walls),
    /// forward fallback before solid walls when direct travel fails, monster blocker slide, and the native minimum
    /// displacement threshold (<c>dx² + dy² >= 25</c>).</summary>
    public static bool TryResolveLanding(Player player, (int X, int Y) from, (int X, int Y) aim,
        int travelDistancePct, bool allowTerrainFallback, out (int X, int Y) to,
        int minRange = 32, int baseMaxRange = 42)
    {
        to = default;
        if (player.Layout.Walkable is not { } walkable || player.Layout.GridW <= 0 || player.Layout.GridH <= 0)
            return false;
        if (aim.X < 0 || aim.Y < 0) return false;
        int clampedX = Math.Clamp(aim.X, 0, player.Layout.GridW - 1);
        int clampedY = Math.Clamp(aim.Y, 0, player.Layout.GridH - 1);
        if (Endpoint(from, ((uint)clampedX, (uint)clampedY), travelDistancePct, minRange, baseMaxRange) is not { } endpoint)
            return false;
        var blockers = player.Blockers;
        var walkBarrier = player.Barrier;
        var sightBarrier = player.SightBarrier;
        var sight = player.Layout.Sight;
        bool Allowed(int x, int y) => x >= 0 && y >= 0 && x < player.Layout.GridW && y < player.Layout.GridH
            && walkable(x, y) && !walkBarrier.Contains((x, y));
        bool Blocked(int x, int y)
        {
            for (int i = 0; i < blockers.Count; i++)
            {
                var b = blockers[i];
                if (Math.Max(Math.Abs((long)x - b.X), Math.Abs((long)y - b.Y)) - b.Size < 2)
                    return true;
            }
            return false;
        }
        bool ClearSightLine((int X, int Y) target)
        {
            // 0x141c006d0 -> 0x141d8c6d0(..., 1) -> 0x141d98830: every cell along the line must have layer-1 sight >= 1
            // and no solid/quest sight barrier. Walk-only doors (prison grates) remain passable.
            foreach (var cell in ClientPathfinder.Line(from, target))
            {
                if (sightBarrier.Contains(cell)) return false;
                if (sight is not null && sight(cell.X, cell.Y) < 1) return false;
            }
            return true;
        }
        // 0x141c00440 (IceDashClient::CanExecute): requires dx² + dy² > 24 (>= 5 cells) or the client rejects 0x01ce.
        bool FarEnough((int X, int Y) target)
        {
            long dx = (long)target.X - from.X, dy = (long)target.Y - from.Y;
            return dx * dx + dy * dy >= 25;
        }
        if (!Allowed(from.X, from.Y)) return false;
        bool directAllowed = Allowed(endpoint.X, endpoint.Y);
        bool directClear = directAllowed && ClearSightLine(endpoint);
        if (!directClear)
        {
            // Unlike Dash's 0x141b43d90 (0x141b43eea breaks on layer 1 < 2, 0x141b43f78 records layer 0 >= 2),
            // IceDash's 0x141c04830 checks the layers in reverse order: 0x141c0498a (r8 = 0) aborts the trace as soon
            // as layer 0 < 2 (!Allowed, including walk-only grates and chasms when the direct endpoint failed), while
            // 0x141c04a1c (r8 = 1) skips recording cells with layer 1 == 1 and updates the landing only where
            // layer 1 >= 2. 0x141c00440 then re-checks dx^2 + dy^2 >= 25 and 0x141c006d0 (layer 1 >= 1 along the line),
            // so sight == 0 or a SightBarrier also terminates the trace instead of being skipped over.
            if (!allowTerrainFallback && sight is null) return false;
            (int X, int Y)? terrain = null;
            foreach (var cell in ClientPathfinder.Line(from, endpoint))
            {
                if (sightBarrier.Contains(cell) || sight is not null && sight(cell.X, cell.Y) < 1) break;
                if (cell == from) continue;
                if (!Allowed(cell.X, cell.Y)) break;
                if (sight is not null && sight(cell.X, cell.Y) < 2) continue;
                if (!Blocked(cell.X, cell.Y)) terrain = cell;
            }
            if (terrain is not { } valid || !FarEnough(valid) || !ClearSightLine(valid)) return false;
            to = valid;
            return true;
        }
        if (Blocked(endpoint.X, endpoint.Y))
        {
            // Aiming onto or beside a monster's footprint should slide to the nearest clear cell around it rather
            // than silently dropping the blink.
            int maxBlockingSize = 0;
            for (int i = 0; i < blockers.Count; i++)
            {
                var b = blockers[i];
                if (Math.Max(Math.Abs((long)endpoint.X - b.X), Math.Abs((long)endpoint.Y - b.Y)) - b.Size < 2
                    && b.Size > maxBlockingSize)
                    maxBlockingSize = b.Size;
            }
            int maxRadius = Math.Clamp(maxBlockingSize + 3, 8, 32);
            (int X, int Y)? fallback = null;
            long bestDistSq = long.MaxValue;
            for (int r = 1; r <= maxRadius && fallback is null; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                        int cx = endpoint.X + dx, cy = endpoint.Y + dy;
                        if (!Allowed(cx, cy) || Blocked(cx, cy) || !FarEnough((cx, cy))) continue;
                        long distSq = (long)dx * dx + (long)dy * dy;
                        if (distSq >= bestDistSq) continue;
                        if (!ClearSightLine((cx, cy))) continue;
                        bestDistSq = distSq;
                        fallback = (cx, cy);
                    }
                }
            }
            if (fallback is not { } clear) return false;
            to = clear;
            return true;
        }
        if (!FarEnough(endpoint)) return false;
        to = endpoint;
        return true;
    }

    // Object 0x1ce binds the entity again on delivery.
    // Only the native clear-grid trace is supported: partial terrain searches and coincident RNG fail closed.
    public static bool TryResolveObject(Player player, int epoch, uint targetId, bool inTown,
        Func<uint, int> stat, out (int X, int Y) from, out (int X, int Y) to, out (int X, int Y) aim)
    {
        double now = Game.ProjectileWorldTime(player);
        from = player.Walk.Position(now); to = aim = default;
        if (!player.Area.Monsters.TryGetValue(targetId, out var target) || target.Dead || target.Life == 0
            || target.Variety is not { ObjectSize: >= 1 and <= 128 } variety) return false;
        aim = target.Walk.Position(now);
        if (ObjectAim(from, aim, 1, variety.ObjectSize - 1) is not { } trace
            || !TryResolveFrom(player, epoch, from, ((uint)trace.End.X, (uint)trace.End.Y), inTown, stat, out to)) return false;
        // 0x141c04ae0 retains the last valid Bresenham candidate. Requiring a wholly clear extension avoids
        // guessing its two terrain-layer thresholds or the outer native terrain fallback on partial traces.
        foreach (var cell in ClientPathfinder.Line(trace.Start, trace.End))
            if (cell.X < 0 || cell.Y < 0 || cell.X >= player.Layout.GridW || cell.Y >= player.Layout.GridH
                || player.Layout.Walkable!(cell.X, cell.Y) == false || player.Barrier.Contains(cell)) return false;
        return Math.Max(Math.Abs((long)trace.End.X - aim.X), Math.Abs((long)trace.End.Y - aim.Y))
            - (variety.ObjectSize - 1) >= 1; // Native candidate must be outside the target's footprint.
    }

    public static bool TryCommitObject(Player player, int epoch, uint targetId, bool inTown,
        Func<uint, int> stat, out (int X, int Y) aim, out (int X, int Y) to)
    {
        using (Game.ProjectileMutation(player))
        {
            if (!TryResolveObject(player, epoch, targetId, inTown, stat, out _, out to, out aim)) return false;
            player.Walk.Teleport(to.X, to.Y, Game.ProjectileWorldTime(player));
            return true;
        }
    }

    // Native ground 0x1ce keeps the current/predicted action at entry, then Frostblink's reposition ends its old walk.
    // Mirror that position change without incrementing ActionCount or cancelling a cast/channel's pending release.
    public static bool TryCommit(Player player, int epoch, (uint X, uint Y) aim, bool inTown,
        Func<uint, int> stat, out (int X, int Y) to)
    {
        using (Game.ProjectileMutation(player))
        {
            if (!TryResolve(player, epoch, aim, inTown, stat, out _, out to)) return false;
            player.Walk.Teleport(to.X, to.Y, Game.ProjectileWorldTime(player));
            return true;
        }
    }
}
