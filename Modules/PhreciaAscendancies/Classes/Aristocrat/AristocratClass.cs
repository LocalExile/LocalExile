namespace GameServer.Modules.PhreciaAscendancies.Classes.Aristocrat;

/// <summary>The implemented 3.29.3b Phrecia Slayer; its stat-driven consumers remain in Game.</summary>
internal static class AristocratClass
{
    internal static PhreciaClass Descriptor { get; } = new("Slayer", "Aristocrat", "Game_VERIFIED_ARISTOCRAT",
        () => VerifiedPhreciaGraph.RequireInstalled("Aristocrat"));
}
