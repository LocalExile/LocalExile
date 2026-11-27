using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Expedition;

public static class ExpeditionLogbooks
{
    public const string Type = "Metadata/Items/Expedition/ExpeditionLogbook";
    sealed record Location(ushort Hash, IReadOnlyList<int> Factions, IReadOnlySet<string> Tags);
    static IReadOnlyList<Location> locations = [];
    static IReadOnlyList<ModCatalog.Mod> implicits = [];
    static IReadOnlyList<string> factionTags = [];

    public static void LoadData()
    {
        var areas = ClientTables.TableOrEmpty("WorldAreas").ToArray();
        var tags = ClientTables.TableOrEmpty("Tags").Select(t => t.TextOrNull("Id") ?? "").ToArray();
        string Tag(int row) => row >= 0 && row < tags.Length ? tags[row] : "";
        locations = [.. ClientTables.TableOrEmpty("ExpeditionAreas")
            .Where(r => r.Index > 0 && r.IntOrNull("Area") is > 0 && r.Int("Area") < areas.Length)
            .Select(r => new Location((ushort)areas[r.Int("Area")].Int("HASH16"),
                r.Ints("__col4").Where(f => f is >= 0 and < 4).ToArray(),
                r.Ints("Tags").Select(Tag).Append("default").ToHashSet()))
            .Where(l => l.Factions.Count > 0)];
        factionTags = [.. ClientTables.TableOrEmpty("ExpeditionFactions").Select(f => Tag(f.Int("Tags")))];
        implicits = [.. Items.Mods.All.Where(m => m.GenerationType == 23)];
    }

    public static bool Ready => locations.Count > 0 && implicits.Count > 0 && Items.Find(Type) is { Supported: true };

    public static Item? Create(int level, Random rng, bool fourImplicits)
    {
        if (!Ready || Items.Find(Type) is not { } type) return null;
        var choices = locations.OrderBy(_ => rng.Next()).Take(rng.Next(2, 4)).ToArray();
        var areas = new List<ExpeditionSagaArea>();
        foreach (var location in choices)
        {
            int faction = location.Factions[rng.Next(location.Factions.Count)];
            var tags = location.Tags.ToHashSet();
            if (faction < factionTags.Count) tags.Add(factionTags[faction]);
            var pool = implicits.Where(m => m.Level <= level && m.MaxLevel >= level && m.WeightFor(tags) > 0).ToList();
            var mods = new List<ItemMod>();
            var families = new HashSet<int>();
            for (int i = 0; i < (fourImplicits ? 4 : 3); i++)
            {
                var available = pool.Where(m => !m.Families.Any(families.Contains)).ToArray();
                int total = available.Sum(m => m.WeightFor(tags));
                if (total == 0) break;
                int roll = rng.Next(total);
                var chosen = available.First(m => (roll -= m.WeightFor(tags)) < 0);
                mods.Add(chosen.Roll(ModSection.Implicit, rng));
                families.UnionWith(chosen.Families);
                pool.Remove(chosen);
            }
            areas.Add(new(location.Hash, (byte)faction, 0, mods));
        }
        return ItemCatalog.Create(type, random: rng) with
        {
            ItemLevel = (byte)Math.Clamp(level, 1, 100), Identified = true,
            ExpeditionSaga = new((byte)Math.Clamp(level, 1, 100), areas),
        };
    }
}
