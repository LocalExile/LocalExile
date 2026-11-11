using GameServer.Core.World;

/// <summary>"X% chance to Knock Enemies Back on Hit" (the Tukohama Brawler tattoo, Knockback Support and other
/// sources): pushes the monster away from the player by the base distance, modified by <c>knockback_distance_+%</c>,
/// interrupts the action it had and stores the destination for the next AI tick to present with a Teleport (the client
/// has no knockback animation; the same state the resync and boss teleports use).</summary>
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
    /// states HitReaction and the AI skip), and not carrying the client's ImmuneToKnockback component (King in the
    /// Mists' humanoid, for one).</summary>
    internal static bool KnockbackAllowed(SpawnedMonster m) =>
        m.Hostile && !m.Dead && m.Life > 0 && !m.StageRunning && !m.Scripted && !m.Asleep
        && m.Variety?.ComponentsOrBase.Contains("ImmuneToKnockback") != true;

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

    /// <summary>Rolls "X% chance to Knock Enemies Back on hit" for a player hit that landed on a monster: the
    /// character's and the skill's own chance and distance (Knockback Support's 25-44% chance and 50% increased
    /// distance are skill stats) add. On success the monster is pushed away from the player, its action is interrupted
    /// and <see cref="SpawnedMonster.KnockbackPending"/> carries the cell for the AI tick's packet. Under the Monsters
    /// lock, after the hit's damage.</summary>
    internal static bool KnockbackOnHitLocked(ClientState conn, SpawnedMonster m, IReadOnlyDictionary<uint, int> player,
        IReadOnlyDictionary<uint, int>? skill, double now, Random random)
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
        var from = conn.Walk.Position(now);
        double cells = KnockbackMinimumMetres * CellsPerMetre * Math.Max(0, 1 + Stat(KnockbackDistanceStat) / 100d);
        int size = Math.Max(0, LivingCollision.Size(m));
        if (KnockbackDestination(from, at, cells,
                (x, y) => conn.Layout.WalkableFor(x, y, size) && !conn.Barrier.Contains((x, y))) is not { } to) return false;
        m.Walk.Teleport(to.X, to.Y, now);
        m.KnockbackPending = (to.X, to.Y);
        m.LastTarget = null;
        m.SwingGeneration = null;
        m.ActionGeneration++;
        m.BusyUntil = Math.Max(m.BusyUntil, now + KnockbackDelaySeconds);
        return true;
    }
}
