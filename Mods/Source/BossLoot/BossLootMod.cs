using GameServer.Modding;

namespace LocalExile.Mods.BossLoot;

/// <summary>An item from every monster of a rarity: by default an Exalted Orb from every unique monster (its item and
/// rarities settings). Off by default: /mod_enable bossloot.</summary>
public sealed class BossLootMod : IMod
{
    sealed record Choices(string Item, string[] Rarities);

    public void Load(IModContext mod)
    {
        var choices = mod.Settings<Choices>();
        var rarities = choices.Rarities.Select(r => Enum.Parse<MonsterRarity>(r, ignoreCase: true)).ToHashSet();
        mod.ModifyDrops(drops =>
        {
            if (rarities.Contains(drops.Monster.Rarity)) drops.Items.Add(choices.Item);
        });
    }
}
