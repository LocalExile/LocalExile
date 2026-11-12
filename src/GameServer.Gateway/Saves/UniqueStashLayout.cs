using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core;

/// <summary>Native collection categories and fixed cells, keyed by unique name and artwork, not base type.</summary>
public sealed class UniqueStashLayout
{
    public sealed record Cell(string Name, ulong Category, byte X, byte Y, uint Word, ushort Visual);
    public IReadOnlyList<Cell> Cells { get; private init; } = [];
    public IReadOnlyDictionary<ulong, string> Categories { get; private init; } = new Dictionary<ulong, string>();
    public Cell? Find(Item item) => item.Rarity == ItemRarity.Unique
        ? Cells.FirstOrDefault(c => item.NameWords.Contains(c.Word) && item.VisualIdentity == c.Visual) : null;

    public static UniqueStashLayout Load(string folder)
    {
        if (new[] { "UniqueStashLayout", "UniqueStashTypes", "Words", "ItemVisualIdentity" }.Any(n => !ClientTables.Has(n))) return new();
        var layout = ClientTables.Table("UniqueStashLayout");
        var types = ClientTables.Table("UniqueStashTypes");
        var words = ClientTables.Table("Words");
        var visuals = ClientTables.Table("ItemVisualIdentity");
        var categories = types.Select((r, i) => (r, i))
            .Where(t => !t.r.Bool("IsDisabled"))
            .ToDictionary(t => (ulong)t.i + 1, t => t.r.Text("Id")!);
        var cells = new List<Cell>();
        foreach (var r in layout)
        {
            int word = r.Int("WordsKey"), visual = r.Int("ItemVisualIdentityKey");
            ulong category = (ulong)r.Int("UniqueStashTypesKey") + 1;
            int x = r.Int("__col3"), y = r.Int("__col4");
            if (!categories.ContainsKey(category) || word < 0 || word >= words.Count
                || visual < 0 || visual >= visuals.Count || x < 0 || x > 253 || y < 0 || y > 3) continue;
            cells.Add(new(words[word].Text("Text")!, category, (byte)x, (byte)y,
                words[word].UInt("HASH32"), (ushort)visuals[visual].Int("HASH16")));
        }
        return new() { Cells = cells, Categories = categories };
    }
}
