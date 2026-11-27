using System.Collections.Frozen;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

/// <summary>Shaper/Elder fragment flags, influenced packs and the standalone Interference companion.</summary>
public static class MapInfluence
{
    public const string Shaper = "Influence.Shaper", Elder = "Influence.Elder";
    // Density is server calibration; the client describes packs and sizes, but does not publish a total.
    const int BasePacks = 10;
    static IReadOnlyDictionary<int, string[]> FragmentFlags = FrozenDictionary<int, string[]>.Empty;
    static IReadOnlyDictionary<uint, IReadOnlyList<PackCatalog.Pack>> Pools = FrozenDictionary<uint, IReadOnlyList<PackCatalog.Pack>>.Empty;
    static IReadOnlyDictionary<string, (ItemInfluence Influence, int Quantity)> Loot = FrozenDictionary<string, (ItemInfluence, int)>.Empty;
    static IReadOnlyDictionary<string, HashSet<int>> AreaBosses = FrozenDictionary<string, HashSet<int>>.Empty;
    static IReadOnlyDictionary<int, int> PackInfluences = FrozenDictionary<int, int>.Empty;
    static IReadOnlyDictionary<int, MonsterModEffects> DropEffects = FrozenDictionary<int, MonsterModEffects>.Empty;
    public static IReadOnlyList<MonsterCatalog.Variety> Companions { get; private set; } = [];

    public static void LoadGameData()
    {
        FragmentFlags = ClientTables.Table("MapFragmentMods").Where(r => r.IntOrNull("BaseItemTypesKey") is not null)
            .ToFrozenDictionary(r => r.Int("BaseItemTypesKey"), r => new[] { r.BoolOrNull("__col5") == true ? Shaper : "", r.BoolOrNull("__col6") == true ? Elder : "" }.Where(s => s.Length > 0).ToArray());
        var packs = ClientTables.Table("MonsterPacks");
        Pools = new[] { Stat.MapShaperInfluenceAdditionalPacksOfSpaceMonsters, Stat.MapElderInfluenceAdditionalPacksOfElderMonsters }
            .ToFrozenDictionary(s => s, s => (IReadOnlyList<PackCatalog.Pack>)[.. ClientTables.Table("AdditionalMonsterPacksFromStats")
                .Where(r => r.IntOrNull("StatsKey") == (int)s - 1 && r.IntOrNull("AdditionalMonsterPacksStatMode") == 0)
                .SelectMany(r => r.Ints("MonsterPacksKeys")).Distinct().Select(k => Game.Packs.Find(packs[k].Text("Id") ?? ""))
                .OfType<PackCatalog.Pack>()]);
        PackInfluences = Pools.SelectMany(p => p.Value.SelectMany(pack => pack.Entries.Select(e => e.Variety).Concat(pack.BossVarieties))
            .Select(row => (Row: row, Influence: p.Key == Stat.MapShaperInfluenceAdditionalPacksOfSpaceMonsters ? 1 : 2)))
            .DistinctBy(p => p.Row).ToFrozenDictionary(p => p.Row, p => p.Influence);
        DropEffects = new[] { (1, "MonsterDroppedItemsInfluenceShaper"), (2, "MonsterDroppedItemsInfluenceElder") }
            .ToFrozenDictionary(p => p.Item1, p => new MonsterModEffects(Program.Items.Mods.All.Single(m => m.Id == p.Item2).Stats.ToDictionary(s => s.Id, s => s.Min)));
        Loot = ClientTables.Table("MonsterVarieties").Select(r =>
        {
            var stats = new Dictionary<uint, int>();
            foreach (int key in r.Ints("ModsKeys").Concat(r.Ints("Special_ModsKeys")))
                foreach (var s in Program.Items.Mods.Row(key)?.Stats ?? []) stats[s.Id] = stats.GetValueOrDefault(s.Id) + s.Min;
            var influence = stats.GetValueOrDefault(Stat.DroppedItemsHaveInfluenceType) switch { 1 => ItemInfluence.Shaper, 2 => ItemInfluence.Elder, _ => ItemInfluence.None };
            return (Id: r.Text("Id") ?? "", Influence: influence, Quantity: stats.GetValueOrDefault(Stat.MonsterDroppedItemQuantityPlusPctFinalForInfluencedMonster));
        }).Where(v => v.Influence != ItemInfluence.None).ToFrozenDictionary(v => v.Id, v => (v.Influence, v.Quantity));
        AreaBosses = ClientTables.Table("WorldAreas").ToFrozenDictionary(r => r.Text("Id") ?? "", r => r.Ints("Bosses_MonsterVarietiesKeys").ToHashSet());
        Companions = [.. Game.Monsters.All.Where(v => v.Supported && v.Id.EndsWith("StandaloneScarab", StringComparison.Ordinal)).OrderBy(v => v.Id, StringComparer.Ordinal)];
    }

    public static void Opening(MapOpening request)
    {
        if (request.Campaign || request.Opening is not { } opening || Pools.Count != 2 || Pools.Values.Any(p => p.Count == 0) || Companions.Count != 16) return;
        var flags = request.Slots.Where(s => !s.IsMap).SelectMany(s => FragmentFlags.GetValueOrDefault(s.BaseRow) ?? []).ToArray();
        request.Opening = opening with { Mods = opening.Mods.WithContentFlags(flags) };
        request.HandledContent.UnionWith(["ShaperPacks", "ElderPacks", "InfluencedPacks"]);
    }

