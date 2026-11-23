namespace GameServer.Modules.PhreciaAscendancies.Classes.Whisperer;

/// <summary>The catalog's Phrecia Deadeye; reusable consumers belong to Game.</summary>
internal static class WhispererClass
{
    internal static PhreciaClass Descriptor { get; } = new("Deadeye", "Whisperer", "Game_VERIFIED_WHISPERER",
        () => VerifiedPhreciaGraph.RequireInstalled("Whisperer"));
}
