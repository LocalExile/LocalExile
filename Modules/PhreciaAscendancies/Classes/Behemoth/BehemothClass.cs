namespace GameServer.Modules.PhreciaAscendancies.Classes.Behemoth;

/// <summary>The implemented Phrecia Juggernaut. Its node sources use the shared stat-driven combat engines.</summary>
internal static class BehemothClass
{
    internal static PhreciaClass Descriptor { get; } = new("Juggernaut", "Behemoth", "Game_VERIFIED_BEHEMOTH",
        () => VerifiedPhreciaGraph.RequireInstalled("Behemoth"));
}
