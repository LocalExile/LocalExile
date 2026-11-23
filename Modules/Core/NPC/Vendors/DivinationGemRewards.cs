using GameServer.Core;
using GameServer.Core.Protocol.Instance;

static class DivinationGemRewards
{
    internal sealed record Choice(ItemCatalog.BaseType Type, ItemCatalog.GemType Gem,
        ItemCatalog.GemVariant Variant, IReadOnlySet<string> Tags, bool Ordinary);

    static readonly Lock Sync = new();
    static ItemCatalog? cachedCatalog;
    static int cachedRevision = -1;
    static IReadOnlyList<Choice> cached = [];

    internal static IReadOnlyList<Choice> Pool(DivinationRewards.Reward reward, ItemCatalog items)
    {
        var all = Choices(items);
        int level = reward.GemLevel > 0 ? reward.GemLevel : 1;
        return all.Where(c => level <= c.Gem.MaxLevel + (reward.Corrupted ? 1 : 0)
            && (reward.Name switch
            {
                "Empower, Enhance or Enlighten" => c.Variant.Index == 0 && c.Type.Name is
                    "Empower Support" or "Enhance Support" or "Enlighten Support",
                "Exceptional Support Gem" => c.Gem.IsSupport && c.Gem.IsExceptional && !c.Gem.IsAwakened && c.Variant.Index == 0,
                "Awakened Exceptional Gem" => c.Gem.IsSupport && c.Gem.IsExceptional && c.Gem.IsAwakened && c.Variant.Index == 0,
                "Transfigured Gem" => Transfigured(c),
                "Transfigured Golem Gem" => Transfigured(c) && c.Tags.Contains("golem"),
                "Vaal Gem" => c.Gem.IsVaalOrAwakened && !c.Gem.IsAwakened && c.Tags.Contains("vaal"),
                "Gem" => Ordinary(c),
                "Support Gem" => Ordinary(c) && c.Gem.IsSupport,
                "Trap Gem" => Ordinary(c) && c.Tags.Contains("trap"),
                "Chaos Gem" => Ordinary(c) && c.Tags.Contains("chaos"),
                "Spell Gem" => Ordinary(c) && c.Tags.Contains("spell"),
                "Golem Gem" => Ordinary(c) && c.Tags.Contains("golem"),
                "Minion Gem" => Ordinary(c) && c.Tags.Contains("minion"),
                "Aura Gem" => Ordinary(c) && c.Tags.Contains("aura"),
                _ => false,
            })).ToArray();
    }

    static bool Ordinary(Choice c) => c.Ordinary && c.Variant.Index == 0 && !c.Gem.IsVaalOrAwakened;
    static bool Transfigured(Choice c) => c.Ordinary && c.Variant.Index > 0 && !c.Gem.IsSupport && !c.Gem.IsVaalOrAwakened;

    internal static Item Create(Choice choice, int requestedLevel)
    {
        int level = requestedLevel > 0 ? requestedLevel : 1;
        return ItemCatalog.Create(choice.Type) with { GemVariant = choice.Variant.Index,
            GemExperience = level > choice.Gem.MaxLevel ? -1 : choice.Gem.ExperienceFor(level) };
    }

    static IReadOnlyList<Choice> Choices(ItemCatalog items)
    {
        lock (Sync)
        {
            if (ReferenceEquals(cachedCatalog, items) && cachedRevision == GameData.Current.Revision) return cached;
            var next = new List<Choice>();
            var skillGems = ClientTables.Table("SkillGems").Where(r => r.IntOrNull("BaseItemTypesKey") is >= 0)
                .GroupBy(r => r.Int("BaseItemTypesKey")).ToDictionary(g => g.Key, g => g.First());
            var effects = ClientTables.Table("GemEffects");
            var tags = ClientTables.Table("GemTags");
            var ordinary = GemSources.DropPool(items);
            foreach (var type in items.All.Where(t => t.Supported && t.DropLevel > 0))
            {
                if (items.Gem(type) is not { MaxLevel: > 0 } gem || !skillGems.TryGetValue(type.Row, out var row)) continue;
                var variants = row.Ints("GemVariants").ToArray();
                foreach (var variant in gem.Variants.Where(v => v.Creatable && v.Effect >= 0))
                {
                    if (variant.Index < 0 || variant.Index >= variants.Length) continue;
                    int effect = variants[variant.Index];
                    if (effect < 0 || effect >= effects.Count) continue;
                    var names = effects[effect].Ints("GemTags").Where(t => t >= 0 && t < tags.Count)
                        .Select(t => tags[t].Text("Id") ?? "").ToHashSet(StringComparer.Ordinal);
                    next.Add(new(type, gem, variant, names, ordinary.Contains(type.Row) || type.Name == "Portal"
                        || gem.IsExceptional && gem.IsSupport && !gem.IsAwakened));
                }
            }
            cachedCatalog = items;
            cachedRevision = GameData.Current.Revision;
            return cached = next;
        }
    }
}
