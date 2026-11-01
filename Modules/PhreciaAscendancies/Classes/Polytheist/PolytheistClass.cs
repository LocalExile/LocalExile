namespace GameServer.Modules.PhreciaAscendancies.Classes.Polytheist;

/// <summary>The implemented 3.29.3b Phrecia Guardian; its stat-driven consumers remain in Game.</summary>
internal static class PolytheistClass
{
    internal static PhreciaClass Descriptor { get; } = new("Guardian", "Polytheist", "Game_VERIFIED_POLYTHEIST",
        () => VerifiedPhreciaGraph.RequireInstalled("Polytheist"));
}
