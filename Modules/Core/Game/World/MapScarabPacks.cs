using GameServer.Core;
using GameServer.Modding;

/// <summary>Scarab encounters expressed by the client's additional-pack table.</summary>
public static class MapScarabPacks
{
    public const string Cloister = "CloisterPacks", CountStat = "map_contains_X_additional_div_card_packs";
    const string DeckChance = "map_putrid_monster_%_chance_to_drop_stack_deck";
    public const string Deck = "Metadata/Items/DivinationCards/DivinationCardDeck";

    static readonly Lazy<MapRunCatalog> Fragments = new(() => MapRunCatalog.Load("data"));
    static readonly Lazy<IReadOnlyList<PackCatalog.Pack>> CloisterPacks = new(() =>
    {
        if (!ClientTables.Has("AdditionalMonsterPacksFromStats")) return [];
        int stat = (int)(Program.Stats.Key(CountStat) ?? 0) - 1;
        var rows = ClientTables.Table("AdditionalMonsterPacksFromStats").Where(r => r.IntOrNull("StatsKey") == stat
            && r.IntOrNull("__col1") == 2 && r.IntOrNull("AdditionalMonsterPacksStatMode") == 0).ToList();
        var names = ClientTables.Table("MonsterPacks").Select(r => r.Text("Id") ?? "").ToList();
        return [.. rows.SelectMany(r => r.Ints("MonsterPacksKeys")).Distinct()
            .Select(i => i >= 0 && i < names.Count ? Game.Packs.Find(names[i]) : null).OfType<PackCatalog.Pack>()];
    });

    public static IReadOnlyList<PackCatalog.Pack> Packs => CloisterPacks.Value;
    static bool Ready => Packs.Count > 0 && Packs.All(p => p.Entries.Count > 0
        && p.Entries.Select(e => e.Variety).Concat(p.BossVarieties).All(v => Game.Monsters.Find(v.ToString()) is { Supported: true }))
        && Program.Items.Find(Deck) is { Supported: true };

    public static void Opening(MapOpening request) => Opening(request, Random.Shared);

    public static void Opening(MapOpening request, Random random)
    {
        if (request.Campaign || request.Opening is not { } opening || !Ready) return;
        request.HandledContent.Add(Cloister);
        RollFragmentCount(request, CountStat, random);
    }

    public static void RollFragmentCount(MapOpening request, string name, Random random)
    {
        if (request.Opening is not { } opening) return;
        int count = 0;
        foreach (var slot in request.Slots.Where(s => !s.IsMap))
            foreach (int row in Fragments.Value.FragmentOf(slot.BaseRow)?.ModRows ?? [])
                foreach (var stat in Program.Items.Mods.Row(row)?.Stats ?? [])
                    if (stat.Id == name) count = checked(count + random.Next(stat.Min, checked(stat.Max + 1)));
        if (count > 0)
            request.Opening = opening with { Mods = opening.Mods.WithFragmentStat(name, count) };
    }

    public static IReadOnlyList<AreaPopulation.AdditionalPacks> Requests(MapMods? mods) =>
        mods?.Get(CountStat) is int count and > 0 && Ready
            ? [new(CountStat, Packs, count, mods.PackSizeFactor)] : [];

    public static bool DropsDeck(MapMods? mods, string variety, Random random) => mods?.Get(DeckChance) is int chance and > 0
        && Packs.Any(p => p.Entries.Any(e => Game.Monsters.Find(e.Variety.ToString())?.Id == variety))
        && random.Next(100) < chance;

    public static void Drops(DropRoll roll)
    {
        if (DropsDeck(Game.MapModsOf(roll.Conn), roll.Monster.VarietyId, Random.Shared)) roll.Items.Add(Deck);
    }
}
