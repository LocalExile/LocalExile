using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using ClientPathfinder = GameServer.Core.World.ClientPathfinder;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // How far around the player GGG lists monsters in a resync (23.4 cells at most).
    public const int ResyncRadius = 24;

    // The monsters around the player where the server has them, each by its own teleport (S→C 0x1b7, terrain height).
    // The player is not among them: the client's position is the truth and the server's walk only estimates it, so S→C
    // 0x128 (whose first record the client puts the player on) snapped the player back to the estimate at every stun,
    // freeze, movement skill and resync request. Called under conn.Monsters.
    //
    // A lockstep client gets none. GGG's resync is S→C 0x128, which that client skips (07 §6), and its monsters already
    // stand where the orders put them. A teleport there zeroes each walker's step accumulator (0x141e34b40) while the
    // server's walk keeps its fraction of a step, so every later order of those walks found the client a cell behind.
    public static byte[] ResyncPacketLocked(Player player, double now)
    {
        if (player.Connection.LockstepClient) return [];
        var playerAt = player.Walk.Position(now);
        var bytes = new List<byte>();
        foreach (var (id, m) in player.Area.Monsters)
        {
            if (m.Dead || m.Life <= 0 || m.Asleep) continue;
            var at = m.Walk.Position(now);
            if (Dist(at, playerAt) <= ResyncRadius) bytes.AddRange(InstanceProtocol.Teleport(id, at.X, at.Y, 0x7fffffff));
        }
        return [.. bytes];
    }


    // ActiveSkillType Movement also covers these, which leave the player where it stands when used: the estimate must stay
    // too, or a resync would teleport the client to the aimed cell.
    static readonly string[] MovementInPlace = ["MirrorArrow", "SmokeMine", "PhaseRun", "ChainHook", "WitheringStep", "Ambush"];

    /// <summary>Whether a skill immediately puts the player where it was aimed. A channelled one (Cyclone) reports its own way.</summary>
    public static bool LandsAtAim(Player player, int effect) =>
        SkillsFor(player).IsMovement(effect) && !SkillsFor(player).IsChannelled(effect)
        && !(SkillsFor(player).EffectId(effect) is { } id && (IsLightningWarpEffect(id) || id is "FrostblinkAltX" or CorpseBodyswapEffect
            || IsConsecratedPath(id) || id.StartsWith("BlinkArrow", StringComparison.Ordinal)
            || MovementInPlace.Any(name => id.StartsWith(name, StringComparison.Ordinal))));

    public static bool IsFlameDashEffect(string? effectId) =>
        effectId is "FlameDash" or "FlameDashAltX" or "FlameDashAltY" or "FlameDashHardMode" or "FlameDashRoyale";

    internal static double FlameDashGroundSeconds(Player player, ushort skillId, int effect,
        IReadOnlyDictionary<uint, int>? own = null, IReadOnlyDictionary<uint, int>? playerStats = null)
    {
        own ??= SupportedSkill(player, skillId).Stats;
        playerStats ??= CharacterPlayerStats(player);
        int baseMs = GolemStat(own, Stat.BaseSkillEffectDuration);
        return SkillDurationModifiers.Seconds(baseMs > 0 ? baseMs : 4000,
            k => GolemStat(playerStats, k), k => GolemStat(own, k), DurationKinds(player, effect));
    }

    /// <summary>Builds Flame Dash's S→C 0x2c8 burning-ground segment update (reader 0x141478930 → 0x1414786c0):
    /// four i32 LE grid coordinates (x1, y1, x2, y2) and f32 LE duration in seconds.</summary>
    public static byte[] FlameDashGroundSegment(Player player, ushort skillId, int effect, ushort sequence,
        (int X, int Y) from, (int X, int Y) to)
    {
        double seconds = FlameDashGroundSeconds(player, skillId, effect);
        ushort actionTypeHash = SkillsFor(player).ActionHash(effect) ?? 0x5ccd;
        byte[] tail = new BlobWriter().I32(from.X).I32(from.Y).I32(to.X).I32(to.Y).F32((float)seconds).ToArray();
        return InstanceProtocol.ActionUpdate(player.Id, actionTypeHash, skillId, sequence, tail);
    }

    public static (int X, int Y) MovementLanding(Player player, int effect, (int X, int Y) from, (int X, int Y) aim) =>
        MovementLanding(player, effect, from, aim, 0, 0);

    /// <summary>Admits Flame Dash inside the cooldown critical section only when its resolved landing moves the player
    /// (<c>landing != from</c>, matching client <c>0x141d39830</c>'s <c>dx^2 + dy^2 &gt;= 25</c> check before spending a
    /// charge), and hands the resolved timing and landing to <paramref name="onAccepted"/> so rejected casts never
    /// debit cooldowns, bump <c>ActionCount</c>, or acknowledge <c>0x01ab</c>/<c>0x02bc</c>.</summary>
    internal static bool TryBeginFlameDashUse(Player player, ushort skillId, int effect, ushort flags,
        double requestedAt, Func<double, (int X, int Y)> resolveAim,
        Action<double, (int X, int Y), (int X, int Y), (int X, int Y)> onAccepted, out CooldownUseTicket? ticket)
    {
        double acceptedAt = 0;
        (int X, int Y) from = default, aim = default, landing = default;
        return TryBeginSkillCooldownUse(player, skillId, effect, CooldownForSkill(player, skillId), false,
            () => onAccepted(acceptedAt, from, aim, landing), out ticket, requestedAt: requestedAt,
            admit: () =>
            {
                bool lockstep = player.Connection.LockstepClient;
                acceptedAt = lockstep && player.Connection.LockstepFrame is { } frame ? frame.Start
                    : Math.Max(requestedAt, ProjectileWorldTime(player));
                if (lockstep) StepWalkers(player, acceptedAt);
                from = player.Walk.Position(acceptedAt);
                aim = resolveAim(acceptedAt);
                landing = MovementLanding(player, effect, from, aim, skillId, flags);
                return landing != from;
            });
    }

    static (int X, int Y) MovementLanding(Player player, int effect, (int X, int Y) from, (int X, int Y) aim, ushort skill, ushort flags)
    {
        string? id = SkillsFor(player).EffectId(effect);
        if (player.Layout.Walkable is not { } floor) return IsFlameDashEffect(id) ? from : aim;
        bool walkable(int x, int y) => floor(x, y) && !player.Barrier.Contains((x, y));
        if (id == "FrostblinkAltX")
        {
            const uint distanceStat = Stat.SkillMaximumTravelDistancePlusPct;
            int travel = GolemStat(CharacterPlayerStats(player), distanceStat);
            if (skill != 0) travel += GolemStat(SupportedSkill(player, skill).Stats, distanceStat);
            return InstantLocationSkill.TryResolveLanding(player, from, aim, travel, allowTerrainFallback: true, out var landing)
                ? landing : from;
        }
        if (IsFlameDashEffect(id))
        {
            // 3.29.3c 0x141d39ea0 / 0x141d39d10 / 0x141d399a0 / 0x141d3b790 / 0x141d39830: Flame Dash clamps to
            // [25, 60 * (100 + travel + maxTravel) / 100], checks layer-1 sight along direct travel, falls back with
            // the same layer-0-abort / layer-1-record trace as IceDash, and requires dx^2 + dy^2 >= 25.
            return FlameDashLanding(player, from, aim, skill, minRange: 25);
        }
        if (id == QuickstepEffect)
            return QuickstepLanding(player, from, aim, (flags & 1) != 0);
        if (id is "Dash" or "DashHardMode" or "DashRoyale")
        {
            const uint distanceStat = Stat.SkillTravelDistancePlusPct;
            int travel = GolemStat(CharacterPlayerStats(player), distanceStat);
            if (skill != 0) travel += GolemStat(SupportedSkill(player, skill).Stats, distanceStat);
            return DashLanding(player.Layout, player.Barrier, from, aim, travel, (flags & 1) != 0,
                size: 1, sightBarriers: player.SightBarrier);
        }
        if (IsWhirlingBlades(id) && aim != from)
        {
            const int cells = 40;
            double f = cells / Math.Sqrt((double)(aim.X - from.X) * (aim.X - from.X) + (double)(aim.Y - from.Y) * (aim.Y - from.Y));
            var end = ((int)Math.Round(from.X + (aim.X - from.X) * f), (int)Math.Round(from.Y + (aim.Y - from.Y) * f));
            var last = from;
            foreach (var cell in ClientPathfinder.Line(from, end).Skip(1))
            {
                if (!walkable(cell.X, cell.Y)) break;
                last = cell;
            }
            return last;
        }
        var sight = player.Layout.Sight;
        var sightBarrier = player.SightBarrier;
        var landed = from;
        foreach (var cell in ClientPathfinder.Line(from, aim))
        {
            if (sightBarrier.Contains(cell) || sight is not null && sight(cell.X, cell.Y) < 1) break;
            if (sight is null && cell != from && !walkable(cell.X, cell.Y)) break;
            if (walkable(cell.X, cell.Y)) landed = cell;
        }
        return landed;
    }

    // Dash crosses open gaps. Only the landing needs walking clearance. If the direct landing fails,
    // the native fallback keeps the last standable cell until its stricter sight test fails (07 §5).
    // The thresholds are a size-1 player's (07 §5); a bigger walker (a size-2 Rogue Exile's Dash) needs its own size of
    // standing room at the end and along the line, and one more of clearance for the fallback: Ainsley Varrich's
    // stopped 4-5 cells short of the size-1 landing at both ends of an open line (sync trace 2026-11-17).
    public static (int X, int Y) DashLanding(AreaLayout layout, IReadOnlySet<(int X, int Y)> barriers,
        (int X, int Y) from, (int X, int Y) aim, int travelPercent, bool reverse, int size = 1) =>
        DashLanding(layout, barriers, from, aim, travelPercent, reverse, size, barriers);

    public static (int X, int Y) DashLanding(AreaLayout layout, IReadOnlySet<(int X, int Y)> barriers,
        (int X, int Y) from, (int X, int Y) aim, int travelPercent, bool reverse, int size,
        IReadOnlySet<(int X, int Y)> sightBarriers)
    {
        size = Math.Max(1, size);
        if (from == aim) return from;
        float dx = (float)aim.X - from.X, dy = (float)aim.Y - from.Y;
        long distance = Math.Max(0L, 30L * (100L + travelPercent) / 100);
        float scale = distance / MathF.Sqrt(dx * dx + dy * dy);
        int direction = reverse ? -1 : 1;
        var end = (X: from.X + direction * (int)MathF.Floor(dx * scale + .5f),
                   Y: from.Y + direction * (int)MathF.Floor(dy * scale + .5f));
        bool floor(int x, int y) => x >= 0 && y >= 0 && x < layout.GridW && y < layout.GridH
            && layout.WalkableFor(x, y, size) && !barriers.Contains((x, y));
        var last = from;
        if (layout.Sight is not { } sight)
        {
            foreach (var cell in ClientPathfinder.Line(from, end))
            {
                if (!floor(cell.X, cell.Y)) break;
                last = cell;
            }
            return last;
        }

        bool direct = floor(end.X, end.Y);
        bool fallback = true;
        foreach (var cell in ClientPathfinder.Line(from, end))
        {
            int clearance = sight(cell.X, cell.Y);
            if (clearance < size || sightBarriers.Contains(cell)) return last;
            fallback &= clearance >= size + 1;
            if (fallback && floor(cell.X, cell.Y)) last = cell;
            if (!fallback && !direct) break;
        }
        return direct ? end : last;
    }

    // HardQuickDodge 0x141ba12c0 projects a fixed 30 cells, independent of Dash's travel stat.
    // Its 0x141ba17b0 trace can cross terrain with layer-1 clearance but retains only standable cells.
    static (int X, int Y) QuickstepLanding(Player player, (int X, int Y) from, (int X, int Y) aim, bool reverse)
    {
        if (from == aim) return from;
        const int distance = 30;
        float dx = (float)aim.X - from.X, dy = (float)aim.Y - from.Y;
        float scale = distance / MathF.Sqrt(dx * dx + dy * dy);
        int direction = reverse ? -1 : 1;
        var end = (from.X + direction * (int)MathF.Floor(dx * scale + .5f),
            from.Y + direction * (int)MathF.Floor(dy * scale + .5f));
        double now = ProjectileWorldTime(player);
        var enemies = player.Area.MonstersNear(from, distance + 128)
            .Where(pair => QuickstepEnemy(pair.Value))
            .Select(pair => (At: pair.Value.Walk.Position(now), Size: LivingCollision.Size(pair.Value)))
            .ToArray();
        var layout = player.Layout;
        var sight = layout.Sight;
        var wall = player.SightBarrier;
        var last = from;
        foreach (var cell in ClientPathfinder.Line(from, end).Skip(1))
        {
            if (cell.X < 0 || cell.Y < 0 || cell.X >= layout.GridW || cell.Y >= layout.GridH
                || wall.Contains(cell) || sight is not null && sight(cell.X, cell.Y) < 1
                || enemies.Any(enemy => Math.Max(Math.Abs((long)cell.X - enemy.At.X),
                    Math.Abs((long)cell.Y - enemy.At.Y)) - enemy.Size < 2)) break;
            if (layout.WalkableFor(cell.X, cell.Y, 1) && !player.Barrier.Contains(cell)) last = cell;
            else if (sight is null) break;
        }
        // HardQuickDodge::CanExecute (0x141ba0f90) refuses displacements of fewer than five cells.
        int x = last.X - from.X, y = last.Y - from.Y;
        return x * x + y * y >= 25 ? last : from;
    }

    static bool QuickstepEnemy(SpawnedMonster enemy) =>
        enemy is { Hostile: true, Dead: false, Life: > 0, OwnerId: null, WorldHidden: false };

    // Resolve Dash through the current terrain-aware landing before payment and commit it in the same
    // admission transaction: a blocked endpoint or a stale action cannot displace the player without a paid use.
    public static bool TryDashLanding(Player player, int effect, (int X, int Y) aim,
        out (int X, int Y) from, out (int X, int Y) to, double? acceptedAt = null, ushort skill = 0, ushort flags = 0)
    {
        to = from = default;
        if (!IsDashMovementEffect(SkillsFor(player).EffectId(effect)) || !SkillsFor(player).IsTravel(effect)
            || !SkillsFor(player).IsMovement(effect) || !player.ProjectileActive(player.ProjectileEpoch)
            || !player.Connection.PlayerSent || player.PlayerLife == 0 || player.Paused || player.Closed || player.LeavingArea
            || player.RageRetired || player.PendingTransfer is not null || InTown(player)
            || player.Layout.Walkable is not { } walkable || player.Layout.GridW <= 0 || player.Layout.GridH <= 0)
            return false;
        double now = acceptedAt ?? ProjectileWorldTime(player);
        from = player.Walk.Position(now);
        // Bound the raw client aim before MovementLanding subtracts coordinates or projects the native distance.
        if (aim == from || aim.X < 0 || aim.Y < 0 || aim.X >= player.Layout.GridW || aim.Y >= player.Layout.GridH
            || from.X < 0 || from.Y < 0 || from.X >= player.Layout.GridW || from.Y >= player.Layout.GridH
            || !walkable(from.X, from.Y) || player.Barrier.Contains(from)) return false;
        var landing = MovementLanding(player, effect, from, aim, skill, flags);
        to = landing;
        if (landing == from || landing.X < 0 || landing.Y < 0 || landing.X >= player.Layout.GridW
            || landing.Y >= player.Layout.GridH || !walkable(landing.X, landing.Y) || player.Barrier.Contains(landing))
            return false;
        return !player.Blockers.Any(b => Math.Max(Math.Abs((long)landing.X - b.X), Math.Abs((long)landing.Y - b.Y))
            - b.Size < 2);
    }

    static (int X, int Y) FlameDashLanding(Player player, (int X, int Y) from, (int X, int Y) aim,
        ushort skill, int minRange)
    {
        var stats = CharacterPlayerStats(player);
        int travel = GolemStat(stats, Stat.SkillTravelDistancePlusPct) + GolemStat(stats, Stat.SkillMaximumTravelDistancePlusPct);
        if (skill != 0)
        {
            var own = SupportedSkill(player, skill).Stats;
            travel += GolemStat(own, Stat.SkillTravelDistancePlusPct) + GolemStat(own, Stat.SkillMaximumTravelDistancePlusPct);
        }
        return InstantLocationSkill.TryResolveLanding(player, from, aim, travel, allowTerrainFallback: true,
            out var landing, minRange: minRange, baseMaxRange: 60) ? landing : from;
    }

    /// <summary>The least time after a movement spell before the resync goes out.</summary>
    const double MovementResyncSeconds = .5;

    /// <summary>How long a movement skill's action lasts on the client: its attack time, or its cast time.</summary>
    public static double MovementSeconds(Player player, int effect, int level, ushort skillId = 0) =>
        SkillsFor(player).IsAttack(effect) ? skillId == 0 ? SkillAttackSeconds(player, effect, level) : SupportedAttackSeconds(player, skillId)
            : Math.Max(MovementResyncSeconds, SkillsFor(player).CastTimeMs(effect) / 1000.0 / PlayerActionFactor(player));

    // The server has the player landed while the client is on its way there, so nothing resyncs until the action is over.
    public static void ResyncAfter(Player player, double seconds, string why)
    {
        player.LandingUntil = Math.Max(player.LandingUntil, AreaTime(player) + seconds);
        SendAfter(player, seconds, () => SendResync(player, why));
    }

    // Something to send later, on its own: the connection may be gone by then.
    public static void SendAfter(Player player, double seconds, Func<Task> send) =>
        _ = AreaFrames.Start(async () =>
        {
            await AreaDelay(player, seconds);
            try { await send(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        });

    public static Task SendResync(Player player, string why) =>
        Send(player, [], $"0x0128 resync ({why})", atGate: () =>
        {
            return player.Closed || player.LeavingArea || player.Connection.LockstepClient || AreaTime(player) < player.LandingUntil ? null
                : ResyncPacketLocked(player, AreaTime(player));
        });
}
