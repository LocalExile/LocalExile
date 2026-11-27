using GameServer.Core;
using GameServer.Modding;

/// <summary>Scarab encounters expressed by the client's additional-pack table.</summary>
public static class MapScarabPacks
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadGameData()
    {
        Fragments = LoadFragments();
        CloisterPacks = LoadPacks(CountStat);
        IncursionPacks = LoadPacks(IncursionCountStat);
    }

    public const string Cloister = "CloisterPacks", Incursion = "Incursion.Invasion";
    public const uint IncursionCountStat = Stat.MapContainsXAdditionalIncursionPacks;
    public const uint CountStat = Stat.MapContainsXAdditionalDivCardPacks;
    const uint DeckChance = Stat.MapPutridMonsterPctChanceToDropStackDeck;
    public const string Deck = "Metadata/Items/DivinationCards/DivinationCardDeck";

    static MapRunCatalog Fragments = default!;
    static MapRunCatalog LoadFragments() => MapRunCatalog.Load("data");
    static IReadOnlyList<PackCatalog.Pack> CloisterPacks = default!, IncursionPacks = default!;

    public static IReadOnlyList<PackCatalog.Pack> LoadPacks(uint countStat)
    {
        if (!ClientTables.Has("AdditionalMonsterPacksFromStats")) return [];
        int stat = (int)countStat - 1;
        var rows = ClientTables.Table("AdditionalMonsterPacksFromStats").Where(r => r.IntOrNull("StatsKey") == stat
            && r.IntOrNull("__col1") == 2 && r.IntOrNull("AdditionalMonsterPacksStatMode") == 0).ToList();
        var names = ClientTables.Table("MonsterPacks").Select(r => r.Text("Id") ?? "").ToList();
        return [.. rows.SelectMany(r => r.Ints("MonsterPacksKeys")).Distinct()
            .Select(i => i >= 0 && i < names.Count ? Game.Packs.Find(names[i]) : null).OfType<PackCatalog.Pack>()];
    }

    public static IReadOnlyList<PackCatalog.Pack> Packs => CloisterPacks;
    static bool Ready => Packs.Count > 0 && Packs.All(p => p.Entries.Count > 0
        && p.Entries.Select(e => e.Variety).Concat(p.BossVarieties).All(v => Game.Monsters.Find(v.ToString()) is { Supported: true }))
        && Program.Items.Find(Deck) is { Supported: true };

    public static void Opening(MapOpening request) => Opening(request, Game.AreaRng(request.Player));

    public static void Opening(MapOpening request, Random random)
    {
        if (request.Campaign || request.Opening is not { } opening) return;
        if (Ready)
        {
            request.HandledContent.Add(Cloister);
            RollFragmentCount(request, CountStat, random);
        }
        if (ReadyPacks(IncursionPacks))
        {
            request.HandledContent.Add(Incursion);
            RollFragmentCount(request, IncursionCountStat, random);
        }
    }

    public static void RollFragmentCount(MapOpening request, uint name, Random random)
    {
        if (request.Opening is not { } opening) return;
        int count = 0;
        foreach (var slot in request.Slots.Where(s => !s.IsMap))
            foreach (int row in Fragments.FragmentOf(slot.BaseRow)?.ModRows ?? [])
                foreach (var stat in Program.Items.Mods.Row(row)?.Stats ?? [])
                    if (stat.Id == name) count = checked(count + random.Next(stat.Min, checked(stat.Max + 1)));
        if (count > 0)
            request.Opening = opening with { Mods = opening.Mods.WithFragmentStat(name, count) };
    }

    static bool ReadyPacks(IReadOnlyList<PackCatalog.Pack> packs) => packs.Count > 0
        && packs.All(p => p.Entries.Count > 0 && p.Entries.Select(e => e.Variety).Concat(p.BossVarieties)
            .All(v => Game.Monsters.Find(v.ToString()) is { Supported: true }));

    public static IReadOnlyList<AreaPopulation.AdditionalPacks> Requests(MapMods? mods)
    {
        var result = new List<AreaPopulation.AdditionalPacks>();
        if (mods is null) return result;
        if (mods.Get(CountStat) is int cloister and > 0 && Ready)
            result.Add(new(Program.Stats.Name(CountStat), Packs, cloister, mods.PackSizeFactor));
        if (mods.Get(IncursionCountStat) is int incursion and > 0 && ReadyPacks(IncursionPacks))
            result.Add(new(Program.Stats.Name(IncursionCountStat), IncursionPacks, incursion, mods.PackSizeFactor));
        return result;
    }

    public static bool DropsDeck(MapMods? mods, string variety, Random random) => mods?.Get(DeckChance) is int chance and > 0
        && Packs.Any(p => p.Entries.Any(e => Game.Monsters.Find(e.Variety.ToString())?.Id == variety))
        && random.Next(100) < chance;

    public static void Drops(DropRoll roll)
    {
        if (DropsDeck(Game.MapModsOf(roll.Player), roll.Monster.VarietyId, Game.AreaRng(roll.Player))) roll.Items.Add(Deck);
    }
}
