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

    /// <summary>The components the client reads for this type, in order [data: object_types rows of the base cache and
    /// its ramparts variant]: Positioned, BaseEvents, Animated, Chest, Stats, ObjectMagicProperties (5),
    /// InteractionAction, MinimapIcon. There is no StateMachine segment: component 7 is MinimapIcon, so a
    /// Strongbox-style StateMachine update would land on the MinimapIcon.</summary>
    public const byte ChestComponent = 3, MagicPropertiesComponent = 5, MinimapIconComponent = 7;

    /// <summary>The ObjectMagicProperties sent with the open: u16 flags 0x0008, 00, 00. The cache's own create carries
    /// the same four bytes [measured]; the open's come from another Heist chest's open, so they are a transplant
    /// there. It is not the normal identified form (0x18): a cache is never identified.</summary>
    public static readonly byte[] OpenMagicProperties = [0x08, 0x00, 0x00, 0x00];

    /// <summary>A cache's Positioned object size is 2 and it blocks [data: object_types, positioned_object_size 2,
    /// positioned_blocking true].</summary>
    public const int BoxSize = 2;

    /// <summary>The least distance in cells between two caches: the Strongboxes' own number, reused. The arrival and
    /// transition clearance are <c>AreaPopulation.EncounterSpot</c>'s own.</summary>
    public const int PlaceApart = 60;
}
