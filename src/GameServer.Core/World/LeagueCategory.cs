namespace GameServer.Core.World;

/// <summary>
/// The league content category an instance is played in: S→C 0x1d's u8 at +0x28, which the client stores at world +0xd0
/// (setter <c>0x142149550</c>). 1–59 are LeagueCategoryToDisplayName ids; 60 (0x3c) is "no league content". The client
/// reads it for its topology pick (Topologies +0x2c, <c>0x14202e270</c>), the World panel's SpecialMapPins <c>__col5</c>
/// and the object types' <c>league_category</c>. Agents/docs/re/011-level-generation.md §3.
/// </summary>
public static class LeagueCategory
{
    public const int None = 60;

    /// <summary><paramref name="value"/> when it is a category the client knows (1–60), else <see cref="None"/>.</summary>
    public static int Clamp(int? value) => value is >= 1 and <= None ? value.Value : None;
}
