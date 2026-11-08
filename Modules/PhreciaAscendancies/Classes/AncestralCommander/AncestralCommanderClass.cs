namespace GameServer.Modules.PhreciaAscendancies.Classes.AncestralCommander;

/// <summary>The implemented 3.29.3c Phrecia Juggernaut; its stat-driven consumers remain in Game.</summary>
internal static class AncestralCommanderClass
{
    internal static PhreciaClass Descriptor { get; } = new("Juggernaut", "Ancestral Commander", "Game_VERIFIED_ANCESTRAL_COMMANDER",
        () => VerifiedPhreciaGraph.RequireInstalled("Ancestral Commander"));
}
