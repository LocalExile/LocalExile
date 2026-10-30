partial class Program
{
    internal static double PlayerMovementSpeed(ClientState conn)
    {
        var stats = CharacterStatSources(conn).Stats;
        foreach (var (key, value) in conn.PlayerStats) stats[key] = value;
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        return WalkSpeed * ActionSpeed.From(Get).MovementFactor(conn.Pools.MovementVelocityPct);
    }
}
