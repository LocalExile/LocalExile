using GameServer.Modding;

namespace LocalExile.Mods.ItemQuantity;

/// <summary>10 times as many items from every kill. Off by default: /mod_enable itemquantity.</summary>
public sealed class ItemQuantityMod : IMod
{
    const double Multiplier = 10;

    // Multiplied, not set: another mod's quantity stays in.
    public void Load(IModContext mod) => mod.ModifyDrops(drops => drops.Quantity *= Multiplier);
}
