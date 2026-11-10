namespace GameServer.Modules.PhreciaAscendancies.Classes.Harbinger;

// Deliberately not in ImplementedClasses until the parent integrates the runtime after Whisperer.
internal static class HarbingerClass
{
    internal static PhreciaClass Descriptor { get; } = new("Occultist", "Harbinger", "Game_VERIFIED_HARBINGER",
        () => VerifiedPhreciaGraph.RequireInstalled("Harbinger"));

    internal static bool Matches(AscendancyIdentity identity) => Descriptor.Matches(identity);
}
