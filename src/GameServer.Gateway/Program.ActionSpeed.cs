partial class Program
{
    internal static double PlayerMovementSpeed(ClientState conn)
    {
        var stats = CharacterPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        return WalkSpeed * ActionSpeed.From(Get).MovementFactor(Get("movement_velocity_+%"));
    }
}
