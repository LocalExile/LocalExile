using GameServer.Core;
using GameServer.Core.Protocol.Instance;

/// <summary>The client's Tormented Spirits (TormentSpirits): each spirit's monster, its own, Touch and Grip mods, its area
/// levels and its spawn weight.</summary>
public static class TormentCatalog
{
    /// <summary>One TormentSpirits row. <see cref="Touch"/> and <see cref="Grip"/> carry the shared Touched/Possessed
    /// rows too (speed, damage, drops and the effect mod whose buff shows the touch).</summary>
    public sealed record Spirit(int Row, string Variety, IReadOnlyList<int> Own, IReadOnlyList<int> Touch, IReadOnlyList<int> Grip,
        int MinLevel, int MaxLevel, int Weight, bool Unusual);

    // The Torment Scarab of Peculiarity's spirits (poewiki "Tormented spirit", Unusual): weight 0 in the table, as are
    // the ones that only come from their own encounter (Belcer's, the Delve Miners, the Mirage's Deserter, Shavronne).
    static readonly HashSet<string> UnusualVarieties = new(StringComparer.Ordinal)
    {
        "Metadata/Monsters/Spirit/TormentedFelon", "Metadata/Monsters/Spirit/TormentedBetrayer",
        "Metadata/Monsters/Spirit/TormentedTrespasser",
    };

    // The Effect mods: no stats, their BuffTemplate is the touch's buff (BuffDefinitions torment_touched 388,
    // torment_possessed 389; the Faridun pair for the Mirage's Deserter).
    public const string TouchedEffect = "TormentTouchedEffect", PossessedEffect = "TormentPossessedEffect";
    public const ushort TouchedBuff = 388, PossessedBuff = 389;
    /// <summary>A possessed player's buff (BuffDefinitions torment_possessed_player: "Possession"), Speaker of the Dead.</summary>
    public const ushort PossessedPlayerBuff = 2113;

    static IReadOnlyList<Spirit> Data = [];
    static int[] LifeScaling = [];
    static Dictionary<string, Spirit> Varieties = new(StringComparer.Ordinal);
    static Dictionary<int, Spirit> Rows = [];
    static Dictionary<int, Spirit> GripMods = [];
    static (ushort Touched, ushort Possessed) EffectHashes;

    public static void LoadGameData()
    {
        var data = Load();
        Data = data;
        LifeScaling = LoadLifeScaling();
        Varieties = data.ToDictionary(s => s.Variety, StringComparer.Ordinal);
        Rows = data.ToDictionary(s => s.Row);
        GripMods = data.Where(s => s.Grip.Count > 0)
            .GroupBy(s => s.Grip[0]).ToDictionary(g => g.Key, g => g.First());
        EffectHashes = (Program.Items.Mods.ById(TouchedEffect)?.Hash ?? 0, Program.Items.Mods.ById(PossessedEffect)?.Hash ?? 0);
    }

    public static IReadOnlyList<Spirit> All => Data;

    static IReadOnlyList<Spirit> Load()
    {
        if (!ClientTables.Has("TormentSpirits")) return [];
        var result = new List<Spirit>();
        int index = 0;
        foreach (var row in ClientTables.Table("TormentSpirits"))
        {
            int at = index++;
            if (Game.Monsters.Find(row.Int("MonsterVarietiesKey").ToString()) is not { Supported: true } variety) continue;
            result.Add(new(at, variety.Id, row.Ints("Spirit_ModsKeys"), row.Ints("Touched_ModsKeys"), row.Ints("Possessed_ModsKeys"),
                row.Int("MinZoneLevel"), row.Int("MaxZoneLevel"), row.Int("SpawnWeight"), UnusualVarieties.Contains(variety.Id)));
        }
        return result;
    }

    public static Spirit? ByVariety(string? variety) => variety is not null && Varieties.TryGetValue(variety, out var s) ? s : null;
    public static Spirit? ByRow(int row) => Rows.GetValueOrDefault(row);
    // A spirit by its own Grip mod (the first of its Possessed_ModsKeys: the shared ones are in every spirit's list).
    public static Spirit? ByGripMod(int modRow) => GripMods.GetValueOrDefault(modRow);

    /// <summary>The spirits an area of this level rolls from: the weighted ones, or the unusual ones with the Torment Scarab
    /// of Peculiarity (<c>map_tormented_spirits_rare_varieties</c>), equally likely (their table weight is 0).</summary>
    public static IReadOnlyList<Spirit> Pool(int level, bool unusual) => [.. All.Where(s => s.MinLevel <= level && level <= s.MaxLevel
        && (unusual ? s.Unusual : s.Weight > 0))];

    /// <summary>A spirit by its weight; <paramref name="lessCommon"/> (Unrelenting Torment's <c>map_rare_spirits_more_common</c>:
    /// "more likely to be less common varieties") weighs them all alike, our reading of "more likely".</summary>
    public static Spirit? Pick(IReadOnlyList<Spirit> pool, Random random, bool lessCommon = false)
    {
        if (pool.Count == 0) return null;
        int Weight(Spirit s) => lessCommon ? 1 : Math.Max(1, s.Weight);
        int roll = random.Next(pool.Sum(Weight));
        foreach (var s in pool) if ((roll -= Weight(s)) < 0) return s;
        return pool[^1];
    }

    /// <summary>The spirits' more life at a level (TormentedSpiritLifeScalingPerLevel, the .ot's
    /// <c>set_additional_life_scaling_index = 16</c>): 0 to level 55, 20 per level to 240 at 67, 40 per level to 920.</summary>
    public static int LifePct(int level)
    {
        var table = LifeScaling;
        return table.Length == 0 ? 0 : table[Math.Clamp(level, 1, table.Length) - 1];
    }

    static int[] LoadLifeScaling()
    {
        if (!ClientTables.Has("TormentedSpiritLifeScalingPerLevel")) return [];
        var rows = ClientTables.Table("TormentedSpiritLifeScalingPerLevel").Select(r => (Level: r.Int("Level"), Life: r.Int("Life")))
            .Where(r => r.Level >= 1).OrderBy(r => r.Level).ToArray();
        if (rows.Length == 0) return [];
        var table = new int[rows[^1].Level];
        foreach (var (level, life) in rows) table[level - 1] = life;
        return table;
    }

    /// <summary>Mods.dat rows rolled as a monster's explicit mods (the list its name plate shows), with their summed stats.</summary>
    public static (IReadOnlyList<ItemMod> Mods, Dictionary<uint, int> Stats) Roll(IEnumerable<int> rows, Random random)
    {
        var mods = new List<ItemMod>();
        var stats = new Dictionary<uint, int>();
        foreach (var mod in rows.Select(Program.Items.Mods.Row).OfType<ModCatalog.Mod>())
        {
            var rolled = mod.Roll(ModSection.Explicit, random);
            mods.Add(rolled);
            for (int i = 0; i < mod.Stats.Count && i < rolled.Values.Count; i++)
                stats[mod.Stats[i].Id] = stats.GetValueOrDefault(mod.Stats[i].Id) + rolled.Values[i];
        }
        return (mods, stats);
    }

    /// <summary>Whether a monster's mods carry a spirit's Touch (or, <paramref name="grip"/>, a Grip).</summary>
    public static bool Has(MonsterModSet mods, bool grip)
    {
        ushort hash = grip ? EffectHashes.Possessed : EffectHashes.Touched;
        if (hash == 0) return false;
        foreach (var mod in mods.Mods) if (mod.Hash == hash) return true;
        return false;
    }
}
