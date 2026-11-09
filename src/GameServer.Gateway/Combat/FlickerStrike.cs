/// <summary>
/// Flicker Strike (ActionTypes <c>FlickerStrike</c>, HASH16 0x6352): the monster it goes to, as the client's action picks
/// it (<c>0x141a5fa50</c>). That is the clicked monster when it is in range, else the monster in range nearest to the
/// aimed point, else none. In range means that the distance from the player, less the monster's size, is at most
/// 40 cells × (1 + <c>flicker_strike_teleport_range_+%</c> / 100), or 60 with <c>apply_cut_debuff_on_hit</c>
/// (<c>0x141a60380</c>, <c>0x141a60180</c>). The client's action never moves the player itself (traced live
/// 2026-11-09), so the server teleports it.
/// </summary>
static class FlickerStrike
{
    public const ushort Action = 0x6352;

    /// <summary>The farthest a target may be, in cells (the client truncates the product).</summary>
    public static int Range(int teleportRangePct, bool cutDebuff) =>
        (int)((teleportRangePct + 100) / 100f * (cutDebuff ? 60 : 40));

    /// <summary>Whether a monster of this size at <paramref name="target"/> is within <paramref name="range"/> of the player.</summary>
    public static bool InRange((int X, int Y) player, (int X, int Y) target, int size, int range)
    {
        float dx = target.X - player.X, dy = target.Y - player.Y;
        float d = MathF.Sqrt(dx * dx + dy * dy);
        if (size > 0) d = MathF.Max(d - size, 0);
        return d * d <= (float)(range * range);
    }

    /// <summary>The hostile monster Flicker Strike goes to, or null when none is in range.</summary>
    public static (uint Id, (int X, int Y) At)? Pick(IEnumerable<(uint Id, (int X, int Y) At, int Size, bool Hostile)> monsters,
        (int X, int Y) player, (int X, int Y) aim, int range, uint clicked = 0)
    {
        (uint Id, (int X, int Y) At)? best = null;
        long bestDistance = 0;
        foreach (var (id, at, size, hostile) in monsters)
        {
            // Only hostile monsters, a clicked one included: the client's pick checks the factions (0x141a603e0, 0x141a61c00).
            if (!hostile || !InRange(player, at, size, range)) continue;
            if (clicked != 0 && id == clicked) return (id, at);
            long dx = at.X - aim.X, dy = at.Y - aim.Y, distance = dx * dx + dy * dy;
            // The client keeps a later monster at the same distance (<=).
            if (best is null || distance <= bestDistance) (best, bestDistance) = ((id, at), distance);
        }
        return best;
    }
}
