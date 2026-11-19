using GameServer.Core.World;

/// <summary>"X% chance to Knock Enemies Back on Hit" (the Tukohama Brawler tattoo, Knockback Support and other
/// sources): pushes the monster away from the player by the base distance, modified by <c>knockback_distance_+%</c>,
/// interrupts the action it had and stores the destination for the next AI tick to present with a Teleport (the same
/// state the resync and boss teleports use).</summary>
static partial class Game
{
    internal const string KnockbackChanceStat = "base_global_chance_to_knockback_%";
    internal const string KnockbackDistanceStat = "knockback_distance_+%";
    // Knockback has a minimum distance of 0.4 metres (poewiki Knockback); a Gateway cell is 0.1 m (10 units per
    // metre, Agents/docs/protocol/11-skills.md), so the shove is at least four cells, scaled by knockback_distance_+%.
    internal const double KnockbackMinimumMetres = .4;
    internal const double CellsPerMetre = 10;
    // The monster stands still for this long after the shove, as on the official server's hit reaction.
    internal const double KnockbackDelaySeconds = .2;

    /// <summary>Whether a monster may be knocked back at all: hostile, alive, not staged, scripted or asleep (the
    /// states HitReaction and the AI skip), and neither carrying the client's ImmuneToKnockback component (King in the
    /// Mists' humanoid, for one) nor naming that mod in its own row (Dominus, Malachai, Avarius, Vilenta … act bosses
    /// that are immune by mod only; BossMods).</summary>
    internal static bool KnockbackAllowed(SpawnedMonster m) =>
        m.Hostile && !m.Dead && m.Life > 0 && !m.StageRunning && !m.Scripted && !m.Asleep
        && !BossMods.KnockbackImmune(m.Variety);

    /// <summary>The push destination: the farthest walkable cell along the direction from <paramref name="from"/>
    /// (the player) to <paramref name="at"/> (the monster), from <paramref name="cells"/> down to 1 away; null when
    /// even the next cell is blocked or the two stand on the same cell.</summary>
    internal static (int X, int Y)? KnockbackDestination((int X, int Y) from, (int X, int Y) at, double cells,
        Func<int, int, bool> walkable)
    {
        double dx = at.X - from.X, dy = at.Y - from.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length <= 0) return null;
        dx /= length; dy /= length;
        for (int step = Math.Max(1, (int)Math.Round(cells)); step >= 1; step--)
        {
            var cell = ((int)Math.Round(at.X + dx * step), (int)Math.Round(at.Y + dy * step));
            if (cell == at || !walkable(cell.Item1, cell.Item2)) continue;
            return cell;
        }
        return null;
    }

    /// <summary>Clears enemies overlapping a Leap Slam landing, independently of the later hit's accuracy, block or damage.
    /// Native 0x1419fe0f0 separates centers by source size + target size + 2; this server queues the destination through
    /// its existing Teleport presentation, without reproducing native knockback speed or animation. Call under the
    /// Monsters lock before damage, with the final skill radius. Only a coincident target uses <paramref name="from"/>
    /// toward <paramref name="landing"/> as its direction; without a nonzero direction it stays in place.</summary>
    public static void LeapSlamDisplaceOccupants(Player conn, (int X, int Y) landing, double radius, double now,
        IReadOnlyDictionary<uint, int>? skill = null, (int X, int Y)? from = null)
    {
        if (!(radius > 0) || !double.IsFinite(radius)) return;
        foreach (var m in conn.Monsters.Values)
        {
            if (!KnockbackAllowed(m) || m.Effects["cannot_be_knocked_back"] > 0) continue;
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
            if (to == at || !conn.Layout.WalkableFor(to.X, to.Y, size) || conn.Barrier.Contains(to)) continue;
            KnockbackCommitLocked(m, to, now);
        }
    }

    /// <summary>Commits a validated knockback destination and invalidates the interrupted action. Call under the
    /// Monsters lock; the next AI tick presents <see cref="SpawnedMonster.KnockbackPending"/>.</summary>
    public static void KnockbackCommitLocked(SpawnedMonster m, (int X, int Y) to, double now)
    {
        m.Walk.Teleport(to.X, to.Y, now);
        m.KnockbackPending = (to.X, to.Y);
        m.LastTarget = null;
        m.SwingGeneration = null;
        m.ActionGeneration++;
        m.BusyUntil = Math.Max(m.BusyUntil, now + KnockbackDelaySeconds);
    }

    /// <summary>Rolls "X% chance to Knock Enemies Back on hit" for a player hit that landed on a monster: the
    /// character's and the skill's own chance and distance (Knockback Support's 25-44% chance and 50% increased
    /// distance are skill stats) add. On success the monster is pushed away from the player, its action is interrupted
    /// and <see cref="SpawnedMonster.KnockbackPending"/> carries the cell for the AI tick's packet. Under the Monsters
    /// lock, after the hit's damage.</summary>
    internal static bool KnockbackOnHitLocked(Player conn, SpawnedMonster m, IReadOnlyDictionary<uint, int> player,
        IReadOnlyDictionary<uint, int>? skill, double now, Random random, (int X, int Y)? origin = null)
    {
        if (!KnockbackAllowed(m)) return false;
        int Stat(string name)
        {
            if (Stats.Key(name) is not uint key) return 0;
            return (int)Math.Clamp((long)player.GetValueOrDefault(key) + (skill?.GetValueOrDefault(key) ?? 0),
                int.MinValue, int.MaxValue);
        }
        if (!RandomRolls.Chance(Stat(KnockbackChanceStat), 100, random)) return false;
        var at = m.Walk.Position(now);
        var from = origin ?? conn.Walk.Position(now);
        double cells = KnockbackMinimumMetres * CellsPerMetre * Math.Max(0, 1 + Stat(KnockbackDistanceStat) / 100d);
        int size = Math.Max(0, LivingCollision.Size(m));
        if (KnockbackDestination(from, at, cells,
                (x, y) => conn.Layout.WalkableFor(x, y, size) && !conn.Barrier.Contains((x, y))) is not { } to) return false;
        KnockbackCommitLocked(m, to, now);
        return true;
    }
}
