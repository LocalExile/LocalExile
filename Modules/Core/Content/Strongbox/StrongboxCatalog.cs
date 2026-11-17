using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Strongbox;

/// <summary>
/// The strongbox types, from the client tables: a <c>Strongboxes</c> row (its spawn weights, the area stat that raises its
/// weight, <c>__col2</c>) and the <c>Chests</c> row it names (metadata path, name, model, level range, tags, chest mods).
/// The object a type is created as is its own <c>object_types</c> row, else its <c>Chests.InheritsFrom</c> one (the
/// HardMode rows have no .ot of their own), with the deserialized components and StateMachine defaults of that row.
/// </summary>
public static class StrongboxCatalog
{
    /// <summary>What a box drops: from its Chests tags (armour, weapon, jewellery, gem, map, divination_card, scarab),
    /// else from its id (Arcanist: currency, Artisan: quality currency), else the general drop table.</summary>
    public enum Contents { General, Currency, QualityCurrency, Armour, Weapon, Jewellery, Gem, Map, Card, Scarab }

    /// <param name="Items">The Strongboxes row's <c>__col2</c> (5, 10, 12, 15): read as the box's base item count
    /// (assumed: Armourer's 15, Blacksmith's 12, Large and Ornate 10, the rest 5).</param>
    /// <param name="WeightStat">The area stat (client key) that raises the weight in percent, or 0.</param>
    /// <param name="HiddenRarity">The chest mods' <c>chest_hidden_item_rarity_+%</c> (Ornate: 500).</param>
    /// <param name="SummonRarity">The chest mods' <c>summoned_monster_rarity</c> (ChestSummonNormals: normal).</param>
    public sealed record Entry(int Row, string Type, string Name, string Model, IReadOnlyList<string> Components,
        StateMachineData? States, int Size, int Weight, int WeightHardMode, uint WeightStat, int MinLevel, int MaxLevel,
        int Items, Contents Kind, int HiddenRarity, MonsterRarity SummonRarity)
    {
        /// <summary>Whether the object can be written: QuestObject's components and a StateMachine.</summary>
        public bool Supported => States is not null && QuestObject.CanSerialize(Components, true);

        /// <summary>The object's blob, closed or opened. Opened sets the StateMachine's <c>opened</c> (the client's
        /// <c>on_or_create_state_opened_1</c> hides the minimap icon) and the Chest component's opened bit.</summary>
        public byte[] Blob(int x, int y, float rotation, bool opened) =>
            new QuestObject(x, y, Components, rotation, null, Model, 1, opened)
            {
                StateMachine = opened ? States!.Set("opened", 1) : States,
            }.Blob();
    }

    static readonly Lazy<IReadOnlyList<Entry>> Loaded = new(Load);

    public static IReadOnlyList<Entry> All => Loaded.Value;

    public static Entry? ByRow(int row) => All.FirstOrDefault(e => e.Row == row);

    static IReadOnlyList<Entry> Load()
    {
        if (!ClientTables.Has("Strongboxes") || !ClientTables.Has("Chests")) return [];
        var chests = ClientTables.Table("Chests");
        var mods = ClientTables.TableOrEmpty("Mods");
        var tags = ClientTables.TableOrEmpty("Tags");
        var types = ObjectTypes.All().GroupBy(t => t.Type).ToDictionary(g => g.Key, g => g.First().Row, StringComparer.Ordinal);
        var shared = ObjectTypes.SharedStates();
        var result = new List<Entry>();
        foreach (var row in ClientTables.Table("Strongboxes"))
        {
            if (row.IntOrNull("ChestsKey") is not int chestRow || chests.Row(chestRow) is not { } chest) continue;
            string id = chest.Text("Id") ?? "";
            string? model = chest.Texts("AOFiles").FirstOrDefault();
            if (id.Length == 0 || model is null) continue;
            if (!types.TryGetValue(id, out var type) && !(chest.Text("InheritsFrom") is { } parent && types.TryGetValue(parent, out type)))
                continue;
            int size = type.TextOrNull("positioned_object_size") is { } os && int.TryParse(os, out int s) ? Math.Max(1, s - 1) : 1;
            var chestTags = chest.Ints("TagsKeys").Select(t => tags.Row(t)?.Text("Id") ?? "").ToHashSet(StringComparer.Ordinal);
            int hiddenRarity = 0;
            var summon = MonsterRarity.Normal;
            foreach (int m in chest.Ints("ModsKeys"))
                if (mods.Row(m) is { } mod)
                    for (int i = 1; i <= 6; i++)
                    {
                        if (mod.IntOrNull($"StatsKey{i}") is not int stat || mod.IntOrNull($"Stat{i}Min") is not int value) continue;
                        string name = Stats.Name((uint)stat + 1);
                        if (name == "chest_hidden_item_rarity_+%") hiddenRarity += value;
                        else if (name == "summoned_monster_rarity") summon = (MonsterRarity)Math.Clamp(value, 0, 2);
                    }
            int max = chest.IntOrNull("MaxLevel") is int top and > 0 ? top : 100;
            result.Add(new Entry(row.Index, id, chest.Text("Name") ?? id, model, ObjectTypes.Components(type),
                ObjectTypes.StateMachine(type, shared), size, row.IntOrNull("SpawnWeight") ?? 0, row.IntOrNull("SpawnWeightHardmode") ?? 0,
                row.IntOrNull("SpawnWeightIncrease") is int increase ? (uint)increase + 1 : 0,
                chest.IntOrNull("MinLevel") ?? 0, max, Math.Max(1, row.IntOrNull("__col2") ?? 1), KindOf(id, chestTags),
                hiddenRarity, summon));
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
        : id.Contains("/Arcanist", StringComparison.Ordinal) ? Contents.Currency
        : id.Contains("/Artisan", StringComparison.Ordinal) ? Contents.QualityCurrency
        : Contents.General;
}
