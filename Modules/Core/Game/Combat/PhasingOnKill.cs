using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    /// <summary>The charm's "X% chance to gain Phasing for 4 seconds on Kill": one roll per kill, resetting the
    /// connection's timed phasing (<see cref="ClientState.IsPhasing"/>). Phasing already keeps monsters from blocking
    /// the player and answers the <c>_while_phasing</c> item conditions.</summary>
    public static void GainPhasingOnKill(ClientState conn, double now)
    {
        if (Stats.Key("phasing_for_4_seconds_on_kill_%") is not uint key) return;
        int chance = CharacterPlayerStats(conn).GetValueOrDefault(key);
        if (chance <= 0 || Random.Shared.Next(100) >= Math.Min(100, chance)) return;
        lock (conn.Monsters)
            conn.PhasingUntil = Math.Max(conn.PhasingUntil, now + 4);
    }
}
