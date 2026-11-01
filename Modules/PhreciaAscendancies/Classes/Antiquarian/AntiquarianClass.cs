namespace GameServer.Modules.PhreciaAscendancies.Classes.Antiquarian;

/// <summary>The implemented 3.29.3b Phrecia Chieftain; its stat-driven consumers remain in Game.</summary>
internal static class AntiquarianClass
{
    internal static PhreciaClass Descriptor { get; } = new("Chieftain", "Antiquarian", "Game_VERIFIED_ANTIQUARIAN",
        () => VerifiedPhreciaGraph.RequireInstalled("Antiquarian"));
}
