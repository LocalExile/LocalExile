using System.Text.Json;
using System.Text.Json.Serialization;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Core.Accounts
{
    public enum CharacterClass { Scion, Marauder, Ranger, Witch, Duelist, Templar, Shadow }
}

namespace GameServer.Core.Protocol.Instance
{
    public enum SocketColour : byte { Red = 1, Green = 2, Blue = 3, White = 4, Abyss = 5, Delve = 6 }
    [Flags] public enum ItemInfluence : byte { None = 0, Shaper = 1, Elder = 2, Crusader = 4, Redeemer = 8, Hunter = 16, Warlord = 32 }
    public enum ItemRarity : byte { Normal, Magic, Rare, Unique }
    public enum ModSection : byte { Implicit = 4, Explicit = 5, Enchant = 6, Scourge = 10, Hidden = 11 }
    public sealed record ItemSocket(SocketColour Colour, Item? Gem = null);
    public sealed record ItemMod(ModSection Section, ushort Hash, IReadOnlyList<int> Values);
    // Export-time representation only. It neither runs gameplay nor serializes network messages.
    public sealed record Item(uint TypeHash, IReadOnlyList<string> Components, ushort VisualIdentity)
    {
        public const byte MaximumDefenceBaseRoll = 31, SpecialDefenceBaseRoll = 32;
        public byte DefenceBaseRoll { get; init; }
        public byte ItemLevel { get; init; } = 1;
        public byte Quality { get; init; }
        public int Charges { get; init; }
        public ushort StackSize { get; init; } = 1;
        public IReadOnlyList<ItemSocket> Sockets { get; init; } = [];
        public Item? OriginalItem { get; init; }
        public bool NativeMods { get; init; }
        public bool NativeSockets { get; init; }
        public IReadOnlyList<byte>? Links { get; init; }
        public int GemExperience { get; init; }
        public int GemVariant { get; init; }
        public byte GemFlags { get; init; }
        public ItemRarity Rarity { get; init; }
        public bool Identified { get; init; } = true;
        public bool Corrupted { get; init; }
        public byte FracturedCount { get; init; }
        public bool Mirrored { get; init; }
        public ItemInfluence Influences { get; init; }
        public bool Split { get; init; }
        public bool Synthesised { get; init; }
        public IReadOnlyList<ItemMod> Mods { get; init; } = [];
        public IReadOnlyList<uint> NameWords { get; init; } = [];
        static readonly HashSet<string> Supported = ["Transformed", "Tincture", "LocalStats", "AttributeRequirements", "Weapon", "Usable", "Flask", "Quest", "Shield", "RenderItem", "Base", "Mods", "Armour", "Quality", "Sockets", "Charges", "Stack", "SkillGem", "Map", "MapKey"];
        public static bool CanSerialize(IEnumerable<string> components) => components.All(Supported.Contains);
    }
}

static class CharacterSaves
{
    public sealed record SavedSocket(SocketColour Colour, SavedItem? Gem = null);
    public sealed record SavedMod(string Id, ModSection Section, List<int> Values);
    // Exact upstream save fields. A server older than DefenceBaseRoll ignores it; a newer one reads a missing value as the minimum.
    public sealed record SavedItem(uint Type, byte ItemLevel = 1, byte Quality = 0, int Charges = 0, ushort StackSize = 1,
        List<SavedSocket>? Sockets = null, List<byte>? Links = null, int GemExperience = 0,
        ItemRarity Rarity = ItemRarity.Normal, bool Identified = true, List<SavedMod>? Mods = null, List<uint>? Name = null,
        int GemVariant = 0, bool Corrupted = false, byte Fractured = 0, bool Mirrored = false, byte GemFlags = 0, ushort? Visual = null,
        byte DefenceBaseRoll = 0, ItemInfluence Influences = ItemInfluence.None, bool Split = false, bool Synthesised = false, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SavedItem? OriginalItem = null);
    public sealed record SavedPlacement(uint Id, byte X, byte Y, SavedItem Item);
    // Tattoos: passive node -> tattoo item name; TattooKeystones: the keystone Journey Tattoo of Makanui granted, by node.
    // Both are upstream save fields, omitted when empty so a save without tattoos is byte-for-byte what it was.
    public sealed record Save(Dictionary<byte, List<SavedPlacement>> Inventories, Dictionary<int, string>? SkillBar = null,
        uint? Experience = null, List<ushort>? Passives = null, List<string>? Waypoints = null, List<string>? Visited = null,
        ushort? ExtraPassivePoints = null, Dictionary<ushort, ushort>? MasteryEffects = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<ushort, string>? Tattoos = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<ushort, ushort>? TattooKeystones = null);
    public static SavedItem ToSaved(Item item, ModCatalog mods) => new(item.TypeHash, item.ItemLevel, item.Quality,
        item.Charges, item.StackSize, [.. item.Sockets.Select(s => new SavedSocket(s.Colour, s.Gem is null ? null : ToSaved(s.Gem, mods)))],
        item.Links is null ? null : [.. item.Links], item.GemExperience, item.Rarity, item.Identified,
        [.. item.Mods.Select(m => new SavedMod(mods.ByHash(m.Hash)?.Id ?? throw new ArgumentException("Unknown native modifier"), m.Section, [.. m.Values]))],
        [.. item.NameWords], item.GemVariant, item.Corrupted, item.FracturedCount, item.Mirrored, item.GemFlags,
        item.Rarity == ItemRarity.Unique ? item.VisualIdentity : null, item.DefenceBaseRoll, item.Influences, item.Split, item.Synthesised, item.OriginalItem is null ? null : ToSaved(item.OriginalItem, mods));
}

