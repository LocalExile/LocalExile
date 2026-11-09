static partial class Game
{
    /// <summary>Where Flicker Strike takes the player: the monster the client's action picks (FlickerStrike) and the
    /// landing next to it. Null when no monster is in range: the client then strikes where the player stands.</summary>
    internal static (uint Id, (int X, int Y) At, (int X, int Y) Landing)? FlickerDestination(ClientState conn, int effect,
        ushort skillId, (int X, int Y) player, (int X, int Y) aim, uint clicked, double now)
    {
        var stats = SupportedSkill(conn, skillId).Stats;
        int range = FlickerStrike.Range(GolemStat(stats, "flicker_strike_teleport_range_+%"),
            GolemStat(stats, "apply_cut_debuff_on_hit") != 0);
        var monsters = new List<(uint, (int, int), int)>();
        lock (conn.Monsters)
            foreach (var (id, m) in conn.Monsters)
            {
                if (m.Dead || m.Life <= 0 || m.OwnerId is not null || conn.AsleepMonsters.Contains(id)
                    || conn.Instance is not null && !conn.MonsterViews.ContainsKey(id)) continue;
                monsters.Add((id, m.Walk.Position(now), Math.Clamp((m.Variety?.ObjectSize ?? 1) - 1, -1, 127)));
            }
        if (FlickerStrike.Pick(monsters, player, aim, range, clicked) is not { } pick) return null;
        return (pick.Id, pick.At, MovementLanding(conn, effect, player, pick.At));
    }
}
