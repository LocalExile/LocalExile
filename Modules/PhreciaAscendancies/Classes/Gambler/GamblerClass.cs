namespace GameServer.Modules.PhreciaAscendancies.Classes.Gambler;

/// <summary>The implemented 3.29.3b Phrecia Gladiator; its stat-driven consumers remain in Game.</summary>
internal static class GamblerClass
{
    internal static PhreciaClass Descriptor { get; } = new("Gladiator", "Gambler", "Game_VERIFIED_GAMBLER",
        () => VerifiedPhreciaGraph.RequireInstalled("Gambler"));
}
