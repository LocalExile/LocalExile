using GameServer.Core.Protocol.Instance;

static class DivinationRewards
{
    internal sealed record Reward(string Card, int Stack, string Kind, string Name, int Count,
        bool Corrupted, bool TwoImplicit, int Quality, int GemLevel, bool SixLink, int ItemLevel,
        bool WhiteSockets, bool Enabled);

    static int revision = -1;
    static IReadOnlyDictionary<string, Reward> rewards = new Dictionary<string, Reward>();

    internal static IReadOnlyDictionary<string, Reward> All
    {
        get
        {
            if (revision == GameData.Current.Revision) return rewards;
            var next = new Dictionary<string, Reward>(StringComparer.Ordinal);
            foreach (var row in GameData.Store!.LoadRows("divination_card_rewards", "card_id"))
            {
                int N(string k) => checked((int)Convert.ToInt64(row[k]));
                string S(string k) => (string)row[k]!;
                var reward = new Reward(S("card_id"), N("stack_size"), S("reward_kind"), S("reward_name"),
                    N("reward_count"), N("corrupted") != 0, N("two_implicit") != 0, N("quality"),
                    N("gem_level"), N("six_link") != 0, N("item_level"), N("white_sockets") != 0,
                    N("enabled") != 0);
                next.Add(reward.Card, reward);
            }
            rewards = next;
            revision = GameData.Current.Revision;
            return rewards;
        }
    }

    internal static Reward? Find(string card) => All.GetValueOrDefault(card);

    internal static Item? Create(Reward reward, ItemCatalog items, UniqueCatalog uniques, ItemRolls rolls, Random random,
        int characterLevel = 80)
    {
        if (!reward.Enabled || reward.Count < 1) return null;
        Item? item;
        ItemCatalog.BaseType? type;
        if (reward.Kind == "unique")
        {
            var definition = uniques.All.FirstOrDefault(d => UniqueCatalog.NameKey(d.Name)
                .Equals(UniqueCatalog.NameKey(reward.Name), StringComparison.OrdinalIgnoreCase));
            if (definition is null || (item = uniques.Create(definition, random)) is null) return null;
            type = items.ByHash(item.TypeHash);
        }
        else if (reward.Kind == "gem")
        {
            if (items.FindGem(reward.Name) is not { } gem
                || !gem.Variant.Creatable || !gem.Variant.Name.Equals(reward.Name, StringComparison.OrdinalIgnoreCase)
                || items.Gem(gem.Type) is not { } details) return null;
            int level = reward.GemLevel > 0 ? reward.GemLevel : 1;
            if (level > details.MaxLevel + 1) return null;
            type = gem.Type;
            item = ItemCatalog.Create(type) with { GemVariant = gem.Variant.Index,
                GemExperience = level > details.MaxLevel ? -1 : details.ExperienceFor(level) };
        }
        else if (reward.Kind is "currency" or "normal" or "rare" or "magic" or "card" or "divination")
        {
            // An unresolved description is not permission to substitute arbitrary equipment or another unique.
            type = items.All.FirstOrDefault(t => t.Supported && (t.Id == reward.Name || t.Name == reward.Name));
            if (type is null || (reward.Kind is "card" or "divination" && type.Class != "DivinationCard")) return null;
            item = ItemCatalog.Create(type, random: random);
        }
        else return null;
        if (type is not { Supported: true } || reward.Count > Math.Max(1, (int)type.MaxStack)) return null;
        int levelOfItem = reward.ItemLevel > 0 ? reward.ItemLevel : Math.Clamp(characterLevel, 1, 80);
        item = item with { ItemLevel = (byte)Math.Clamp(levelOfItem, 1, 100), StackSize = (ushort)reward.Count,
            Quality = reward.Quality > 0 ? (byte)reward.Quality : item.Quality };
        if (reward.Kind == "rare") item = rolls.Alchemy(item, random);
        else if (reward.Kind == "magic") item = rolls.Transmute(item, random);
        if (item is null) return null;
        if (reward.SixLink)
        {
            if (CurrencyEffects.SocketCapacity(item, type) < 6) return null;
            var requirements = items.Requirements(type);
            item = item with { Sockets = [.. Enumerable.Range(0, 6).Select(_ => new ItemSocket(
                reward.WhiteSockets ? SocketColour.White : SocketColours.Roll(requirements, item.ItemLevel, item.Quality, random)))],
                Links = [6] };
        }
        if (reward.TwoImplicit)
        {
            if (TwoImplicits(item, type, items, random) is not { } corrupted) return null;
            item = corrupted;
        }
        return item with { Identified = true, Corrupted = item.Corrupted || reward.Corrupted };
    }

    static Item? TwoImplicits(Item item, ItemCatalog.BaseType type, ItemCatalog items, Random random)
    {
        if (!type.Components.Contains("Mods") || type.Components.Contains("SkillGem")) return null;
        var kept = item.Mods.Where(m => m.Section != ModSection.Implicit).ToList();
        var families = new HashSet<int>();
        for (int index = 0; index < 2; index++)
        {
            var pool = items.Mods.All.Where(m => m.GenerationType == 5 && m.Domain == type.ModDomain
                && m.Level <= item.ItemLevel && m.WeightFor(type.Tags) > 0 && !m.Families.Any(families.Contains)).ToArray();
            if (pool.Length == 0) return null;
            long draw = random.NextInt64(pool.Sum(m => (long)m.WeightFor(type.Tags)));
            var picked = pool[^1];
            foreach (var mod in pool)
            {
                draw -= mod.WeightFor(type.Tags);
                if (draw < 0) { picked = mod; break; }
            }
            kept.Insert(index, picked.Roll(ModSection.Implicit, random));
            families.UnionWith(picked.Families);
        }
        return item with { Mods = kept, Corrupted = true };
    }
}
