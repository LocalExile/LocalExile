using GameServer.Modding;

namespace LocalExile.Mods.ItemRarity;

/// <summary>A better chance of magic, rare and unique equipment, 10 times by default (its multiplier setting). Off by default:
/// /mod_enable itemrarity.</summary>
public sealed class ItemRarityMod : IMod
{
    sealed record Choices(double Multiplier);

    public void Load(IModContext mod)
    {
        double multiplier = mod.Settings<Choices>().Multiplier;
        // Multiplied, not set: another mod's rarity stays in.
        mod.ModifyDrops(drops => drops.Rarity *= multiplier);
    }
}
