using GameServer.Core.Protocol.Instance;

static class DivinationRewards
{
    internal sealed record Reward(string Card, int Stack, string Kind, string Name, int Count,
        bool Corrupted, bool TwoImplicit, int Quality, int GemLevel, bool SixLink, int ItemLevel,
        bool WhiteSockets, bool Enabled)
    {
        public int QualityMax { get; init; }
        public DivinationEquipmentRewards.Rule? Equipment { get; init; }
        public IReadOnlyList<DivinationEquipmentRewards.Member> Members { get; init; } = [];
        public IReadOnlyList<string> ImplicitMods { get; init; } = [];
    }

    static int revision = -1;
    static IReadOnlyDictionary<string, Reward> rewards = new Dictionary<string, Reward>();

    internal static IReadOnlyDictionary<string, Reward> All
    {
        get
        {
            if (revision == GameData.Current.Revision) return rewards;
            var next = new Dictionary<string, Reward>(StringComparer.Ordinal);
            var rules = DivinationEquipmentRewards.LoadRules();
            var members = DivinationEquipmentRewards.LoadMembers();
            var implicitMods = GameData.Store!.LoadRows("divination_reward_implicit_mods", "card_id, mod_id")
                .GroupBy(r => (string)r["card_id"]!).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(r => (string)r["mod_id"]!)]);
            foreach (var row in GameData.Store!.LoadRows("divination_card_rewards", "card_id"))
            {
                int N(string k) => checked((int)Convert.ToInt64(row[k]));
                string S(string k) => (string)row[k]!;
                var reward = new Reward(S("card_id"), N("stack_size"), S("reward_kind"), S("reward_name"),
                    N("reward_count"), N("corrupted") != 0, N("two_implicit") != 0, N("quality"),
                    N("gem_level"), N("six_link") != 0, N("item_level"), N("white_sockets") != 0,
                    N("enabled") != 0) { QualityMax = N("quality_max"), Equipment = rules.GetValueOrDefault(S("card_id")),
                        Members = members.GetValueOrDefault(S("card_id")) ?? [], ImplicitMods = implicitMods.GetValueOrDefault(S("card_id")) ?? [] };
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
        if (reward.Kind == "set_pool")
        {
            var sets = reward.Members.Where(m => m.Kind == "set" && Find(m.Id) is { Enabled: true, Kind: not "set_pool" }).ToArray();
            if (sets.Length == 0) return null;
            return Create(Find(DivinationEquipmentRewards.Pick(sets, random).Id)!, items, uniques, rolls, random, characterLevel);
        }
        Item? item;
        ItemCatalog.BaseType? type;
        int levelOfItem = reward.ItemLevel > 0 ? reward.ItemLevel : Math.Clamp(characterLevel, 1, 80);
        if (reward.Members.Count > 0)
        {
            var eligible = reward.Members.Where(m => DivinationEquipmentRewards.Eligible(m, reward.Equipment, reward.Count, items, rolls)).ToArray();
            if (eligible.Length == 0) return null;
            var member = DivinationEquipmentRewards.Pick(eligible, random);
            if (member.Kind == "unique")
            {
                var definition = uniques.All.FirstOrDefault(d => d.Name == member.Id && d.BaseTypeId == member.Base);
                item = definition is null ? null : uniques.Create(definition with { ItemLevel = (byte)levelOfItem }, random);
            }
            else if (member.Kind == "map") item = uniques.NamedMap(member.Id);
            else item = items.Find(member.Id) is { Supported: true } selected ? ItemCatalog.Create(selected, random: random) : null;
            if (item is null) return null;
            type = items.ByHash(item.TypeHash);
        }
        else if (reward.Kind == "unique")
        {
            var definition = uniques.All.FirstOrDefault(d => UniqueCatalog.NameKey(d.Name)
                .Equals(UniqueCatalog.NameKey(reward.Name), StringComparison.OrdinalIgnoreCase));
            item = definition is null ? uniques.NamedMap(reward.Name)
                : uniques.Create(definition with { ItemLevel = (byte)levelOfItem }, random);
            if (item is null) return null;
            type = items.ByHash(item.TypeHash);
        }
        else if (reward.Kind == "gem_pool")
        {
            var pool = DivinationGemRewards.Pool(reward, items);
            if (pool.Count == 0) return null;
            var choice = pool[random.Next(pool.Count)];
            type = choice.Type;
            item = DivinationGemRewards.Create(choice, reward.GemLevel);
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
        if (reward.Quality < 0 || reward.Quality > 255 || reward.QualityMax < 0 || reward.QualityMax > 255
            || reward.QualityMax > 0 && reward.QualityMax < reward.Quality) return null;
        int quality = reward.QualityMax > 0 ? random.Next(reward.Quality, reward.QualityMax + 1) : reward.Quality;
        item = item with { ItemLevel = (byte)Math.Clamp(levelOfItem, 1, 100), StackSize = (ushort)reward.Count,
            Quality = quality > 0 ? (byte)quality : item.Quality };
        if (reward.Equipment is { } rule)
            item = DivinationEquipmentRewards.Apply(item, type, reward, rule, items, rolls, random);
        else if (reward.Kind == "rare") item = rolls.Alchemy(item, random);
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
        // Only a reward that names its corruption implicits ("Two-Implicit", "1-2 Implicit") rolls them, replacing the
        // base's own. A plain "Corrupted" reward is the item as it is, corrupted: Apocalypse's Eyes of the Greatwolf keeps
        // its doubled Talisman implicit, and a corrupted unique keeps its own implicits.
        if (reward.Equipment?.Special is not ("corrupted_blood" or "talisman")
            && (reward.TwoImplicit || reward.Equipment?.Implicits == -2))
        {
            int count = reward.Equipment?.Implicits == -2 ? random.Next(1, 3) : 2;
            if (CorruptionImplicits(item, type, items, random, count) is not { } corrupted) return null;
            item = corrupted;
        }
        return item with { Identified = reward.Equipment?.Unidentified != true, Corrupted = item.Corrupted || reward.Corrupted };
    }

    static Item? CorruptionImplicits(Item item, ItemCatalog.BaseType type, ItemCatalog items, Random random, int count)
    {
        if (!type.Components.Contains("Mods") || type.Components.Contains("SkillGem")) return null;
        var kept = item.Mods.Where(m => m.Section != ModSection.Implicit).ToList();
        var families = new HashSet<int>();
        var hashes = new HashSet<ushort>();
        for (int index = 0; index < count; index++)
        {
            var pool = items.Mods.All.Where(m => m.GenerationType == 5 && m.Domain == type.ModDomain
                && m.Level <= item.ItemLevel && m.WeightFor(type.Tags) > 0
                && !hashes.Contains(m.Hash) && !m.Families.Any(families.Contains)).ToArray();
            if (pool.Length == 0)
                return index == 0 && count == 1 ? item with { Corrupted = true } : null;
            long draw = random.NextInt64(pool.Sum(m => (long)m.WeightFor(type.Tags)));
            var picked = pool[^1];
            foreach (var mod in pool)
            {
                draw -= mod.WeightFor(type.Tags);
                if (draw < 0) { picked = mod; break; }
            }
            kept.Insert(index, picked.Roll(ModSection.Implicit, random));
            families.UnionWith(picked.Families);
            hashes.Add(picked.Hash);
        }
        return item with { Mods = kept, Corrupted = true };
    }
}
