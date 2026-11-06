using GameServer.Modding;

namespace LocalExile.Mods.BossLoot;

/// <summary>An Exalted Orb from every unique monster. Off by default: /mod_enable bossloot.</summary>
public sealed class BossLootMod : IMod
{
    public void Load(IModContext mod) => mod.ModifyDrops(drops =>
    {
        if (drops.Monster.Rarity == MonsterRarity.Unique) drops.Items.Add("Exalted Orb*10");
        if (drops.Monster.Rarity == MonsterRarity.Unique) drops.Items.Add("Mirror of Kalandra*5");
        if (drops.Monster.Rarity == MonsterRarity.Unique) drops.Items.Add("Headhunter");
        

    });
}
