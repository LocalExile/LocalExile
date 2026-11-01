namespace GameServer.Modules.PhreciaAscendancies.Classes.Paladin;

/// <summary>The implemented 3.29.3b Phrecia Champion; its stat-driven consumers remain in Game.</summary>
internal static class PaladinClass
{
    internal static PhreciaClass Descriptor { get; } = new("Champion", "Paladin", "Game_VERIFIED_PALADIN",
        () => VerifiedPhreciaGraph.RequireInstalled("Paladin"));
}
