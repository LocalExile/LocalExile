using System.Text.Json;
using GameServer.Core.Protocol.Instance;

/// <summary>Native collection categories and fixed cells, keyed by unique name and artwork, not base type.</summary>
sealed class UniqueStashLayout
{
    public sealed record Cell(string Name, ulong Category, byte X, byte Y, uint Word, ushort Visual);
    public IReadOnlyList<Cell> Cells { get; private init; } = [];
    public IReadOnlyDictionary<ulong, string> Categories { get; private init; } = new Dictionary<ulong, string>();
    public Cell? Find(Item item) => item.Rarity == ItemRarity.Unique
        ? Cells.FirstOrDefault(c => item.NameWords.Contains(c.Word) && item.VisualIdentity == c.Visual) : null;

    public static UniqueStashLayout Load(string folder)
    {
        string Table(string name) => Path.Combine(folder, "tables", name + ".json");
        if (new[] { "UniqueStashLayout", "UniqueStashTypes", "Words", "ItemVisualIdentity" }.Any(n => !File.Exists(Table(n)))) return new();
        using var layout = JsonDocument.Parse(File.ReadAllBytes(Table("UniqueStashLayout")));
        using var types = JsonDocument.Parse(File.ReadAllBytes(Table("UniqueStashTypes")));
        using var words = JsonDocument.Parse(File.ReadAllBytes(Table("Words")));
        using var visuals = JsonDocument.Parse(File.ReadAllBytes(Table("ItemVisualIdentity")));
        var categories = types.RootElement.EnumerateArray().Select((r, i) => (r, i))
            .Where(t => !t.r.GetProperty("IsDisabled").GetBoolean())
            .ToDictionary(t => (ulong)t.i + 1, t => t.r.GetProperty("Id").GetString()!);
        var cells = new List<Cell>();
        foreach (var r in layout.RootElement.EnumerateArray())
        {
            int word = r.GetProperty("WordsKey").GetInt32(), visual = r.GetProperty("ItemVisualIdentityKey").GetInt32();
            ulong category = (ulong)r.GetProperty("UniqueStashTypesKey").GetInt32() + 1;
            int x = r.GetProperty("__col3").GetInt32(), y = r.GetProperty("__col4").GetInt32();
            if (!categories.ContainsKey(category) || word < 0 || word >= words.RootElement.GetArrayLength()
                || visual < 0 || visual >= visuals.RootElement.GetArrayLength() || x < 0 || x > 253 || y < 0 || y > 3) continue;
            cells.Add(new(words.RootElement[word].GetProperty("Text").GetString()!, category, (byte)x, (byte)y,
                words.RootElement[word].GetProperty("HASH32").GetUInt32(), (ushort)visuals.RootElement[visual].GetProperty("HASH16").GetInt32()));
        }
        return new() { Cells = cells, Categories = categories };
    }
}
