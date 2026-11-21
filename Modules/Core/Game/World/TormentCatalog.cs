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

    static readonly Lazy<IReadOnlyList<Spirit>> Data = new(Load);
    static readonly Lazy<int[]> LifeScaling = new(LoadLifeScaling);
    public static IReadOnlyList<Spirit> All => Data.Value;

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

    static readonly Lazy<Dictionary<string, Spirit>> Varieties = new(() => All.ToDictionary(s => s.Variety, StringComparer.Ordinal));
    static readonly Lazy<Dictionary<int, Spirit>> Rows = new(() => All.ToDictionary(s => s.Row));
    public static Spirit? ByVariety(string? variety) => variety is not null && Varieties.Value.TryGetValue(variety, out var s) ? s : null;
    public static Spirit? ByRow(int row) => Rows.Value.GetValueOrDefault(row);
    // A spirit by its own Grip mod (the first of its Possessed_ModsKeys: the shared ones are in every spirit's list).
    static readonly Lazy<Dictionary<int, Spirit>> GripMods = new(() => All.Where(s => s.Grip.Count > 0)
        .GroupBy(s => s.Grip[0]).ToDictionary(g => g.Key, g => g.First()));
    public static Spirit? ByGripMod(int modRow) => GripMods.Value.GetValueOrDefault(modRow);

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
        var table = LifeScaling.Value;
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
    public static (IReadOnlyList<ItemMod> Mods, Dictionary<string, int> Stats) Roll(IEnumerable<int> rows, Random random)
    {
        var mods = Game.MonsterModRolls.Value.Species(rows.ToArray(), random);
        return (mods, Game.MonsterModRolls.Value.RolledStats(mods));
    }

    static readonly Lazy<HashSet<ushort>> TouchAndGripHashes = new(() => All.SelectMany(s => s.Touch.Concat(s.Grip))
        .Distinct().Select(Program.Items.Mods.Row).OfType<ModCatalog.Mod>().Select(m => m.Hash).ToHashSet());

    /// <summary>The Touches and Grips actually rolled on this monster, including a kept monster's older mod list.</summary>
    internal static MonsterModEffects EffectsOn(MonsterModSet mods)
    {
        var rolls = mods.Mods.Where(m => TouchAndGripHashes.Value.Contains(m.Hash)).ToArray();
        return rolls.Length == 0 ? MonsterModEffects.None : new(Game.MonsterModRolls.Value.RolledStats(rolls));
    }

    static readonly Lazy<(ushort Touched, ushort Possessed)> EffectHashes = new(() =>
        (Program.Items.Mods.ById(TouchedEffect)?.Hash ?? 0, Program.Items.Mods.ById(PossessedEffect)?.Hash ?? 0));

    /// <summary>Whether a monster's mods carry a spirit's Touch (or, <paramref name="grip"/>, a Grip).</summary>
    public static bool Has(MonsterModSet mods, bool grip)
    {
        ushort hash = grip ? EffectHashes.Value.Possessed : EffectHashes.Value.Touched;
        if (hash == 0) return false;
        foreach (var mod in mods.Mods) if (mod.Hash == hash) return true;
        return false;
    }

    static readonly Lazy<ModCatalog.Mod?> MartyrTouch = new(() => Program.Items.Mods.ById("TormentTouchedMonsterMartyr"));

    internal static int? MartyrGrantRow(ClientState conn, MonsterModSet mods)
    {
        if (mods.Mods.Count == 0 || MartyrTouch.Value is not { } touch) return null;
        for (int i = 0; i < mods.Mods.Count; i++)
            if (mods.Mods[i].Hash == touch.Hash)
                return (Program.LeagueFor(conn).HardMode && touch.RuthlessGrantedEffectLevels.Count > 0
                    ? touch.RuthlessGrantedEffectLevels : touch.GrantedEffectLevels).Single();
        return null;
    }

    // The Martyr's Touch grants a specific GrantedEffectsPerLevel row. It is a triggered Actor skill, outside the
    // host's attack rotation. The same row and actor-local id survive a refresh of the host's ordinary attacks.
    internal static IReadOnlyList<GrantedSkill>? WithMartyrSkill(ClientState conn, MonsterModSet mods,
        IReadOnlyList<GrantedSkill>? skills, out GrantedSkill? grant)
    {
        grant = null;
        if (MartyrGrantRow(conn, mods) is not int row) return skills;
        grant = skills?.FirstOrDefault(s => s.EffectsPerLevelRow == row);
        if (grant is not null) return skills;
        var catalog = Game.SkillsFor(conn);
        var (effect, level) = catalog.EffectsPerLevel(row) ?? throw new InvalidDataException($"Martyr's Touch grants unknown skill row {row}");
        ushort id = catalog.SkillIds().First(id => skills is null || skills.All(s => s.Id != id));
        grant = catalog.FindByRow(id, effect, level, catalog.EffectStats(effect, level))
            ?? throw new InvalidDataException($"Martyr's Touch has no skill record for row {row}");
        return [.. skills ?? [], grant];
    }
}