static class ItemModStats
{
    public static Dictionary<string, int> Local(Item item, ModCatalog mods)
    {
        var result = new Dictionary<string, int>();
        foreach (var mod in item.Mods)
            if (mods.ByHash(mod.Hash) is { } row)
                for (int i = 0; i < row.Stats.Count; i++)
                    if (row.Stats[i].Local) result[row.Stats[i].Id] = ActorScale.Add(0, result.GetValueOrDefault(row.Stats[i].Id), mod.Values[i]);
        return result;
    }
    public static int LevelRequirement(Item item, int dropLevel, ModCatalog mods)
    {
        var local = Local(item, mods);
        if (local.GetValueOrDefault("local_level_requirement_override") is int over and not 0) return Math.Max(0, over);
        int required = Math.Max(dropLevel, item.Mods.Select(m => (mods.ByHash(m.Hash)?.Level ?? 0) * 4 / 5).DefaultIfEmpty(0).Max());
        return Math.Max(0, required + local.GetValueOrDefault("local_level_requirement_+"));
    }
}
static class ActorScale
{
    public static int Add(uint key, int a, int b) => (int)Math.Clamp((long)a + b, int.MinValue, int.MaxValue);
}
static class PlayerInventories
{
    public const byte Flask = 11;
    public static bool Accepts(byte row, string type) => row switch
    {
        1 => type == "Body Armour", 4 => type == "Helmet", 5 => type == "Amulet", 6 or 7 or 102 => type == "Ring",
        8 => type == "Gloves", 9 => type == "Boots", 10 => type == "Belt",
        11 => type is "LifeFlask" or "ManaFlask" or "HybridFlask" or "UtilityFlask" or "UtilityFlaskCritical" or "Tincture",
        2 or 14 => Weapons.Contains(type), 3 or 15 => Weapons.Contains(type) || type is "Shield" or "Quiver", _ => false
    };
    static readonly HashSet<string> Weapons = ["Wand", "Dagger", "Rune Dagger", "Claw", "One Hand Sword", "Thrusting One Hand Sword", "One Hand Axe", "One Hand Mace", "Sceptre", "Two Hand Sword", "Two Hand Axe", "Two Hand Mace", "Bow", "Staff", "Warstaff", "FishingRod"];
}
static class CurrencyEffects
{
    public static int SocketCapacity(Item item, ItemCatalog.BaseType type)
    {
        if (!item.Components.Contains("Sockets") || !type.Components.Contains("Sockets")) return 0;
        int maximum = type.Class switch
        {
            "Body Armour" or "Bow" or "Staff" or "Warstaff" or "Two Hand Sword" or "Two Hand Axe" or "Two Hand Mace" or "FishingRod" => 6,
            "Helmet" or "Gloves" or "Boots" => 4,
            "Shield" or "Claw" or "Dagger" or "Rune Dagger" or "Wand" or "One Hand Sword" or "Thrusting One Hand Sword" or "One Hand Axe" or "One Hand Mace" or "Sceptre" => 3,
            _ => 0
        };
        return Math.Min(maximum, type.Width * type.Height);
    }
}

static class NativeGraph
{
    public static IReadOnlyDictionary<uint, uint[]> Load(string folder)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "passive-skill-graphs.json")));
        if (doc.RootElement.GetProperty("revision").GetString() != "3.29.3b") throw new ArgumentException("Passive graph data must be 3.29.3b.");
        return doc.RootElement.GetProperty("normal").GetProperty("nodes").EnumerateArray()
            .ToDictionary(n => n.GetProperty("id").GetUInt32(), n => n.GetProperty("adjacent").EnumerateArray().Select(v => v.GetUInt32()).ToArray());
    }
}
static class PathOfBuildingPassiveGraph
{
    public static HashSet<ushort> Connected(uint start, IReadOnlyCollection<ushort> allocated,
        Func<ushort, bool> mastery, IReadOnlyDictionary<uint, uint[]> graph)
    {
        var requested = allocated.Select(n => (uint)n).ToHashSet();
        var reached = new HashSet<uint> { start }; var queue = new Queue<uint>(); queue.Enqueue(start);
        while (queue.TryDequeue(out uint current))
            if (current > ushort.MaxValue || !mastery((ushort)current))
                foreach (uint next in graph.GetValueOrDefault(current) ?? [])
                    if (requested.Contains(next) && graph.ContainsKey(next) && reached.Add(next)) queue.Enqueue(next);
        return allocated.Where(n => reached.Contains(n)).ToHashSet();
    }
}