    public static IReadOnlyList<AreaPopulation.AdditionalPacks> Requests(MapMods? mods)
    {
        if (mods is null) return [];
        var result = new List<AreaPopulation.AdditionalPacks>();
        void Add(string flag, uint countStat, uint sizeStat)
        {
            int count = Math.Max(0, mods.Get(countStat)) + (mods.ContentFlags.Contains(flag) ? BasePacks : 0);
            if (count <= 0 || !Pools.TryGetValue(countStat, out var pool)) return;
            int size = mods.Get(sizeStat);
            if (size == 0) size = mods.Get(Stat.MapInfluencePackSizePlusPct);
            result.Add(new(flag, pool, count, mods.PackSizeFactor * Math.Max(0, 1 + size / 100d)));
        }
        Add(Shaper, Stat.MapShaperInfluenceAdditionalPacksOfSpaceMonsters, Stat.MapShaperInfluencePackSizePlusPct);
        Add(Elder, Stat.MapElderInfluenceAdditionalPacksOfElderMonsters, Stat.MapElderInfluencePackSizePlusPct);
        return result;
    }

    public static IReadOnlyList<AreaPopulation.Spawn> Accompany(Player player, AreaLayout layout, IReadOnlyList<AreaPopulation.Spawn> spawns)
    {
        if (Game.MapModsOf(player)?.Get(Stat.MapBossSpawnStandaloneGuardian) is not > 0 || Companions.Count == 0
            || !AreaBosses.TryGetValue(layout.Id, out var bosses)) return [];
        var random = new Random(unchecked((int)player.Seed ^ 0x494e464c));
        var targets = spawns.Where(s => bosses.Contains(s.Variety)).ToArray();
        if (targets.Length == 0) return [];
        var target = targets[random.Next(targets.Length)];
        var companion = Companions[random.Next(Companions.Count)];
        var pack = new PackCatalog.Pack(-1, "Scarab/Interference", 1, 1, 0, 0, [], [(companion.Row, 1)], new Dictionary<int, int>());
        return AreaPopulation.AtPacks([(pack, target.X + 8, target.Y, MonsterRarity.Unique)],
            (x, y) => layout.WalkableFor(x, y, Math.Clamp(companion.ObjectSize - 1, 0, 4)), row => row == companion.Row,
            random.Next(), occupied: spawns.Select(s => (s.X, s.Y)));
    }

    public static MonsterModSet MonsterMods(MonsterCatalog.Variety variety, MonsterModSet mods, MapMods? map)
    {
        if (mods.Effects[Stat.DroppedItemsHaveInfluenceType] != 0 || map is null || !PackInfluences.TryGetValue(variety.Row, out int influence)) return mods;
        string flag = influence == 1 ? Shaper : Elder;
        uint count = influence == 1 ? Stat.MapShaperInfluenceAdditionalPacksOfSpaceMonsters : Stat.MapElderInfluenceAdditionalPacksOfElderMonsters;
        return map.ContentFlags.Contains(flag) || map.Get(count) > 0 ? mods with { Effects = mods.Effects.Plus(DropEffects[influence]) } : mods;
    }

    public static void Drops(DropRoll roll)
    {
        var mods = Game.MapModsOf(roll.Player);
        bool species = Loot.TryGetValue(roll.Monster.VarietyId, out var source);
        if (roll.Monster.ObjectId != 0 && roll.Player.Area.Monsters.TryGetValue(roll.Monster.ObjectId, out var monster))
        {
            var effects = monster.Effects;
            var influence = effects[Stat.DroppedItemsHaveInfluenceType] switch { 1 => ItemInfluence.Shaper, 2 => ItemInfluence.Elder, _ => ItemInfluence.None };
            if (influence != ItemInfluence.None) { source = (influence, effects[Stat.MonsterDroppedItemQuantityPlusPctFinalForInfluencedMonster]); species = true; }
        }
        int shaper = Math.Max(0, mods?.Get(Stat.MapShaperRareChancePlusPct) ?? 0), elder = Math.Max(0, mods?.Get(Stat.MapElderRareChancePlusPct) ?? 0);
        if (!species && shaper == 0 && elder == 0) return;
        if (species) roll.Quantity *= Math.Max(0, 1 + source.Quantity / 100d);
        roll.Changes.Add((drop, rng) =>
        {
            if (!drop.Type.CanBeMirrored || drop.Item.Rarity == ItemRarity.Unique || drop.Item.FracturedCount > 0 || drop.Item.Synthesised
                || drop.Item.Corrupted || drop.Item.Mirrored) return [drop];
            Item Roll(ItemInfluence influence)
            {
                var (sockets, links) = Program.Items.DropSockets(drop.Type, drop.Item.ItemLevel, rng);
                return Program.Rolls.Roll(ItemCatalog.Create(drop.Type, sockets, links, random: rng) with
                {
                    ItemLevel = drop.Item.ItemLevel, Influences = influence, Identified = drop.Item.Identified,
                }, drop.Item.Rarity, rng);
            }
            var item = species ? Program.Rolls.Roll(drop.Item with { Influences = source.Influence }, drop.Item.Rarity, rng) : drop.Item;
            var result = new List<ItemDrops.Drop> { drop with { Item = item } };
            int more = item.Rarity != ItemRarity.Rare ? 0 : item.Influences.HasFlag(ItemInfluence.Shaper) ? shaper : item.Influences.HasFlag(ItemInfluence.Elder) ? elder : 0;
            int extra = more / 100 + (rng.Next(100) < more % 100 ? 1 : 0);
            for (int i = 0; i < extra; i++) result.Add(drop with { Item = Roll(item.Influences) });
            return result;
        });
    }
}
