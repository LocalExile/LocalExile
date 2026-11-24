using GameServer.Core.World;

/// <summary>"X% chance to Knock Enemies Back on Hit" (the Tukohama Brawler tattoo, Knockback Support and other
/// sources): pushes the monster away from the player by the base distance, modified by <c>knockback_distance_+%</c>,
/// interrupts the action it had and stores the destination for the next AI tick to present with a Teleport (the same
/// state the resync and boss teleports use).</summary>
static partial class Game
{
    internal const uint KnockbackChanceStat = Stat.BaseGlobalChanceToKnockbackPct;
    internal const uint KnockbackDistanceStat = Stat.KnockbackDistancePlusPct;
    internal const uint KnockbackReversedStat = Stat.EnemyKnockbackDirectionIsReversed;
    // Native 0x1af knockback presentation (Client/Game.exe 0x141e9828b -> 0x141e9d030):
    // flags bit 11 (0x0800) invokes Actor::ApplyKnockback with kind bits 0x04 (u3c speed %) and 0x08 (u3e distance %).
    internal const ushort ActionHitKnockbackFlag = 0x0800;
    internal const byte ActionHitKnockbackKindMask = 0x0c;
    // Knockback has a minimum distance of 0.4 metres (poewiki Knockback); a Gateway cell is 0.1 m (10 units per
    // metre, Agents/docs/protocol/11-skills.md), so the shove is at least four cells, scaled by knockback_distance_+%.
    internal const double KnockbackMinimumMetres = .4;
    internal const double CellsPerMetre = 10;
    // The monster stands still for this long after the shove, as on the official server's hit reaction.
    internal const double KnockbackDelaySeconds = .2;

    /// <summary>Whether a monster may be knocked back at all: hostile, alive, not staged, scripted or asleep (the
    /// states HitReaction and the AI skip), not carrying the client's ImmuneToKnockback component (King in the
    /// Mists' humanoid, for one) nor a mod in its own row whose stats say nothing may shove it (Dominus, Malachai,
    /// Avarius, Vilenta … act bosses that are immune by mod only; BossMods), and not granted
    /// <c>cannot_be_knocked_back</c> by a mod it was spawned or upgraded with.</summary>
    internal static bool KnockbackAllowed(SpawnedMonster m) =>
        m.Hostile && !m.Dead && m.Life > 0 && !m.StageRunning && !m.Scripted && !m.Asleep
        && m.Variety is not ({ MovementSpeed: 0 } or { Immobile: true })
        && !BossMods.KnockbackImmune(m.Variety) && m.Effects[Stat.CannotBeKnockedBack] <= 0;

