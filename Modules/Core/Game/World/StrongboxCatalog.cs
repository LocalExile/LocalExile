using GameServer.Core;
using GameServer.Core.Protocol.Instance;

/// <summary>Strongbox types, selection weights, level limits and native object data from the client tables.</summary>
public static class StrongboxCatalog
{
    public sealed record Definition(int Row, string Id, string Name, int Weight, int HardModeWeight, int MinLevel, int MaxLevel,
        bool RareType, string? WeightStat, string Model, IReadOnlyList<string> Components, StateMachineData? States,
        IReadOnlyList<ItemMod> Mods);

    static readonly Lazy<IReadOnlyList<Definition>> Data = new(Load);
    public static IReadOnlyList<Definition> All => Data.Value;
    public static Definition? Find(string id) => All.FirstOrDefault(r => r.Id == id);

    static IReadOnlyList<Definition> Load()
    {
        if (!ClientTables.Has("Strongboxes") || !ClientTables.Has("Chests")) return [];
        var chests = ClientTables.Table("Chests").ToArray();
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
            result.Add(new(index, id, chest.Text("Name") ?? id, row.Int("SpawnWeight"), row.Int("SpawnWeightHardmode"),
                chest.Int("MinLevel"), chest.Int("MaxLevel"), row.BoolOrNull("__col7") == true,
                stat is int key && key >= 0 && key < stats.Count ? stats[key] : null, model, components, states, mods));
        }
        return result;
    }

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
        new QuestObject(x, y, type.Components, Model: type.Model, Opened: opened)
        {
            StateMachine = type.States?.Set("started", started ? 1 : 0).Set("opened", opened ? 1 : 0),
            Mods = mods ?? type.Mods, Rarity = rarity, Stats = stats ?? [],
        }.Blob();
}
