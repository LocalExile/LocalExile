using GameServer.Core;
using GameServer.Core.Protocol.Instance;

/// <summary>Strongbox types, selection weights, level limits and native object data from the client tables.</summary>
public static class StrongboxCatalog
{
    public sealed record Definition(int Row, string Id, string Name, int Weight, int HardModeWeight, int MinLevel, int MaxLevel,
        bool RareType, string? WeightStat, string Model, IReadOnlyList<string> Components, StateMachineData? States,
        IReadOnlyList<ItemMod> Mods)
    {
        /// <summary>Strongboxes <c>__col4</c>: false on the fixed boxes (the Grandmaster's caches, the unique chests' bases,
        /// Atziri's Vessel, the Breach box, Ventor's box), true on every box that spawns. Read (inferred) as "rolls a
        /// rarity and mods".</summary>
        public bool Rollable { get; init; } = true;
        /// <summary>Strongboxes <c>__col2</c> (5, 10, 12, 15): read as the box's base item count. ASSUMED meaning (unnamed
        /// column; it fits Armourer's 15, Blacksmith's 12, Large and Ornate 10, the rest 5).</summary>
        public int Items { get; init; } = 5;
        /// <summary>What the box drops (<see cref="StrongboxLoot"/>).</summary>
        public Contents Kind { get; init; }
        /// <summary>The Chests row's tags and "default": what its explicit mods' spawn weights read.</summary>
        public IReadOnlySet<string> Tags { get; init; } = new HashSet<string>(["default"]);
        /// <summary>The Chests row's <c>ChestEffectsKey</c>: the ChestEffects row whose per-rarity open effect the box
        /// plays as it opens (<see cref="OpenEffect"/>).</summary>
        public int? ChestEffect { get; init; }
    }

    /// <summary>What a box drops: from its Chests tags (armour, weapon, jewellery, gem, map, divination_card, scarab), else
    /// from its id (Arcanist: currency, Artisan: quality currency), else the general container table.</summary>
    public enum Contents { General, Currency, QualityCurrency, Armour, Weapon, Jewellery, Gem, Map, Card, Scarab, Jewel, Flask }

    static readonly Lazy<IReadOnlyList<Definition>> Data = new(Load);
    public static IReadOnlyList<Definition> All => Data.Value;
    public static Definition? Find(string id) => All.FirstOrDefault(r => r.Id == id);

    static IReadOnlyList<Definition> Load()
    {
        if (!ClientTables.Has("Strongboxes") || !ClientTables.Has("Chests")) return [];
        var chests = ClientTables.Table("Chests").ToArray();
        var tags = ClientTables.TableOrEmpty("Tags");
        var types = ObjectTypes.All().ToDictionary(t => t.Type, t => t.Row);
        var shared = ObjectTypes.SharedStates();
        var stats = Program.Stats.Names;
        var result = new List<Definition>();
        foreach (var row in ClientTables.Table("Strongboxes"))
        {
            if (row.IntOrNull("ChestsKey") is not int index || (uint)index >= chests.Length) continue;
            var chest = chests[index];
            string id = chest.Text("Id") ?? "";
            if (!types.TryGetValue(id, out var type)
                && !types.TryGetValue(chest.Text("InheritsFrom") ?? "", out type)) continue;
            var components = ObjectTypes.Components(type);
            var states = components.Contains("StateMachine") ? ObjectTypes.StateMachine(type, shared) : null;
            if (!QuestObject.CanSerialize(components, states is not null)) continue;
            if (chest.Texts("AOFiles").FirstOrDefault() is not { Length: > 0 } model) continue;
            var mods = new List<ItemMod>();
            foreach (int mod in chest.Ints("ModsKeys"))
                if (Program.Items.Mods.Row(mod) is { } value)
                    mods.Add(value.Roll(ModSection.Implicit, new Random(0)));
            int? stat = row.IntOrNull("SpawnWeightIncrease");
            var chestTags = chest.Ints("TagsKeys").Select(t => tags.Row(t)?.Text("Id") ?? "").Append("default").ToHashSet(StringComparer.Ordinal);
            result.Add(new(index, id, chest.Text("Name") ?? id, row.Int("SpawnWeight"), row.Int("SpawnWeightHardmode"),
                chest.Int("MinLevel"), chest.Int("MaxLevel"), row.BoolOrNull("__col7") == true,
                stat is int key && key >= 0 && key < stats.Count ? stats[key] : null, model, components, states, mods)
            {
                Rollable = row.BoolOrNull("__col4") ?? true,
                Items = Math.Max(1, row.IntOrNull("__col2") ?? 5),
                Kind = KindOf(id, chestTags),
                Tags = chestTags,
                ChestEffect = chest.IntOrNull("ChestEffectsKey") is int effect and >= 0 ? effect : null,
            });
        }
        return result;
    }

