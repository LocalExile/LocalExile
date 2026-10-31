using System.Text.Json;

/// <summary>
/// Which gems can drop from monsters (19-gems.md §5). The data's SkillGems table also holds rows that never drop in 3.29:
/// Graft skills (the Chayula gems), item-granted and legacy skills (Envy, Death Aura, Quickstep, the single-level utility
/// rows) and placeholders such as "...". The drop pool is therefore the gems the game actually gives a player: the
/// QuestRewardOffers gems of every class, each class's starting gems (Characters StartSkillGem/StartSupportGem) and the
/// drop-only exceptional supports (Empower, Enhance, Enlighten).
/// </summary>
static class GemSources
{
    static readonly string[] DropOnlySupports = ["Empower Support", "Enhance Support", "Enlighten Support"];

    public static IReadOnlySet<int> DropPool(ItemCatalog items, string folder = "data")
    {
        var pool = new HashSet<int>();
        foreach (var offer in QuestRewardCatalog.Load(folder).Offers)
            foreach (var choice in offer.Choices)
                if (items.Row(choice.ItemRow) is { } type && items.Gem(type) is not null) pool.Add(type.Row);
        string characters = Path.Combine(folder, "tables", "Characters.json"), gems = Path.Combine(folder, "tables", "SkillGems.json");
        if (File.Exists(characters) && File.Exists(gems))
        {
            using var c = JsonDocument.Parse(File.ReadAllBytes(characters));
            using var g = JsonDocument.Parse(File.ReadAllBytes(gems));
            foreach (var character in c.RootElement.EnumerateArray())
                foreach (string column in new[] { "StartSkillGem", "StartSupportGem" })
                    if (character.TryGetProperty(column, out var value) && value.ValueKind == JsonValueKind.Number
                        && value.GetInt32() is int row && row >= 0 && row < g.RootElement.GetArrayLength()
                        && g.RootElement[row].TryGetProperty("BaseItemTypesKey", out var baseRow) && baseRow.ValueKind == JsonValueKind.Number
                        && items.Row(baseRow.GetInt32()) is { } type && items.Gem(type) is not null)
                        pool.Add(type.Row);
        }
        foreach (string name in DropOnlySupports)
            if (items.Find(name) is { } type && items.Gem(type) is not null) pool.Add(type.Row);
        return pool;
    }
}
