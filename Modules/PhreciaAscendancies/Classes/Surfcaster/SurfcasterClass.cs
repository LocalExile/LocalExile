namespace GameServer.Modules.PhreciaAscendancies.Classes.Surfcaster;

/// <summary>The implemented 3.29.3b Phrecia Saboteur; its stat-driven consumers remain in Game.</summary>
internal static class SurfcasterClass
{
    internal static PhreciaClass Descriptor { get; } = new("Saboteur", "Surfcaster", "Game_VERIFIED_SURFCASTER",
        () => VerifiedPhreciaGraph.RequireInstalled("Surfcaster"));
}