    static Contents KindOf(string id, IReadOnlySet<string> tags) =>
        tags.Contains("armour") ? Contents.Armour
        : tags.Contains("weapon") ? Contents.Weapon
        : tags.Contains("jewellery") ? Contents.Jewellery
        : tags.Contains("gem") ? Contents.Gem
        : tags.Contains("map") ? Contents.Map
        : tags.Contains("divination_card") ? Contents.Card
        : tags.Contains("scarab") ? Contents.Scarab
        : tags.Contains("jewel") ? Contents.Jewel
        : tags.Contains("flask") ? Contents.Flask
        : id.Contains("/Arcanist", StringComparison.Ordinal) ? Contents.Currency
        : id.Contains("/Artisan", StringComparison.Ordinal) ? Contents.QualityCurrency
        : Contents.General;

    /// <summary>A ChestEffects row: the effect models a chest plays while closed (a looping glow) and when it opens,
    /// one per rarity (the strongbox rows 0-3 are the white/blue/gold/brown effect sets; 4-5 have a single one).</summary>
    sealed record EffectRow(string Id, string? NormalClosed, string? MagicClosed, string? RareClosed, string? UniqueClosed,
        string? Normal, string? Magic, string? Rare, string? Unique);

    static readonly Lazy<IReadOnlyList<EffectRow>> Effects = new(LoadEffects);

    /// <summary>The ChestEffects rows in row order (Chests <c>ChestEffectsKey</c> indexes them).</summary>
    static IReadOnlyList<EffectRow> LoadEffects()
    {
        if (!ClientTables.Has("ChestEffects")) return [];
        return [.. ClientTables.Table("ChestEffects").Select(row => new EffectRow(row.Text("Id") ?? "",
            AO(row.Text("Normal_Closed_AOFile")), AO(row.Text("Magic_Closed_AOFile")),
            AO(row.Text("Rare_Closed_AOFile")), AO(row.Text("Unique_Closed_AOFile")),
            AO(row.Text("Normal_Open_AOFile")), AO(row.Text("Magic_Open_AOFile")),
            AO(row.Text("Rare_Open_AOFile")), AO(row.Text("Unique_Open_AOFile"))))];
        static string? AO(string? path) => string.IsNullOrEmpty(path) ? null : path;
    }

    static EffectRow? EffectOf(Definition type) =>
        type.ChestEffect is int key && (uint)key < (uint)Effects.Value.Count ? Effects.Value[key] : null;

    /// <summary>The .ao of the looping effect a closed box wears: the ChestEffects row's <c>Normal_Closed_AOFile</c> (0),
    /// <c>Magic_…</c> (1), <c>Rare_…</c> (2) or <c>Unique_…</c> (3); null when the row leaves that one empty. Its default
    /// animation is "loop": the coloured glow that tells a box's rarity apart.</summary>
    public static string? ClosedEffect(Definition type, byte rarity) => EffectOf(type) is not { } row ? null : rarity switch
    {
        0 => row.NormalClosed, 1 => row.MagicClosed, 2 => row.RareClosed, _ => row.UniqueClosed,
    };

    /// <summary>The .ao of the effect the box plays as it opens: the ChestEffects row's <c>Normal_Open_AOFile</c> (0),
    /// <c>Magic_…</c> (1), <c>Rare_…</c> (2) or <c>Unique_…</c> (3); null when the row leaves that one empty. Each is an
    /// effect whose default "openfx" animation carries its own SoundEvents (Audio/…/Chests/Strongbox/Effect_$(#).ogg).</summary>
    public static string? OpenEffect(Definition type, byte rarity) => EffectOf(type) is not { } row ? null : rarity switch
    {
        0 => row.Normal, 1 => row.Magic, 2 => row.Rare, _ => row.Unique,
    };

