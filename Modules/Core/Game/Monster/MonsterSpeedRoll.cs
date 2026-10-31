using GameServer.Core.Protocol.Instance;


/// <summary>
/// The client's per-monster movement speed roll (3.29.3b Actor setup 0x141e9ca20; the research branch's
/// re/server-gaps-3.29.3b.md §3). A TinyMT32 state seeded by init_by_array (0x1413cbe50) with the key
/// [object id, the object type's u32 at its data +8] draws a uniform integer percent (0x1404d03d0) in the
/// MonsterVarieties row's range (<see cref="MonsterCatalog.Variety.SpeedRollMin"/>,
/// <see cref="MonsterCatalog.Variety.SpeedRollMax"/>), and the Pathfinding speed is multiplied by 1 + roll / 100.
/// Most rows have an empty range (nothing is drawn, the roll is 0); the Goatmen roll in −5..5, so a base of 8 walks at
/// 7.60–8.40, the band measured live. The server draws the same roll for its walk estimate, so arrival, swing range and
/// contact agree with the client. The type word is taken as the 0x2b9 type hash (the type's register key), which the
/// client stores at that offset by the research branch's reading; the range is certain, the exact value is not yet
/// confirmed live (scripts/re/frida/trace-movement.js prints each monster's seed words, range and multiplier).
/// </summary>
static class MonsterSpeedRoll
{
    /// <summary>The roll in percent for this key: 0 for an empty range (min == max), else uniform in [min, max].</summary>
    public static int Percent(uint objectId, uint typeWord, int min, int max)
    {
        if (min == max) return 0;
        var random = new AnimationVariantSelector.NativeRandom([objectId, typeWord]);
        return random.Uniform(min, max);
    }

    /// <summary>The roll for a monster of this variety created with this object id and 0x2b9 type.</summary>
    public static int Percent(uint objectId, uint type, MonsterCatalog.Variety variety) =>
        Percent(objectId, type, variety.SpeedRollMin, variety.SpeedRollMax);

    /// <summary>The factor on the walk speed: 1 + roll / 100.</summary>
    public static double Factor(int percent) => 1 + percent / 100.0;
}
