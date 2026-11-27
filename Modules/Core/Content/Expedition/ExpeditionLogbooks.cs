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
        factionTags = [.. ClientTables.TableOrEmpty("ExpeditionFactions").Select(f => Tag(f.IntOrNull("Tags") ?? -1))];
        int[] factions = [.. Enumerable.Range(0, Math.Min(4, factionTags.Count))];
        locations = [.. ClientTables.TableOrEmpty("ExpeditionAreas")
            .Where(r => r.Index > 0 && r.IntOrNull("Area") is > 0 && r.Int("Area") < areas.Length)
            .Select(r => new Location((ushort)areas[r.Int("Area")].Int("HASH16"),
                factions,
                r.Ints("Tags").Select(Tag).Append("default").ToHashSet()))
            .Where(l => l.Factions.Count > 0)];
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
            areas.Add(new(location.Hash, (byte)faction, 0, Roll(location, faction, level, rng, fourImplicits ? 4 : 3, [])));
        }
        return ItemCatalog.Create(type, random: rng) with
        {
            ItemLevel = (byte)Math.Clamp(level, 1, 100), Identified = true,
            ExpeditionSaga = new((byte)Math.Clamp(level, 1, 100), areas),
        };
    }

    static IReadOnlyList<ItemMod> Roll(Location location, int faction, int level, Random rng, int count, IReadOnlyList<ItemMod> existing)
    {
        var tags = location.Tags.ToHashSet();
        if (faction < factionTags.Count) tags.Add(factionTags[faction]);
        var pool = implicits.Where(m => m.Level <= level && m.MaxLevel >= level && m.WeightFor(tags) > 0).ToList();
        var mods = existing.ToList();
        var families = existing.SelectMany(m => Items.Mods.ByHash(m.Hash)?.Families ?? []).ToHashSet();
        for (int i = mods.Count; i < count; i++)
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
        return mods;
    }

    public static Item Prepare(Player player, Item item)
    {
        if (Items.ByHash(item.TypeHash)?.Id != Type || MapModsOf(player)?.Get(Stat.MapExpeditionPctChanceLogbooksDropWithFourImplicits) is not int chance
            || chance <= 0 || !Ready) return item;
        var rng = AreaRng(player);
        if (rng.Next(100) >= chance) return item;
        var saga = item.ExpeditionSaga ?? Create(item.ItemLevel, rng, true)?.ExpeditionSaga;
        if (saga is null) return item;
        return item with { ExpeditionSaga = saga with { Areas = [.. saga.Areas.Select(area =>
            area.Implicits.Count >= 4 || locations.FirstOrDefault(l => l.Hash == area.AreaHash) is not { } location ? area
                : area with { Implicits = Roll(location, area.Faction, saga.AreaLevel, rng, 4, area.Implicits) })] } };
    }
}