    public static int Weight(Definition type, int level, bool hardMode, MapMods mods)
    {
        if (level < type.MinLevel || level > type.MaxLevel) return 0;
        long weight = hardMode ? type.HardModeWeight : type.Weight;
        weight = weight * Math.Max(0, 100L + (type.WeightStat is { } stat ? mods.Get(stat) : 0)) / 100;
        if (type.RareType && mods.Get("map_strongbox_rare_boxes_weighting_multiplier") is int multiplier and > 0) weight *= multiplier;
        return (int)Math.Clamp(weight, 0, int.MaxValue);
    }

    public static Definition? Roll(int level, bool hardMode, MapMods mods, Random random)
    {
        var pool = All.Select(t => (Type: t, Weight: Weight(t, level, hardMode, mods))).Where(p => p.Weight > 0).ToArray();
        long total = pool.Sum(p => (long)p.Weight);
        if (total == 0) return null;
        long roll = random.NextInt64(total);
        foreach (var p in pool) if ((roll -= p.Weight) < 0) return p.Type;
        return pool[^1].Type;
    }

    public static byte[] Blob(Definition type, int x, int y, bool started, bool opened, byte rarity = 0,
        IReadOnlyList<ItemMod>? mods = null, IReadOnlyList<(uint Key, int Value)>? stats = null) =>
        Blob(type, x, y, started, opened, rarity, mods, stats, null);

    /// <param name="unique">A unique box: its own model, and its name (Words) in ObjectMagicProperties.</param>
    public static byte[] Blob(Definition type, int x, int y, bool started, bool opened, byte rarity,
        IReadOnlyList<ItemMod>? mods, IReadOnlyList<(uint Key, int Value)>? stats, UniqueBox? unique) =>
        new QuestObject(x, y, type.Components, Model: unique?.Model ?? type.Model, Opened: opened)
        {
            StateMachine = type.States?.Set("started", started ? 1 : 0).Set("opened", opened ? 1 : 0),
            Mods = mods ?? type.Mods, Rarity = rarity, Stats = stats ?? [],
            NameWords = unique is null ? [] : [unique.NameWord],
        }.Blob();

    /// <summary>A unique strongbox (UniqueChests): its name (a Words row's HASH32), its own model, the area level from which
    /// it spawns, its weight among the unique boxes, the strongbox it is (its Chests row), and its mods.</summary>
    public sealed record UniqueBox(int Row, string Id, string Name, uint NameWord, string Model, int MinLevel, int Weight,
        int ChestRow, IReadOnlyList<int> ModRows);

    static readonly Lazy<IReadOnlyList<UniqueBox>> UniqueData = new(LoadUniques);
    /// <summary>The unique strongboxes that spawn (SpawnWeight above 0) on a strongbox type the server creates.</summary>
    public static IReadOnlyList<UniqueBox> Uniques => UniqueData.Value;
    public static UniqueBox? FindUnique(string id) => Uniques.FirstOrDefault(u => u.Id == id);

    static IReadOnlyList<UniqueBox> LoadUniques()
    {
        if (!ClientTables.Has("UniqueChests") || !ClientTables.Has("Words")) return [];
        var words = ClientTables.Table("Words").ToArray();
        var result = new List<UniqueBox>();
        foreach (var row in ClientTables.Table("UniqueChests"))
        {
            if (row.IntOrNull("SpawnWeight") is not int weight || weight <= 0 || row.IntOrNull("ChestsKey") is not int chest
                || All.All(t => t.Row != chest) || row.IntOrNull("WordsKey") is not int word || (uint)word >= words.Length) continue;
            result.Add(new(row.Index, row.Text("Id") ?? "", words[word].Text("Text") ?? "", words[word].UInt("HASH32"),
                row.Text("AOFile") ?? "", row.IntOrNull("MinLevel") ?? 0, weight, chest, row.Ints("ModsKeys")));
        }
        return result;
    }

    /// <summary>A unique box for this area level by weight, among those whose strongbox's level range holds it.</summary>
    public static (UniqueBox Unique, Definition Type)? RollUnique(int level, Random random)
    {
        var pool = Uniques.Where(u => level >= u.MinLevel)
            .Select(u => (Unique: u, Type: All.First(t => t.Row == u.ChestRow)))
            .Where(p => level >= p.Type.MinLevel && level <= p.Type.MaxLevel).ToArray();
        long total = pool.Sum(p => (long)p.Unique.Weight);
        if (total == 0) return null;
        long roll = random.NextInt64(total);
        foreach (var p in pool) if ((roll -= p.Unique.Weight) < 0) return p;
        return pool[^1];
    }
}
