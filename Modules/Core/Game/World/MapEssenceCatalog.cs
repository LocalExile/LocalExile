using GameServer.Core;
using GameServer.Core.Protocol.Instance;

/// <summary>Imprisoned-monster essences, separate from the essences' equipment crafting recipes.</summary>
public static class MapEssenceCatalog
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadGameData()
    {
        Data = LoadData();
    }

    public sealed record Essence(int Row, uint Hash, string Item, int Type, int Tier, bool Corrupted,
        IReadOnlyList<int> DropLevels, IReadOnlyList<int> MonsterMods);
    static IReadOnlyList<Essence> Data = default!;
    static IReadOnlyList<Essence> LoadData() => Load();
    public static IReadOnlyList<Essence> All => Data;
    public static Essence? Find(int row) => All.FirstOrDefault(e => e.Row == row);

    static IReadOnlyList<Essence> Load()
    {
        if (!ClientTables.Has("Essences") || !ClientTables.Has("EssenceType")) return [];
        var types = ClientTables.Table("EssenceType");
        var result = new List<Essence>();
        int index = 0;
        foreach (var row in ClientTables.Table("Essences"))
        {
            int key = row.Int("EssenceTypeKey"), tier = row.Int("Level"), at = index++;
            if (key < 0 || key >= types.Count || tier <= 0 || Program.Items.Row(row.Int("BaseItemTypesKey")) is not { Supported: true } item) continue;
            result.Add(new(at, row.UInt("HASH32"), item.Id, key, tier, types[key].Bool("IsCorruptedEssence"),
                row.Ints("DropLevel"), row.Ints("Monster_ModsKeys")));
        }
        return result;
    }

    public static IReadOnlyList<Essence> Pool(int level) => [.. All.Where(e => !e.Corrupted
        && e.DropLevels.Any(l => l <= level))];

    public static Essence Higher(Essence essence) => All.FirstOrDefault(e => e.Type == essence.Type
        && e.Tier == essence.Tier + 1) ?? essence;

    public static IReadOnlyList<int> Roll(int level, MapMods mods, Random random)
    {
        var pool = Pool(level);
        if (pool.Count == 0) return [];
        // As with ordinary pack density, the official server's base multiplicity/weights are unavailable.
        // Keep this replica distribution explicit rather than treating DropLevel entries as spawn weights.
        int count = random.Next(1, 4) + Math.Max(0, mods.Get(Stat.MapEssenceMonstersHaveAdditionalEssences));
        if (random.Next(100) < mods.Get(Stat.MapEssenceMonstersHaveAnAdditionalEssencePctChance)) count++;
        if (random.Next(100) < mods.Get(Stat.MapEssenceMonstersChanceFor3AdditionalEssencesPct)) count += 3;
        var chosen = new List<Essence>();
        for (int i = 0; i < count; i++)
        {
            var available = pool.Where(e => !chosen.Any(c => c.Type == e.Type)).ToArray();
            if (available.Length == 0) break;
            var pick = available[random.Next(available.Length)];
            if (mods.Get(Stat.MapEssenceMonstersHigherTier) > 0
                || random.Next(100) < mods.Get(Stat.MapEssencesAre1TierHigherChancePct)) pick = Higher(pick);
            chosen.Add(pick);
        }
        return [.. chosen.Select(e => e.Row)];
    }

    public static MonsterModSet Mods(Player conn, MonsterCatalog.Variety variety, byte level, IReadOnlyList<int> essences)
    {
        var basis = Game.MonsterModsFor(conn, variety, level, MonsterRarity.Rare, null, false);
        var rows = essences.Select(Find).OfType<Essence>().SelectMany(e => e.MonsterMods).Distinct();
        var extra = rows.Select(Program.Items.Mods.Row).OfType<ModCatalog.Mod>()
            .Select(m => m.Roll(ModSection.Implicit, Random.Shared));
        return Game.MonsterModRolls.Create([.. basis.Mods, .. extra], basis.NameWords, MonsterRarity.Rare, level);
    }
}
