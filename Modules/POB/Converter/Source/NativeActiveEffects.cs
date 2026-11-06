using System.Text.Json;

static class NativeActiveEffects
{
    static readonly Lazy<HashSet<int>> Active = new(() =>
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes("data/tables/GrantedEffects.json"));
        return doc.RootElement.EnumerateArray().Select((r, i) => (Row: r, Index: i))
            .Where(r => !r.Row.GetProperty("IsSupport").GetBoolean() && r.Row.GetProperty("ActiveSkill").ValueKind == JsonValueKind.Number)
            .Select(r => r.Index).ToHashSet();
    });
    public static bool Contains(int row) => Active.Value.Contains(row);
}
