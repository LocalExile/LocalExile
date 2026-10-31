using GameServer.Core.Protocol.Instance;


/// <summary>
/// The glow of magic and rare monsters: hidden BuffDefinitions rows the server puts on the monster, whose BuffVisuals
/// draw it (row 66 <c>monster_magic_effect_buff</c>, BuffVisuals 58: <c>champions/blue/blue.epk</c>; row 67
/// <c>monster_rare_effect_buff</c>, BuffVisuals 59: <c>champions/gold/gold.epk</c> and <c>gold_large.epk</c>). No Mods.dat
/// row grants them and the client does not add them for the rarity itself; without the buff the monster has no glow.
/// Both rows are invisible, not removable, cancel on death and carry no stats, so the 0x1c1 has no values.
/// </summary>
static class MonsterRarityBuffs
{
    public const ushort MagicRow = 66, RareRow = 67;

    /// <summary>The rarity's glow buff row, or null (normal and unique monsters have none).</summary>
    public static ushort? Row(MonsterRarity rarity) => rarity switch
    {
        MonsterRarity.Magic => MagicRow,
        MonsterRarity.Rare => RareRow,
        _ => null,
    };

    /// <summary>The BuffDefinitions Id of each row.</summary>
    public static string Id(ushort row) => row == MagicRow ? "monster_magic_effect_buff" : "monster_rare_effect_buff";
}
