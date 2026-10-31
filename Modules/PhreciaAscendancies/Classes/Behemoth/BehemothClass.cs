namespace GameServer.Modules.PhreciaAscendancies.Classes.Behemoth;

/// <summary>The implemented Phrecia Berserker. Its node sources use the shared stat-driven combat engines.</summary>
internal static class BehemothClass
{
    internal static PhreciaClass Descriptor { get; } = new("Berserker", "Behemoth", "Game_VERIFIED_BEHEMOTH",
        () => VerifiedPhreciaGraph.RequireInstalled("Behemoth"));
}
