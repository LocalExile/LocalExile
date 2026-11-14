using GameServer.Core.Protocol.Instance;

static class DivinationEquipmentRewards
{
    internal sealed record Rule(ItemInfluence Influences, int RandomInfluences, string Affix, bool Fractured,
        int Links, int Implicits, bool Unidentified, int Modifiers, string Special);
    internal sealed record Member(string Kind, string Id, string Base, int Weight);

    internal static Dictionary<string, Rule> LoadRules() => GameData.Store!.LoadRows("divination_reward_rules", "card_id")
        .ToDictionary(r => (string)r["card_id"]!, r =>
        {
            int N(string k) => Convert.ToInt32(r[k]);
            return new Rule((ItemInfluence)N("influences"), N("random_influences"), (string)r["affix"]!,
                N("fractured") != 0, N("links"), N("implicits"), N("unidentified") != 0, N("modifiers"), (string)r["special"]!);
        });

    internal static Dictionary<string, IReadOnlyList<Member>> LoadMembers() => GameData.Store!
        .LoadRows("divination_reward_members", "card_id, member_kind, member_id, base_type")
        .GroupBy(r => (string)r["card_id"]!).ToDictionary(g => g.Key, g => (IReadOnlyList<Member>)[.. g.Select(r =>
            new Member((string)r["member_kind"]!, (string)r["member_id"]!, (string)r["base_type"]!, Convert.ToInt32(r["weight"])))]);

    internal static Member Pick(IReadOnlyList<Member> pool, Random random)
    {
        long draw = random.NextInt64(pool.Sum(m => (long)m.Weight));
        foreach (var member in pool) if ((draw -= member.Weight) < 0) return member;
        throw new InvalidOperationException("Empty divination reward pool");
    }

    internal static bool Eligible(Member member, Rule? rule, int count, ItemCatalog items, ItemRolls rolls)
    {
        if (member.Kind != "base" || rule is null) return true;
        if (items.Find(member.Id) is not { Supported: true, Name.Length: > 0 } type || count > type.MaxStack) return false;
        var item = ItemCatalog.Create(type) with { Influences = rule.Special == "elevated" ? (ItemInfluence)63 : rule.Influences };
        return rule.Affix.Length == 0 || RequiredMods(rule, type, item, items, rolls).Any();
    }

