using GameServer.Modding;

namespace LocalExile.Mods.ItemRarity;

/// <summary>10 times the chance of magic, rare and unique equipment. Off by default: /mod_enable itemrarity.</summary>
public sealed class ItemRarityMod : IMod
{
    const double Multiplier = 10;

    // Multiplied, not set: another mod's rarity stays in.
    public void Load(IModContext mod) => mod.ModifyDrops(drops => drops.Rarity *= Multiplier);
}
