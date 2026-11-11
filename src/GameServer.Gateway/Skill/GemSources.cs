using System.Text.Json;
using GameServer.Core;

/// <summary>
/// Which gems can drop from monsters (19-gems.md §5). The data's SkillGems table also holds rows that never drop in 3.29:
/// Graft skills (the Chayula gems), item-granted and legacy skills (Envy, Death Aura, Quickstep, the single-level utility
/// rows) and placeholders such as "...". The drop pool is therefore the gems the game actually gives a player: the
/// QuestRewardOffers gems of every class, each class's starting gems (Characters StartSkillGem/StartSupportGem) and the
/// drop-only exceptional supports (Empower, Enhance, Enlighten).
/// </summary>
public static class GemSources
{
    static readonly string[] DropOnlySupports = ["Empower Support", "Enhance Support", "Enlighten Support"];

    public static IReadOnlySet<int> DropPool(ItemCatalog items, string folder = "data")
    {
        var pool = new HashSet<int>();
        foreach (var offer in QuestRewardCatalog.Load(folder).Offers)
            foreach (var choice in offer.Choices)
                if (items.Row(choice.ItemRow) is { } type && items.Gem(type) is not null) pool.Add(type.Row);
        if (ClientTables.Has("Characters") && ClientTables.Has("SkillGems"))
        {
            var c = ClientTables.Table("Characters");
            var g = ClientTables.Table("SkillGems");
            foreach (var character in c)
                foreach (string column in new[] { "StartSkillGem", "StartSupportGem" })
                    if (character.IntOrNull(column) is int row && row >= 0 && row < g.Count
                        && g[row].IntOrNull("BaseItemTypesKey") is int baseRow
                        && items.Row(baseRow) is { } type && items.Gem(type) is not null)
                        pool.Add(type.Row);
        }
        foreach (string name in DropOnlySupports)
            if (items.Find(name) is { } type && items.Gem(type) is not null) pool.Add(type.Row);
        return pool;
    }
}