    internal static Item? Apply(Item item, ItemCatalog.BaseType type, DivinationRewards.Reward reward, Rule rule,
        ItemCatalog items, ItemRolls rolls, Random random)
    {
        var influence = rule.Influences;
        var available = Enum.GetValues<ItemInfluence>().Where(i => i != ItemInfluence.None && !influence.HasFlag(i)).ToList();
        for (int i = 0; i < rule.RandomInfluences; i++)
        {
            int at = random.Next(available.Count);
            influence |= available[at]; available.RemoveAt(at);
        }
        item = item with { Influences = item.Influences | influence };
        ModCatalog.Mod? required = null;
        if (rule.Affix.Length > 0)
        {
            var pool = RequiredMods(rule, type, item, items, rolls).ToArray();
            if (pool.Length == 0) return null;
            required = pool[random.Next(pool.Length)];
        }
        if (reward.Kind is "rare" or "magic")
        {
            item = rolls.Roll(item, reward.Kind == "rare" ? ItemRarity.Rare : ItemRarity.Magic, random,
                rule.Modifiers > 0 ? (rule.Modifiers, rule.Modifiers == 8 ? 4 : 3) : null, required);
            if (!item.Mods.Any(m => m.Section == ModSection.Explicit)) return null;
        }
        if (rule.Fractured)
        {
            if (!type.CanBeFractured) return null;
            var explicits = item.Mods.Where(m => m.Section == ModSection.Explicit).ToList();
            if (explicits.Count == 0) return null;
            var fractured = required is null ? explicits[random.Next(explicits.Count)] : explicits.First(m => m.Hash == required.Hash);
            item = item with { Mods = [.. item.Mods.Where(m => m.Section != ModSection.Explicit), fractured,
                .. explicits.Where(m => !ReferenceEquals(m, fractured))], FracturedCount = 1 };
        }
        if (rule.Links > 0)
        {
            if (CurrencyEffects.SocketCapacity(item, type) < rule.Links) return null;
            var requirements = items.Requirements(type);
            item = item with { Sockets = [.. Enumerable.Range(0, rule.Links).Select(_ => new ItemSocket(
                SocketColours.Roll(requirements, item.ItemLevel, item.Quality, random)))], Links = [(byte)rule.Links] };
        }
        if (rule.Special == "vestigial") item = item with { Vestigial = true };
        if (rule.Special == "vaal_aspect") item = item with { Rarity = ItemRarity.Unique };
        if (rule.Special == "foulborn")
        {
            var original = items.Mods.ById("MagicUtilityFlasksAlwaysApplyUnique__1");
            var replacement = items.Mods.ById("MutatedUniqueBelt43MagicUtilityFlasksAlwaysApplyRightmost");
            if (original is null || replacement is null || !item.Mods.Any(m => m.Hash == original.Hash)) return null;
            item = item with { Mods = [.. item.Mods.Select(m => m.Hash == original.Hash ? replacement.Roll(m.Section, random) : m)] };
        }
        if (rule.Special == "corrupted_blood")
        {
            var mod = items.Mods.ById(type.Class == "AbyssJewel"
                ? "V2CorruptedBloodImmunityAbyssalJewelCorrupted" : "V2CorruptedBloodImmunityCorrupted");
            if (mod is null || mod.Domain != type.ModDomain) return null;
            item = item with { Mods = [mod.Roll(ModSection.Implicit, random), .. item.Mods.Where(m => m.Section != ModSection.Implicit)] };
        }
        if (rule.Special == "double_veiled")
        {
            var prefix = items.Mods.ById("VeiledPrefix"); var suffix = items.Mods.ById("VeiledSuffix");
            if (prefix is null || suffix is null) return null;
            var mods = item.Mods.Where(m => m.Section != ModSection.Explicit).ToList();
            mods.Add(prefix.Roll(ModSection.Explicit, random)); mods.Add(suffix.Roll(ModSection.Explicit, random));
            // Reserve one prefix and suffix for the two veiled lines while rolling the rest of the rare.
            item = rolls.Roll(item with { Mods = mods, FracturedCount = 2 }, ItemRarity.Rare, random) with { FracturedCount = 0 };
        }
        if (rule.Special == "perfect_eldritch")
        {
            var pool = items.Mods.All.Where(m => m.GenerationType is 28 or 29
                && m.Id.EndsWith("EldritchImplicit6", StringComparison.Ordinal) && m.WeightFor(type.Tags) > 0).ToArray();
            if (pool.Length == 0) return null;
            item = item with { Mods = [pool[random.Next(pool.Length)].Roll(ModSection.Implicit, random),
                .. item.Mods.Where(m => m.Section != ModSection.Implicit)] };
        }
        if (rule.Special == "synthesised")
        {
            var pool = reward.ImplicitMods.Select(items.Mods.ById).OfType<ModCatalog.Mod>().Where(m => m.Level <= item.ItemLevel).ToList();
            var chosen = new List<ModCatalog.Mod>();
            for (int i = 0; i < rule.Implicits; i++)
            {
                var allowed = pool.Where(m => !chosen.Any(c => c.Hash == m.Hash || c.Families.Intersect(m.Families).Any())).ToArray();
                if (allowed.Length == 0) return null;
                chosen.Add(allowed[random.Next(allowed.Length)]);
            }
            item = item with { Synthesised = true, Mods = [.. chosen.Select(m => m.Roll(ModSection.Implicit, random)),
                .. item.Mods.Where(m => m.Section != ModSection.Implicit)] };
        }
        if (rule.Special == "elder_guardian")
        {
            string id = new[] { "MapElderContainsBoss1", "MapElderContainsBoss2", "MapElderContainsBoss3", "MapElderContainsBoss4_" }[random.Next(4)];
            if (items.Mods.ById(id) is not { } guardian) return null;
            item = item with { Mods = [guardian.Roll(ModSection.Implicit, random), .. item.Mods] };
        }
        if (rule.Special == "delirium")
        {
            if (items.Mods.ById("MapDeliriumEverywhere") is not { } delirium
                || items.Mods.ById("AfflictionMapReward1__") is not { } rewards) return null;
            item = item with { Mods = [.. item.Mods,
                delirium.With(ModSection.Implicit, [random.Next(1, int.MaxValue), 0], random),
                rewards.With(ModSection.Enchant, [5], random)] };
        }
        if (rule.Special == "blueprint") return DivinationBlueprintRewards.Create(item, type, random);
        return item;
    }

    static IEnumerable<ModCatalog.Mod> RequiredMods(Rule rule, ItemCatalog.BaseType type, Item item, ItemCatalog items, ItemRolls rolls)
    {
        var tags = rolls.TagsWith(type, item.Influences);
        return items.Mods.All.Where(m => m.GenerationType is 1 or 2
            && (rule.Special == "elevated" ? m.Name.Contains("Elevated", StringComparison.Ordinal) && m.Id.Contains("Maven", StringComparison.Ordinal)
                : rule.Affix.StartsWith("id:", StringComparison.Ordinal) ? m.Id == rule.Affix[3..]
                : rule.Affix == "life" ? m.Stats.Any(s => s.Id == "base_maximum_life")
                : UniqueCatalog.NameKey(m.Name) == rule.Affix)
            && (m.Domain == type.ModDomain || rule.Affix.StartsWith("id:", StringComparison.Ordinal) && m.Domain == 9)
            && (m.WeightFor(tags) > 0 || rule.Special == "elevated" && m.SpawnWeights.Any(w => w.Tag != "default" && tags.Contains(w.Tag))
                || rule.Affix == "id:ReduceGlobalFlatManaCostStrIntMasterVendor" && type.Class is "Ring" or "Amulet"
                || rule.Affix is "id:GrantsCatAspectCrafted" or "id:StrIntMasterItemGenerationCanHaveMultipleCraftedMods"));
    }
}
