using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>QuestRewardOffers and class-filtered QuestRewards. Row zero is a real offer/flag.</summary>
sealed class QuestRewardCatalog
{
    public sealed record Choice(uint Id, int ItemRow, int Level, int Stack, IReadOnlyList<int> Classes);
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
                r.GetProperty("Characters").EnumerateArray().Select(c => c.GetInt32()).ToArray())).ToArray());
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

    // Grant only the simple level-one normal items whose complete construction is modelled here. Other quest rewards
    // may require affixes, sockets, levels or scripted effects; a table row alone does not implement those mechanics.
    public static Item? Create(Choice choice, ItemCatalog items) => choice.Level == 1 && choice.Stack > 0
        && items.Row(choice.ItemRow) is { Supported: true } type
        ? ItemCatalog.Create(type) with { StackSize = (ushort)Math.Min(choice.Stack, ushort.MaxValue) } : null;

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
