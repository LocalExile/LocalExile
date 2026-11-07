using GameServer.Modding;

namespace LocalExile.Mods.ItemQuantity;

/// <summary>More items from every kill, 10 times as many by default (its multiplier setting). Off by default: /mod_enable itemquantity.</summary>
public sealed class ItemQuantityMod : IMod
{
    sealed record Choices(double Multiplier);

    public void Load(IModContext mod)
    {
        double multiplier = mod.Settings<Choices>().Multiplier;
        // Multiplied, not set: another mod's quantity stays in.
        mod.ModifyDrops(drops => drops.Quantity *= multiplier);
    }
}
