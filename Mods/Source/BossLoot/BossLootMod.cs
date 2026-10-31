using GameServer.Modding;

namespace LocalExile.Mods.BossLoot;

/// <summary>An Exalted Orb from every unique monster. Off by default: /mod_enable bossloot.</summary>
public sealed class BossLootMod : IMod
{
    public void Load(IModContext mod) => mod.ModifyDrops(drops =>
    {
        if (drops.Monster.Rarity == MonsterRarity.Unique) drops.Items.Add("Exalted Orb");
    });
}
