namespace GameServer.Modules.HeistCache;

/// <summary>
/// A Smuggler's Cache's values, each with where it comes from. [measured] is one captured cache (a level 70 map
/// area); [data] is a client table. The placement spacing is the Strongboxes' own, reused, not re-measured: the
/// capture is one cache, so no placement distribution of caches is known.
/// </summary>
public static class HeistCacheTables
{
    /// <summary>The cache's metadata path [measured: the captured type]. Its murmur2 is 0x6858ece4, checked against
    /// <c>InstanceEntities.ObjectTypeHash</c> (MurmurHash2, seed 0).</summary>
    public const string Path = "Metadata/Chests/LeagueHeist/HeistSmugglersCoinCacheOutdoors";

    /// <summary>The captured type hash: murmur2(<see cref="Path"/>) [measured].</summary>
    public const uint TypeHash = 0x6858ece4;

    /// <summary>A cache's Positioned object size is 2 and it blocks [data: object_types, positioned_object_size 2,
    /// positioned_blocking true].</summary>
    public const int BoxSize = 2;

    /// <summary>The least distance in cells between two caches: the Strongboxes' own number, reused. The arrival and
    /// transition clearance are <c>AreaPopulation.EncounterSpot</c>'s own.</summary>
    public const int PlaceApart = 60;
}
