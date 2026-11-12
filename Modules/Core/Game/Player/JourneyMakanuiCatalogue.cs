using System.Text.Json;
using GameServer.Core;


/// <summary>
/// Journey Tattoo of Makanui: the client can grant any base passive-tree keystone. The client's
/// <c>PassiveSkillOverrides</c> rows with Type 15 are the 48 ordinary keystone overrides; their
/// <c>AllocatedPassiveSkill</c> is the PassiveSkills row and <c>HASH16</c> is what 0x33's fifth list needs.
/// </summary>
public static class JourneyMakanuiCatalogue
{
    public sealed record Entry(ushort GraphId, string Name, int Row, ushort OverrideHash);

    static readonly Lazy<List<Entry>> Entries = new(Load);

    public static IReadOnlyList<Entry> All => Entries.Value;

    public static string[] Names => [.. All.Select(entry => entry.Name)];

    public static Entry? FindByGraphId(ushort graphId) =>
        All.FirstOrDefault(entry => entry.GraphId == graphId);

    public static Entry? Find(string query)
    {
        if (ushort.TryParse(query, out ushort graphId) && FindByGraphId(graphId) is { } numeric) return numeric;
        return All.FirstOrDefault(entry => entry.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
            ?? All.FirstOrDefault(entry => entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A random base keystone, excluding the target keystone (the tattoo always changes the node).</summary>
    public static Entry? Pick(ushort? exclude = null, Random? rng = null)
    {
        var candidates = exclude is ushort id ? All.Where(entry => entry.GraphId != id).ToList() : All.ToList();
        return candidates.Count == 0 ? null : candidates[(rng ?? Random.Shared).Next(candidates.Count)];
    }

    static List<Entry> Load()
    {
        var result = new List<Entry>();
        if (!ClientTables.Has("PassiveSkillOverrides")) return result;
        foreach (var row in ClientTables.Table("PassiveSkillOverrides"))
        {
            if (row.Int("Type") != 15) continue;
            if (row.IntOrNull("AllocatedPassiveSkill") is not int allocated) continue;
            int passiveRow = allocated;
            if (Program.Passives.GraphIdForRow(passiveRow) is not ushort graphId) continue;
            if (Program.Passives.Find(graphId) is not { IsKeystone: true } keystone) continue;
            ushort hash = (ushort)row.Int("HASH16");
            result.Add(new Entry(graphId, keystone.Name, passiveRow, hash));
        }
        return [.. result.DistinctBy(entry => entry.GraphId)];
    }
}