    /// <summary>The push (or pull, when <paramref name="reversed"/> is set) destination: walks step-by-step from 1 up
    /// to <paramref name="cells"/> along the knockback direction, stopping at the first unwalkable/barrier cell so the
    /// monster never tunnels through walls; reversed knockback clamps pull distance before overlapping
    /// <paramref name="from"/>.</summary>
    internal static (int X, int Y)? KnockbackDestination((int X, int Y) from, (int X, int Y) at, double cells,
        Func<int, int, bool> walkable, bool reversed = false, int minSeparation = 0)
    {
        double dx = at.X - from.X, dy = at.Y - from.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= 0) return null;
        dx /= length; dy /= length;
        if (reversed)
        {
            dx = -dx; dy = -dy;
            double maxPull = Math.Max(0, length - Math.Max(1, minSeparation));
            cells = Math.Min(cells, maxPull);
            if (cells < 0.5) return null;
        }
        else if (cells <= 0) return null;
        int maxStep = Math.Max(1, (int)Math.Round(cells));
        (int X, int Y)? best = null;
        for (int step = 1; step <= maxStep; step++)
        {
            var cell = ((int)Math.Round(at.X + dx * step), (int)Math.Round(at.Y + dy * step));
            if (cell == at) continue;
            if (!walkable(cell.Item1, cell.Item2)) break;
            best = cell;
        }
        return best;
    }

    /// <summary>Clears enemies overlapping a Leap Slam landing, independently of the later hit's accuracy, block or damage.
    /// Native 0x1419fe0f0 separates centers by source size + target size + 2; this server queues the destination through
    /// its existing Teleport presentation, without reproducing native knockback speed or animation. Call under the
    /// Monsters lock before damage, with the final skill radius. Only a coincident target uses <paramref name="from"/>
    /// toward <paramref name="landing"/> as its direction; without a nonzero direction it stays in place.</summary>
    public static void LeapSlamDisplaceOccupants(Player player, (int X, int Y) landing, double radius, double now,
        IReadOnlyDictionary<uint, int>? skill = null, (int X, int Y)? from = null)
    {
        if (!(radius > 0) || !double.IsFinite(radius)) return;
        foreach (var m in player.Area.Monsters.Values)
        {
            if (!KnockbackAllowed(m)) continue;
            var at = m.Walk.Position(now);
            double dx = (double)at.X - landing.X, dy = (double)at.Y - landing.Y;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            int size = Math.Max(0, LivingCollision.Size(m));
            double separation = LivingCollision.PlayerSize + size + 2;
            if (distance > radius || distance >= separation) continue;
            double directionLength = distance;
            if (directionLength == 0)
            {
                if (from is not { } origin) continue;
                dx = (double)landing.X - origin.X;
                dy = (double)landing.Y - origin.Y;
                directionLength = Math.Sqrt(dx * dx + dy * dy);
                if (directionLength == 0) continue;
            }
            var to = (X: (int)Math.Round(landing.X + dx / directionLength * separation),
                Y: (int)Math.Round(landing.Y + dy / directionLength * separation));
            if (to == at || !player.Layout.WalkableFor(to.X, to.Y, size) || player.Barrier.Contains(to)) continue;
            KnockbackCommitLocked(m, to, now);
        }
    }

    /// <summary>Commits a validated knockback destination and invalidates the interrupted action. Call under the
    /// Monsters lock; the next AI tick presents <see cref="SpawnedMonster.KnockbackPending"/>.</summary>
    public static void KnockbackCommitLocked(SpawnedMonster m, (int X, int Y) to, double now)
    {
        m.Walk.Teleport(to.X, to.Y, now);
        m.KnockbackPending = (to.X, to.Y);
        m.NativeKnockbackPending = null;
        m.LastTarget = null;
        m.SwingGeneration = null;
        m.ActionGeneration++;
        m.BusyUntil = Math.Max(m.BusyUntil, now + KnockbackDelaySeconds);
    }

    /// <summary>Encodes a validated knockback move into native 0x1af <c>angle</c>, <c>u3c</c> (speed %) and
    /// <c>u3e</c> (distance %) fields matching <c>0x141e9d030</c>:
    /// <c>distance = sign * max(0, (u3e * 0.01 + 1) * 4 * (attackerSize + 5) / (targetSize + 5))</c>.</summary>
    internal static SpawnedMonster.NativeKnockback NativeKnockbackFor((int X, int Y) at, (int X, int Y) to,
        int targetSize, bool reversed = false, int attackerSize = LivingCollision.PlayerSize)
    {
        int dx = to.X - at.X, dy = to.Y - at.Y;
        double actualDist = Math.Sqrt((double)dx * dx + (double)dy * dy);
        double sizeRatio = (attackerSize + 5.0) / (Math.Max(0, targetSize) + 5.0);
        double baseDist = KnockbackMinimumMetres * CellsPerMetre * sizeRatio;
        short distanceMod = baseDist > 0
            ? (short)Math.Clamp(Math.Round((actualDist / baseDist - 1.0) * 100.0), short.MinValue, short.MaxValue)
            : (short)0;
        byte angle = reversed ? HitAngle(-dx, -dy) : HitAngle(dx, dy);
        return new(angle, 0, distanceMod);
    }

    /// <summary>Takes a pending native knockback under the Monsters lock and clears the Teleport fallback so the hit's
    /// 0x1af packet presents the knockback instead of snapping later via 0x0f8.</summary>
    internal static SpawnedMonster.NativeKnockback? TakeNativeKnockbackLocked(SpawnedMonster m)
    {
        if (m.NativeKnockbackPending is not { } kb) return null;
        m.NativeKnockbackPending = null;
        m.KnockbackPending = null;
        return kb;
    }

    internal static void ApplyNativeKnockback(SpawnedMonster.NativeKnockback? knockback, ref ushort flags, ref byte kind,
        ref byte angle, out ushort u3c, out ushort u3e)
    {
        if (knockback is not { } kb) { u3c = 0; u3e = 0; return; }
        flags |= ActionHitKnockbackFlag;
        kind |= ActionHitKnockbackKindMask;
        angle = kb.Angle;
        u3c = unchecked((ushort)kb.SpeedMod);
        u3e = unchecked((ushort)kb.DistanceMod);
    }

    internal static void ConsumeNativeKnockbackLocked(SpawnedMonster m, ref ushort flags, ref byte kind,
        ref byte angle, out ushort u3c, out ushort u3e) =>
        ApplyNativeKnockback(TakeNativeKnockbackLocked(m), ref flags, ref kind, ref angle, out u3c, out u3e);

    /// <summary>Rolls "X% chance to Knock Enemies Back on hit" for a player hit that landed on a monster: the
    /// character's and the skill's own chance and distance (Knockback Support's 25-44% chance and 50% increased
    /// distance are skill stats) add. On success the monster is pushed away from the player, its action is interrupted,
    /// <see cref="SpawnedMonster.NativeKnockbackPending"/> carries the native 0x1af presentation for hits that send
    /// ActionHit and <see cref="SpawnedMonster.KnockbackPending"/> falls back to the AI tick's Teleport for hits that
    /// do not. Under the Monsters lock, after the hit's damage.</summary>
    internal static bool KnockbackOnHitLocked(Player player, SpawnedMonster m, IReadOnlyDictionary<uint, int> playerStats,
        IReadOnlyDictionary<uint, int>? skill, double now, Random random, (int X, int Y)? origin = null,
        int attackerSize = LivingCollision.PlayerSize, bool attackerHasReversedStat = true)
    {
        if (!KnockbackAllowed(m)) return false;
        int Stat(uint name)
        {
            if (!(name is var key)) return 0;
            return (int)Math.Clamp((long)playerStats.GetValueOrDefault(key) + (skill?.GetValueOrDefault(key) ?? 0),
                int.MinValue, int.MaxValue);
        }
        if (Stat(global::Stat.GlobalKnockback) == 0 && !RandomRolls.Chance(Stat(KnockbackChanceStat), 100, random)) return false;
        var at = m.Walk.Position(now);
        var from = origin ?? player.Walk.Position(now);
        bool reversed = Stat(KnockbackReversedStat) != 0;
        double cells = KnockbackMinimumMetres * CellsPerMetre * Math.Max(0, 1 + Stat(KnockbackDistanceStat) / 100d);
        int size = Math.Max(0, LivingCollision.Size(m));
        int effAttackerSize = Math.Max(0, attackerSize);
        int minSeparation = reversed ? Math.Max(1, effAttackerSize + size + 1) : 0;
        if (KnockbackDestination(from, at, cells,
                (x, y) => player.Layout.WalkableFor(x, y, size) && !player.Barrier.Contains((x, y)),
                reversed: reversed, minSeparation: minSeparation) is not { } to) return false;
        KnockbackCommitLocked(m, to, now);
        m.NativeKnockbackPending = NativeKnockbackFor(at, to, size,
            reversed: reversed && attackerHasReversedStat, attackerSize: effAttackerSize);
        return true;
    }
}
