using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>QuestRewardOffers and class-filtered QuestRewards. Row zero is a real offer/flag.</summary>
sealed class QuestRewardCatalog
{
    /// <summary>One choice of an offer (a QuestRewards row). <paramref name="Level"/> is a gem's level and other items' item
    /// level; <see cref="Rarity"/> is the row's rarity column (0 normal, 1 magic; none for gems).</summary>
    public sealed record Choice(uint Id, int ItemRow, int Level, int Stack, IReadOnlyList<int> Classes)
    {
        public int? Rarity { get; init; }
    }
    public sealed record Offer(int Row, string TakenFlag, IReadOnlyList<Choice> Choices);
    readonly Dictionary<int, Offer> offers = [];
    public Offer? Find(int row) => offers.GetValueOrDefault(row);
    public IReadOnlyList<Choice> Choices(int row, int classRow) =>
        Find(row) is { } offer ? [.. offer.Choices.Where(c => c.Classes.Count == 0 || c.Classes.Contains(classRow))] : [];

    public static QuestRewardCatalog Load(string folder = "data")
    {
        var result = new QuestRewardCatalog();
        string Table(string name) => File.ReadAllText(Path.Combine(folder, "tables", name + ".json"));
        using var flags = JsonDocument.Parse(Table("QuestFlags"));
        var names = flags.RootElement.EnumerateArray().Select(f => f.GetProperty("Id").GetString()!).ToArray();
        using var choices = JsonDocument.Parse(Table("QuestRewards"));
        var byOffer = choices.RootElement.EnumerateArray().GroupBy(r => r.GetProperty("RewardOffer").GetInt32())
            .ToDictionary(g => g.Key, g => g.Select(r => new Choice((uint)r.GetProperty("__col1").GetInt32() + 1,
                r.GetProperty("Reward").GetInt32(), r.GetProperty("RewardLevel").GetInt32(), r.GetProperty("RewardStack").GetInt32(),
                r.GetProperty("Characters").EnumerateArray().Select(c => c.GetInt32()).ToArray())
            {
                Rarity = r.TryGetProperty("__col5", out var rarity) && rarity.ValueKind == JsonValueKind.Number ? rarity.GetInt32() : null,
            }).ToArray());
        using var offers = JsonDocument.Parse(Table("QuestRewardOffers"));
        int row = 0;
        foreach (var r in offers.RootElement.EnumerateArray())
        {
            int index = row++;
            if (r.GetProperty("QuestFlag") is not { ValueKind: JsonValueKind.Number } flag) continue;
            int f = flag.GetInt32();
            if (f < 0 || f >= names.Length || names[f] == "NEVER_SET") continue;
            result.offers[index] = new(index, names[f], byOffer.GetValueOrDefault(index) ?? []);
        }
        return result;
    }

    /// <summary>The offers whose reward scripts are audited: Hillock's (0), Nessa's Medicine Chest flasks and support gems
    /// (a1q5: 1, 2) and her prison preparation (8). Other reward scripts need auditing.</summary>
    public static bool Implemented(int offer) => offer is 0 or MercyMission.RewardOffer or MercyMission.SupportRewardOffer or 8;

    // Grant only the simple items whose complete construction is modelled here: level-one gems and other level-one normal
    // items, and normal-rarity flasks at their row's item level (a1q5's Medium Life, Medium Mana and Quicksilver Flask:
    // level 4, rarity 0). Other quest rewards may require affixes (offer 8's magic armours: rarity 1), sockets, levels or
    // scripted effects; a table row alone does not implement those mechanics.
    public static Item? Create(Choice choice, ItemCatalog items)
    {
        if (choice.Stack <= 0 || items.Row(choice.ItemRow) is not { Supported: true } type) return null;
        bool normalFlask = type.Components.Contains("Flask") && choice.Rarity == 0 && choice.Level is >= 1 and <= 100;
        if (choice.Level != 1 && !normalFlask) return null;
        return ItemCatalog.Create(type) with { StackSize = (ushort)Math.Min(choice.Stack, ushort.MaxValue), ItemLevel = (byte)choice.Level };
    }

    public static PlayerInventories.Stacking? Claim(string character, string takenFlag, PlayerInventories inventory,
        Item reward, Func<Item, PlayerInventories.Shape?> shapeOf) => Saves.Atomically(() =>
    {
        // Read the durable flag again inside the transaction: a retry or another connection cannot duplicate a reward.
        if (CharacterSaves.Load(character)?.QuestFlags?.Contains(takenFlag) == true
            || !inventory.Fits(PlayerInventories.MainInventory, reward, shapeOf)) return null;
        var added = inventory.AddStacked(PlayerInventories.MainInventory, reward, shapeOf);
        if (added.Left != 0) throw new InvalidOperationException("A quest reward did not fit after its capacity check");
        CharacterSaves.ChangeQuestFlags(character, [takenFlag], true);
        return added;
    });
}
