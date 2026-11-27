using System.Collections.Immutable;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core;

/// <summary>
/// The monsters /spawn_mob can create: MonsterVarieties rows from the extracted tables
/// (the MonsterVarieties client table), with each type's
/// deserialized components from data/object-types.json (`python -I scripts/re/ot_components.py --all --json
/// data/object-types.json`). Without that file every variety is assumed to have the base Monster list, which is wrong
/// for about half of them (StateMachine, DiesAfterTime …) and then the client shows "Buffer Underflow".
/// Both files are game data: they stay in data/ and are read when the catalog is made.
/// </summary>
public sealed class MonsterCatalog
{
    readonly string folder;

    public MonsterCatalog(string folder = "data")
    {
        this.folder = folder;
        _resistance = new(folder);
        _rows = Load(folder);
        _stockSnapshot = BuildSnapshot(_rows.Rows, [], [], 0, 0);
        _snapshot = _stockSnapshot;
        _life = LifeTables.Load(folder);
        _mapBoss = MapBossScalingTable.Load(folder);
        _areaTiers = LoadAreaTiers();
        _takeHit = LoadTakeHit();
        _corpse = LoadCorpse();
        _degenDeath = LoadDegenDeath();
    }

    public sealed record Variety(int Row, string Id, string Name, string BaseType, string? AnimatedObject, IReadOnlyList<string>? Components,
        int LifeMultiplier = 100, int MonsterType = -1, StateMachineData? StateMachine = null, byte TransitionStates = 1,
        int MovementSpeed = 30, int ObjectSize = 3, int AggroRange = 60, IReadOnlyList<int>? GrantedEffects = null,
        int AttackMs = 1500, int AttackRange = 8, int DamageMultiplier = 100, int ExperienceMultiplier = 100,
        bool BossHealthBar = false, IReadOnlyList<int>? SpeciesMods = null)
    {
        public MonsterLifeData LifeData { get; init; } = new(null, null, null);
        public bool RangedWeapon { get; init; }
        /// <summary>What its row says it holds (Weapon1, Weapon2: the first of each, with the MainHand and OffHand classes), as
        /// the Inventories component lists it. Only an encounter that passes it on sends it (Izaro's).</summary>
        public IReadOnlyList<GameServer.Core.Protocol.Instance.VisibleItem> Weapons { get; init; } = [];
        /// <summary>Native Positioned.blocking, independent of targetability. Missing optional metadata retains
        /// the earlier living-body assumption; the extracted resolved type supplies explicit false values.</summary>
        public bool PositionedBlocking { get; init; } = true;
        /// <summary>The type's Stats set_phase_through_objects (object-types.json "phases"): the client's Pathfinding
        /// phases for it, so it is no body to any other walker and no living body stops it (Maven's orbs).</summary>
        public bool Phases { get; init; }
        /// <summary>The type's Pathfinding immobile (object-types.json "immobile"): the client never walks it. A Move's
        /// 0x1ab only puts it on the order's from cell (no path search, no step: a unique Maven's orb, traced
        /// 2026-11-10), so it gets no chase, like a variety with MovementSpeed 0.</summary>
        public bool Immobile { get; init; }
        /// <summary>The resolved Actor resource's slow_animations_go_to_idle flag. Null for legacy or incomplete
        /// metadata, or varieties with addon types whose Actor overrides are not resolved here.</summary>
        public bool? SlowAnimationsGoToIdle { get; init; }
        /// <summary>The row's HASH16: the key the client's MonsterVarieties table and the statue task use.</summary>
        public ushort Hash16 { get; init; }
        /// <summary>
        /// The row's movement speed roll range in percent: the two unnamed i32 columns before the f32 that precedes
        /// AilmentThreshold (<c>__col75</c>, <c>__col76</c>; (0, 0) for most rows, (−5, 5) for the Goatmen, (−10, 10)
        /// for many others). The client draws each monster's roll in it (MonsterSpeedRoll.cs).
        /// </summary>
        public int SpeedRollMin { get; init; }
        public int SpeedRollMax { get; init; }
        /// <summary>The row's ModelSizeMultiplier as a factor (100 → 1): the client scales the model, and a skill with
        /// <c>use_scaled_contact_offset</c> scales its release offset with it.</summary>
        public double ModelScale { get; init; } = 1;
        /// <summary>The row's MinAgroRange (cells); 0 when the row has none.</summary>
        public int MinAgroRange { get; init; }
        /// <summary>The row's AilmentThreshold multiplier (percent of DefaultMonsterStats.AilmentThreshold); 0 means use LifeMultiplier.</summary>
        public int AilmentThreshold { get; init; }
        /// <summary>The row's MinimumAttackDistance (cells).</summary>
        public int MinimumAttackDistance { get; init; }
        /// <summary>Passive species Mods.dat rows from ModsKeys and Special_ModsKeys (excluding stance-only ModsKeys2).</summary>
        public IReadOnlyList<int>? ActiveSpeciesMods { get; init; }
        /// <summary>Area-tier Mods.dat rows applied in Part 1 (Acts 1–5), Part 2 (Acts 6–10) and Endgame (maps).</summary>
        public IReadOnlyList<int>? Part1Mods { get; init; }
        public IReadOnlyList<int>? Part2Mods { get; init; }
        public IReadOnlyList<int>? EndgameMods { get; init; }
        public IReadOnlyList<string> ComponentsOrBase => Components ?? MonsterObject.BaseComponents;
        /// <summary>Index of the <c>StateMachine</c> component in <see cref="ComponentsOrBase"/>, or −1 when absent.</summary>
        public int StateMachineComponentIndex { get; init; } = IndexOfComponent(Components ?? MonsterObject.BaseComponents, "StateMachine");
        /// <summary>Per-shared-state metadata from <c>object_type_state_meta</c> (client listeners, client GEAL writers,
        /// and default-hidden rendering flags), merged with <c>AddonMonsterTypeIndex</c>.</summary>
        public IReadOnlyDictionary<string, ObjectTypes.StateFlagMeta> StateMeta { get; init; } = EmptyStateMeta;
        /// <summary>The type's .ot chain from object-types.json, for lineage-gated extras
        /// (<see cref="MonsterObject.CanSerialize"/>): empty when object-types.json is absent.</summary>
        public IReadOnlyList<string> Chain { get; init; } = [];
        /// <summary>Mods.dat rows owned by this MonsterVarieties row (ModsKeys, ModsKeys2 and Special_ModsKeys).</summary>
        public IReadOnlyList<int> SpeciesModsOrEmpty => SpeciesMods ?? [];
        public IReadOnlyList<int> ActiveSpeciesModsOrEmpty => ActiveSpeciesMods ?? SpeciesModsOrEmpty;
        public IReadOnlyList<int> Part1ModsOrEmpty => Part1Mods ?? [];
        public IReadOnlyList<int> Part2ModsOrEmpty => Part2Mods ?? [];
        public IReadOnlyList<int> EndgameModsOrEmpty => EndgameMods ?? [];
        public bool Supported => MonsterObject.CanSerialize(ComponentsOrBase, StateMachine is not null, Chain);
        public override string ToString() => $"{Name} ({Id}, row {Row})";
    }

    static readonly IReadOnlyDictionary<string, ObjectTypes.StateFlagMeta> EmptyStateMeta =
        new Dictionary<string, ObjectTypes.StateFlagMeta>();

    static int IndexOfComponent(IReadOnlyList<string> components, string name)
    {
        for (int i = 0; i < components.Count; i++)
            if (components[i] == name) return i;
        return -1;
    }

    private readonly (List<Variety> Rows, string? Error) _rows;
    private readonly LifeTables? _life;
    private readonly MapBossScalingTable? _mapBoss;
    private readonly (Dictionary<string, int> ById, Dictionary<ushort, int> ByHash) _areaTiers;
    private readonly MonsterResistanceCatalog _resistance;
    private readonly Dictionary<string, HitAnimation[]> _takeHit;

    public string? Error => _rows.Error;

    /// <summary>The variety's elemental/chaos base resistance at this level, before spawned monster modifiers.</summary>
    public int? BaseResistance(Variety variety, int level, GameServer.Core.World.DamageType type) =>
        _resistance.BaseResistance(variety.MonsterType, level, type);

    /// <summary>
    /// The variety's TakeHit clips: their indexes in its rig's animation list and their lengths
    /// (data/monster-takehit.json, written by <c>python -I scripts/re/monster_takehit.py</c>). S→C 0x1b2 kind 0 plays
    /// one of them on the Animated component and the clip's own SoundEvents play with it, when a hit passes
    /// <see cref="HitReaction"/>'s gates. Most varieties map one clip; a few map a pool (the .act's
    /// "flinch_small:max_duration=…, flinch_large:min_duration=…" or "flinch_01, flinch_02, flinch_03"), from which the
    /// server picks one per flinch. Empty without the file or the
    /// row (the monster then just does not flinch). A clip without a length (a file from before "lengths") has 0 s, so
    /// the 0.4 s minimum applies.
    /// </summary>
    public IReadOnlyList<HitAnimation> TakeHit(string id) => _takeHit.GetValueOrDefault(id) ?? [];

    /// <summary>Nothing left to build: the constructor reads the life tables and the take-hit animations. Kept for the game
    /// module's startup warm-up.</summary>
    public void Warm() { }

    /// <summary>
    /// When the variety's death clip clears the corpse's body from the client's paths, in ms from the clip's start (its
    /// remove_collidability event; client.db's monster_takehit death_unblock), null when it has none: the client then
    /// clears the body with the death itself. 150 without the table (most varieties' value).
    /// </summary>
    public int? CorpseUnblockMs(string id) => _corpse is { } known ? known.GetValueOrDefault(id) : 150;

    private readonly Dictionary<string, int?>? _corpse;

    /// <summary>The variety's DeathFromDegen clip clears the body at another time than its Death clip (client.db's
    /// monster_takehit death_unblockByKind_DeathFromDegen: five Faridun Flesh Hulks, 166 ms against 150).</summary>
    public bool DegenDeathClipDiffers(string id) => _degenDeath.Contains(id);

    private readonly HashSet<string> _degenDeath;

    static HashSet<string> LoadDegenDeath() =>
        [.. ClientTables.TableOrEmpty("monster_takehit").Where(r => !r.IsNull("death_unblockByKind_DeathFromDegen"))
            .Select(r => r.Text("variety")!)];

    static Dictionary<string, int?>? LoadCorpse()
    {
        if (ClientTables.TableOrNull("monster_takehit") is not { } table) return null;
        Dictionary<string, int?>? result = null;
        foreach (var r in table)
            if (!r.IsNull("death_clips")) (result ??= [])[r.Text("variety")!] = r.IntOrNull("death_unblock");
        return result;
    }

    static Dictionary<string, HitAnimation[]> LoadTakeHit()
    {
        var result = new Dictionary<string, HitAnimation[]>();
        foreach (var r in ClientTables.TableOrEmpty("monster_takehit"))
        {
            // "lengths" (ms) runs parallel to "indexes".
            double[] lengths = r.Reals("lengths");
            double Seconds(int i) => i < lengths.Length ? lengths[i] / 1000 : 0;
            if (!r.IsNull("indexes"))
                result[r.Text("variety")!] = [.. r.Ints("indexes").Select((e, i) => new HitAnimation(e, Seconds(i)))];
            else if (r.IntOrNull("index") is int one)
                result[r.Text("variety")!] = [new HitAnimation(one, Seconds(0))];
        }
        return result;
    }

    /// <summary>
    /// The maximum life the client gives a monster of this variety at this level, before mods: the client's Monster type
    /// (0x141e76860) sets stat 0x7d to max(1, column × LifeMultiplier / 100) from DefaultMonsterStats row level − 1 (0x48-byte
    /// rows). The column depends on the variety's MonsterTypes row: AltLife1 → AltLife1, else AltLife2 → AltLife2, else
    /// IsPlayerMinion → MinionLife, else MonsterLife. Null when the tables are missing.
    /// </summary>
    public int? BaseLife(Variety v, byte level)
    {
        if (_life is not { } t || level < 1 || level > t.Levels.Count) return null;
        var row = t.Levels[level - 1];
        var type = v.MonsterType >= 0 && v.MonsterType < t.Types.Count ? t.Types[v.MonsterType] : default;
        int column = type.AltLife1 ? row.AltLife1 : type.AltLife2 ? row.AltLife2 : type.Minion ? row.MinionLife : row.MonsterLife;
        return Math.Max(1, column * v.LifeMultiplier / 100);
    }

    /// <summary>
    /// monster_life_+%_final_from_rarity of the rarity's own monster mod (Mods.dat MonsterMagic2 148, MonsterRare2 390,
    /// MonsterUnique2 698): the life a magic, rare or unique monster has more than a normal one. Hillock at level 1:
    /// 39 → 311.
    /// </summary>
    public static int RarityLifePct(MonsterRarity rarity) => rarity switch
    {
        MonsterRarity.Magic => 148,
        MonsterRarity.Rare => 390,
        MonsterRarity.Unique => 698,
        _ => 0,
    };

    /// <summary>The maximum life with the rarity's monster_life_+%_final_from_rarity (<see cref="RarityLifePct"/>).</summary>
    public static int LifeWithRarity(int baseLife, MonsterRarity rarity) => (int)((long)baseLife * (100 + RarityLifePct(rarity)) / 100);

    /// <summary>
    /// A monster's hit at this level, before mods: DefaultMonsterStats row level − 1 `Damage` (`MinionDamage` for an
    /// IsPlayerMinion type, as PoB's monsterAllyDamageTable for non-hostile minions) × DamageMultiplier / 100
    /// × the attack time in seconds (unless its MonsterTypes row has BaseDamageIgnoresAttackSpeed, as Path of Building
    /// does), spread ± MonsterTypes `DamageSpread` percent (20 without one). Hillock at level 1: 4.99 × 1.01 × 1.65 = 8.3,
    /// spread 30%. Null when the tables are missing.
    /// </summary>
    public (int Min, int Max)? BaseDamage(Variety v, byte level)
    {
        if (_life is not { } t || level < 1 || level > t.Levels.Count) return null;
        var type = v.MonsterType >= 0 && v.MonsterType < t.Types.Count ? t.Types[v.MonsterType] : default;
        var row = t.Levels[level - 1];
        double baseDmg = type.AltLife1 ? row.AltDamage1 : type.AltLife2 ? row.AltDamage2 : type.Minion ? row.MinionDamage : row.Damage;
        double d = baseDmg * v.DamageMultiplier / 100.0 * (type.IgnoresAttackSpeed ? 1 : v.AttackMs / 1000.0);
        double spread = (type.DamageSpread > 0 ? type.DamageSpread : 20) / 100.0;
        return (Math.Max(1, (int)Math.Round(d * (1 - spread))), Math.Max(1, (int)Math.Round(d * (1 + spread))));
    }

    /// <summary>The rarity's damage and attack speed (its monster mods MonsterMagic5/Rare5/Unique5
    /// monster_rarity_damage_+%_final 30/50/70, and MonsterMagic9/Rare9/Unique8
    /// monster_rarity_attack_cast_speed_+%_and_damage_-%_final 20/33/33: that much faster, that much less damage per
    /// hit). Hillock: × 1.7 × 0.67 per hit, 1.65 s → 1.24 s.</summary>
    public static (double DamageFactor, double SpeedFactor) RarityAttack(MonsterRarity rarity)
    {
        var (more, faster) = rarity switch
        {
            MonsterRarity.Magic => (30, 20),
            MonsterRarity.Rare => (50, 33),
            MonsterRarity.Unique => (70, 33),
            _ => (0, 0),
        };
        return ((1 + more / 100.0) * (1 - faster / 100.0), 1 + faster / 100.0);
    }

    /// <summary>The experience a monster of this level gives before its variety's ExperienceMultiplier, rarity and the
    /// level penalty (Progression.MonsterExperience): DefaultMonsterStats row level − 1 `Experience`. Null when the
    /// tables are missing.</summary>
    public int? BaseExperience(Variety v, byte level) =>
        _life is { } t && level >= 1 && level <= t.Levels.Count ? t.Levels[level - 1].Experience : null;

    /// <summary>A normal monster's damage at this level, before a variety's multiplier: DefaultMonsterStats `Damage`.
    /// Used by the defence probe; the actual hit spread is in <see cref="BaseDamage(Variety, byte)"/>.</summary>
    public int? BaseDamage(int level) =>
        _life is { } t && level >= 1 && level <= t.Levels.Count ? (int)Math.Round(t.Levels[level - 1].Damage) : null;

    /// <summary>A monster's accuracy at this level: DefaultMonsterStats `Accuracy`.</summary>
    public int? BaseAccuracy(int level) =>
        _life is { } t && level >= 1 && level <= t.Levels.Count ? t.Levels[level - 1].Accuracy : null;

    /// <summary>A variety's accuracy at this level: DefaultMonsterStats `Accuracy` × MonsterTypes `Accuracy` / 100
    /// (default 100 for standard monsters, 120–500 for accurate archetypes; verified in Game.exe 0x141e7829d).</summary>
    public int? BaseAccuracy(Variety? variety, int level)
    {
        if (_life is not { } t || level < 1 || level > t.Levels.Count) return null;
        int acc = t.Levels[level - 1].Accuracy;
        if (variety is null || variety.MonsterType < 0 || variety.MonsterType >= t.Types.Count) return acc;
        int pct = t.Types[variety.MonsterType].Accuracy;
        return pct > 0 && pct != 100 ? (int)((long)acc * pct / 100) : acc;
    }

    /// <summary>A monster's evasion rating at this level: DefaultMonsterStats `Evasion` (the same table PoB uses for
    /// <c>monsterEvasionTable</c>). Null when the tables are missing.</summary>
    public int? BaseEvasion(int level) =>
        _life is { } t && level >= 1 && level <= t.Levels.Count ? t.Levels[level - 1].Evasion : null;

    /// <summary>A variety's evasion rating at this level: interpolates/extrapolates from DefaultMonsterStats `Evasion`
    /// (MonsterTypes.Evasion = 0) through `EvasiveEvasion` (MonsterTypes.Evasion = 50), matching Game.exe 0x141e7813b.</summary>
    public int? BaseEvasion(Variety? variety, int level)
    {
        if (_life is not { } t || level < 1 || level > t.Levels.Count) return null;
        var row = t.Levels[level - 1];
        if (variety is null || variety.MonsterType < 0 || variety.MonsterType >= t.Types.Count) return row.Evasion;
        int pct = t.Types[variety.MonsterType].Evasion;
        if (pct <= 0) return row.Evasion;
        if (pct == 50) return row.EvasiveEvasion;
        return Math.Max(0, row.Evasion + (int)((long)(row.EvasiveEvasion - row.Evasion) * pct / 50));
    }

    /// <summary>The evasion of an evasive (high-dexterity) monster at this level: DefaultMonsterStats `EvasiveEvasion`.
    /// The character panel's "Chance to Hit Evasive Monsters" row uses it.</summary>
    public int? BaseEvasiveEvasion(int level) =>
        _life is { } t && level >= 1 && level <= t.Levels.Count ? t.Levels[level - 1].EvasiveEvasion : null;

    /// <summary>A variety's base armour rating at this level: DefaultMonsterStats `Armour` × MonsterTypes `Armour` / 100
    /// (0 when MonsterTypes.Armour is 0; null when tables are missing).</summary>
    public int? BaseArmour(Variety? variety, int level)
    {
        if (_life is not { } t || level < 1 || level > t.Levels.Count) return null;
        if (variety is null || variety.MonsterType < 0 || variety.MonsterType >= t.Types.Count) return 0;
        int pct = t.Types[variety.MonsterType].Armour;
        return pct <= 0 ? 0 : (int)Math.Clamp((long)t.Levels[level - 1].Armour * pct / 100, 0, int.MaxValue);
    }

    /// <summary>A variety's base energy shield at this level: ((baseLife × GameConstants.EnergyShieldRatioOfLife / 100)
    /// × MonsterTypes.EnergyShield) / 100 (40% × type% of base life; verified in Game.exe 0x141e780ef).</summary>
    public int? BaseEnergyShield(Variety? variety, int level, long baseLife)
    {
        if (_life is not { } t || level < 1 || level > t.Levels.Count) return null;
        if (variety is null || baseLife <= 0 || variety.MonsterType < 0 || variety.MonsterType >= t.Types.Count) return 0;
        int pct = t.Types[variety.MonsterType].EnergyShield;
        if (pct <= 0) return 0;
        long ratio = baseLife * t.EnergyShieldRatioOfLife / 100;
        return (int)Math.Clamp(ratio * pct / 100, 0, int.MaxValue);
    }

    /// <summary>A variety's ailment threshold for a monster with <paramref name="maxLife"/>: scales <paramref name="maxLife"/>
    /// by MonsterVarieties.AilmentThreshold / LifeMultiplier (e.g. Kitava A5 3000/7200 and reduced-threshold map bosses)
    /// and DefaultMonsterStats.AilmentThreshold / matching base life column (verified in Game.exe 0x141e781f8).</summary>
    public long AilmentThreshold(Variety? variety, int level, long maxLife)
    {
        if (maxLife <= 1 || variety is null) return Math.Max(1, maxLife);
        double threshold = maxLife;
        if (variety.AilmentThreshold > 0 && variety.LifeMultiplier > 0 && variety.AilmentThreshold != variety.LifeMultiplier)
            threshold *= (double)variety.AilmentThreshold / variety.LifeMultiplier;
        if (_life is { } t && level >= 1 && level <= t.Levels.Count)
        {
            var row = t.Levels[level - 1];
            var type = variety.MonsterType >= 0 && variety.MonsterType < t.Types.Count ? t.Types[variety.MonsterType] : default;
            int baseRowLife = type.AltLife1 ? row.AltLife1 : type.AltLife2 ? row.AltLife2 : type.Minion ? row.MinionLife : row.MonsterLife;
            if (row.AilmentThreshold > 0 && baseRowLife > 0 && row.AilmentThreshold != baseRowLife)
                threshold *= (double)row.AilmentThreshold / baseRowLife;
        }
        return Math.Max(1L, (long)Math.Round(threshold));
    }

    /// <summary>Area progression tier for <see cref="SpeciesModsForArea(Variety, int)"/>: 1 = Part 1 (Acts 1–5, Normal
    /// Labyrinth), 2 = Part 2 (Acts 6–10, Cruel Labyrinth), 3 = Endgame (maps, Act 11+, Merciless/Eternal Labyrinth,
    /// Delve, Heist).</summary>
    public int AreaTier(string? areaId, ushort areaHash, bool isMap)
    {
        if (isMap) return 3;
        if (!string.IsNullOrEmpty(areaId) && _areaTiers.ById.TryGetValue(areaId, out int byId)) return byId;
        if (areaHash != 0 && _areaTiers.ByHash.TryGetValue(areaHash, out int byHash)) return byHash;
        if (string.IsNullOrEmpty(areaId)) return 1;
        return ClassifyAreaTier(areaId, act: 0, areaLevel: 0, isMapArea: false, isLabyrinth: false);
    }

    static int ClassifyAreaTier(string id, int act, int areaLevel, bool isMapArea, bool isLabyrinth)
    {
        if (isMapArea || act >= 11) return 3;
        if (isLabyrinth || id.Contains("Labyrinth", StringComparison.OrdinalIgnoreCase))
        {
            if (id.StartsWith("EndGame_", StringComparison.OrdinalIgnoreCase)
                || id.StartsWith("3_Labyrinth", StringComparison.OrdinalIgnoreCase)
                || areaLevel >= 68)
                return 3;
            if (id.StartsWith("2_Labyrinth", StringComparison.OrdinalIgnoreCase) || areaLevel >= 55)
                return 2;
            return 1;
        }
        if (id.StartsWith("Delve_", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("Heist", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("EndGame_", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("Map", StringComparison.OrdinalIgnoreCase))
            return 3;
        if (act is >= 6 and <= 10 || id.StartsWith("2_", StringComparison.Ordinal)) return 2;
        return 1;
    }

    static (Dictionary<string, int> ById, Dictionary<ushort, int> ByHash) LoadAreaTiers()
    {
        var byId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byHash = new Dictionary<ushort, int>();
        if (ClientTables.TableOrNull("WorldAreas") is not { } areas) return (byId, byHash);
        foreach (var r in areas)
        {
            string id = r.Text("Id") ?? "";
            if (id.Length == 0) continue;
            int act = r.IntOrNull("Act") ?? 0;
            int areaLevel = r.IntOrNull("AreaLevel") ?? 0;
            bool isMap = r.BoolOrNull("IsMapArea") == true || r.BoolOrNull("IsUniqueMapArea") == true;
            bool isLab = r.BoolOrNull("IsLabyrinthArea") == true || r.BoolOrNull("IsLabyrinthBossArea") == true;
            int tier = ClassifyAreaTier(id, act, areaLevel, isMap, isLab);
            byId[id] = tier;
            if (r.IntOrNull("HASH16") is int h and > 0 and <= ushort.MaxValue)
                byHash.TryAdd((ushort)h, tier);
        }
        return (byId, byHash);
    }

    /// <summary>The active species Mods.dat rows for a variety in an area: ModsKeys + Special_ModsKeys plus the area's
    /// tier mods (1 = Part 1, 2 = Part 2, 3 = Endgame).</summary>
    public static IReadOnlyList<int> SpeciesModsForArea(Variety variety, int tier)
    {
        var baseMods = variety.ActiveSpeciesModsOrEmpty;
        var tierMods = tier >= 3 ? variety.EndgameModsOrEmpty : tier == 2 ? variety.Part2ModsOrEmpty : variety.Part1ModsOrEmpty;
        if (tierMods.Count == 0) return baseMods;
        if (baseMods.Count == 0) return tierMods;
        var merged = new List<int>(baseMods.Count + tierMods.Count);
        foreach (int m in baseMods) merged.Add(m);
        foreach (int m in tierMods) if (!merged.Contains(m)) merged.Add(m);
        return merged;
    }

    /// <summary>Compatibility overload mapping (part, isMap) to area tier (1 = Part 1, 2 = Part 2, 3 = Endgame).</summary>
    public static IReadOnlyList<int> SpeciesModsForArea(Variety variety, int part, bool isMap) =>
        SpeciesModsForArea(variety, isMap ? 3 : part >= 2 ? 2 : 1);

    /// <summary>
    /// The life and damage a unique map boss has more than its base life and hit at this map level: MonsterMapBossDifficulty
    /// <c>BossLifePercentIncrease</c>/<c>BossDamagePercentIncrease</c> plus MonsterMapDifficulty
    /// <c>LifePercentIncrease</c>/<c>DamagePercentIncrease</c>, added as percentages (level 83: 1 + 18.64 + 1.09 = 20.73
    /// life). Poedb's level-83 King in the Mists life (45,614,731) is this multiplier over 15,319 x 18 x 7.98.
    /// </summary>
    public (double Life, double Damage) MapBossScaling(int level) =>
        _mapBoss is { } s && level >= 1 && level < s.BossLife.Length
            ? (1 + (s.BossLife[level] + s.MapLife[level]) / 100.0,
               1 + (s.BossDamage[level] + s.MapDamage[level]) / 100.0)
            : (1, 1);

    private sealed record MapBossScalingTable(double[] BossLife, double[] BossDamage, double[] MapLife, double[] MapDamage)
    {
        public static MapBossScalingTable? Load(string folder)
        {
            if (!ClientTables.Has("MonsterMapBossDifficulty") || !ClientTables.Has("MonsterMapDifficulty")) return null;
            var b = ClientTables.Table("MonsterMapBossDifficulty");
            var m = ClientTables.Table("MonsterMapDifficulty");
            var bossRows = b
                .Select(r => (Level: r.Int("MapLevel"), Life: r.Real("BossLifePercentIncrease"),
                    Damage: r.Real("BossDamagePercentIncrease"))).ToArray();
            var mapRows = m
                .Select(r => (Level: r.Int("MapLevel"), Life: r.Real("LifePercentIncrease"),
                    Damage: r.Real("DamagePercentIncrease"))).ToArray();
            int size = Math.Max(bossRows.Max(r => r.Level), mapRows.Max(r => r.Level)) + 1;
            var result = new MapBossScalingTable(new double[size], new double[size], new double[size], new double[size]);
            foreach (var r in bossRows) if (r.Level >= 0 && r.Level < size) { result.BossLife[r.Level] = r.Life; result.BossDamage[r.Level] = r.Damage; }
            foreach (var r in mapRows) if (r.Level >= 0 && r.Level < size) { result.MapLife[r.Level] = r.Life; result.MapDamage[r.Level] = r.Damage; }
            return result;
        }
    }

    private sealed record LifeTables(List<(int MonsterLife, int MinionLife, int AltLife1, int AltLife2, double Damage,
        int Experience, int Accuracy, int Evasion, int EvasiveEvasion, double MinionDamage,
        int Armour, int AilmentThreshold, double AltDamage1, double AltDamage2)> Levels,
        List<(bool Minion, bool AltLife1, bool AltLife2, int DamageSpread, bool IgnoresAttackSpeed,
            int Armour, int EnergyShield, int Evasion, int Accuracy)> Types,
        int EnergyShieldRatioOfLife)
    {
        public static LifeTables? Load(string folder)
        {
            if (!ClientTables.Has("DefaultMonsterStats") || !ClientTables.Has("MonsterTypes")) return null;
            var s = ClientTables.Table("DefaultMonsterStats");
            var t = ClientTables.Table("MonsterTypes");
            int esRatio = 40;
            if (ClientTables.TableOrNull("GameConstants") is { } constants)
            {
                foreach (var c in constants)
                {
                    if (c.Text("Id") == "EnergyShieldRatioOfLife")
                    {
                        int val = c.IntOrNull("Value") ?? 40;
                        int div = c.IntOrNull("Divisor") ?? 1;
                        if (val > 0 && div > 0) esRatio = val / div;
                        break;
                    }
                }
            }
            return new LifeTables(
                [.. s.Select(r => (r.Int("MonsterLife"), r.Int("MinionLife"),
                    r.Int("AltLife1"), r.Int("AltLife2"), r.Real("Damage"),
                    r.Int("Experience"), r.Int("Accuracy"), r.Int("Evasion"),
                    r.Int("EvasiveEvasion"), r.Real("MinionDamage"),
                    r.IntOrNull("Armour") ?? 0,
                    r.IntOrNull("AilmentThreshold") ?? r.Int("MonsterLife"),
                    r.RealOrNull("AltDamage1") ?? r.Real("Damage"),
                    r.RealOrNull("AltDamage2") ?? r.Real("Damage")))],
                [.. t.Select(r => (r.Bool("IsPlayerMinion"), r.Bool("AltLife1"),
                    r.Bool("AltLife2"),
                    (r.IntOrNull("DamageSpread") ?? 0),
                    r.BoolOrNull("BaseDamageIgnoresAttackSpeed") == true,
                    r.IntOrNull("Armour") ?? 0,
                    r.IntOrNull("EnergyShield") ?? 0,
                    r.IntOrNull("Evasion") ?? 0,
                    r.IntOrNull("Accuracy") ?? 100))],
                esRatio);
        }
    }

    sealed record RegisteredOverlay(long Id, string Owner, Variety Variety);

    sealed record CatalogSnapshot(
        List<Variety> Rows,
        Dictionary<int, Variety> ByRow,
        Dictionary<string, Variety> ById,
        long LocalRevision,
        long SharedRevision);

    static ImmutableArray<RegisteredOverlay> _sharedOverlays = [];
    static long _sharedNextId;
    static long _sharedRevision;

    ImmutableArray<RegisteredOverlay> _localOverlays = [];
    long _localNextId;
    long _localRevision;
    readonly Lock _snapshotLock = new();
    readonly CatalogSnapshot _stockSnapshot;
    CatalogSnapshot _snapshot;

    /// <summary>Advances whenever shared runtime <see cref="MonsterCatalog"/> overlays change.</summary>
    public static long OverlayRevision => Volatile.Read(ref _sharedRevision);

    /// <summary>Advances whenever this catalog's instance or shared runtime overlays change.</summary>
    public long Revision => Volatile.Read(ref _localRevision) + Volatile.Read(ref _sharedRevision);

    /// <summary>Registers a runtime <see cref="Variety"/> overlay visible to every <see cref="MonsterCatalog"/> instance
    /// until the handle is disposed or <paramref name="owner"/> unloads via <see cref="GameEventOwners"/>.</summary>
    public static IDisposable RegisterOverlay(string owner, Variety variety)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ValidateOverlay(variety);
        long id = Interlocked.Increment(ref _sharedNextId);
        ImmutableInterlocked.Update(ref _sharedOverlays, list => list.Add(new(id, owner, variety)));
        Interlocked.Increment(ref _sharedRevision);
        return GameEventOwners.Own(owner, new Undo(() =>
        {
            if (ImmutableInterlocked.Update(ref _sharedOverlays, list => list.RemoveAll(e => e.Id == id)))
                Interlocked.Increment(ref _sharedRevision);
        }));
    }

    /// <summary>Registers a runtime <see cref="Variety"/> overlay on this <see cref="MonsterCatalog"/> instance until
    /// disposed or <paramref name="owner"/> unloads via <see cref="GameEventOwners"/>.</summary>
    public IDisposable Register(string owner, Variety variety)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ValidateOverlay(variety);
        long id = Interlocked.Increment(ref _localNextId);
        ImmutableInterlocked.Update(ref _localOverlays, list => list.Add(new(id, owner, variety)));
        Interlocked.Increment(ref _localRevision);
        return GameEventOwners.Own(owner, new Undo(() =>
        {
            if (ImmutableInterlocked.Update(ref _localOverlays, list => list.RemoveAll(e => e.Id == id)))
                Interlocked.Increment(ref _localRevision);
        }));
    }

    static void ValidateOverlay(Variety variety)
    {
        ArgumentNullException.ThrowIfNull(variety);
        if (string.IsNullOrWhiteSpace(variety.Id))
            throw new ArgumentException("MonsterCatalog.Variety.Id must be non-empty.", nameof(variety));
    }

    CatalogSnapshot CurrentSnapshot()
    {
        long localRev = Volatile.Read(ref _localRevision);
        long sharedRev = Volatile.Read(ref _sharedRevision);
        var current = Volatile.Read(ref _snapshot);
        if (current.LocalRevision == localRev && current.SharedRevision == sharedRev)
            return current;
        lock (_snapshotLock)
        {
            localRev = Volatile.Read(ref _localRevision);
            sharedRev = Volatile.Read(ref _sharedRevision);
            current = _snapshot;
            if (current.LocalRevision == localRev && current.SharedRevision == sharedRev)
                return current;
            var shared = _sharedOverlays;
            var local = _localOverlays;
            if (shared.IsEmpty && local.IsEmpty)
            {
                var stock = _stockSnapshot with { LocalRevision = localRev, SharedRevision = sharedRev };
                Volatile.Write(ref _snapshot, stock);
                return stock;
            }
            var rebuilt = BuildSnapshot(_rows.Rows, shared, local, localRev, sharedRev);
            Volatile.Write(ref _snapshot, rebuilt);
            return rebuilt;
        }
    }

    static CatalogSnapshot BuildSnapshot(
        List<Variety> stockRows,
        ImmutableArray<RegisteredOverlay> shared,
        ImmutableArray<RegisteredOverlay> local,
        long localRev,
        long sharedRev)
    {
        var rows = new List<Variety>(stockRows.Count + shared.Length + local.Length);
        rows.AddRange(stockRows);
        var byRow = new Dictionary<int, Variety>(rows.Count + shared.Length + local.Length);
        var byId = new Dictionary<string, Variety>(rows.Count + shared.Length + local.Length, StringComparer.OrdinalIgnoreCase);
        var rowPos = new Dictionary<int, int>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            var v = rows[i];
            byRow[v.Row] = v;
            byId.TryAdd(v.Id, v);
            rowPos[v.Row] = i;
        }
        int nextSyntheticRow = stockRows.Count;
        void Apply(ImmutableArray<RegisteredOverlay> entries)
        {
            for (int i = 0; i < entries.Length; i++)
            {
                var v = entries[i].Variety;
                if (v.Row < 0)
                {
                    if (byId.TryGetValue(v.Id, out var existingById))
                        v = v with { Row = existingById.Row };
                    else
                    {
                        while (byRow.ContainsKey(nextSyntheticRow)) nextSyntheticRow++;
                        v = v with { Row = nextSyntheticRow++ };
                    }
                }
                if (byRow.TryGetValue(v.Row, out var replacedRow)
                    && byId.TryGetValue(replacedRow.Id, out var curId) && ReferenceEquals(curId, replacedRow))
                    byId.Remove(replacedRow.Id);
                if (byId.TryGetValue(v.Id, out var replacedId) && replacedId.Row != v.Row)
                {
                    byRow.Remove(replacedId.Row);
                    if (rowPos.Remove(replacedId.Row, out int oldPos))
                    {
                        int lastIdx = rows.Count - 1;
                        if (oldPos < lastIdx)
                        {
                            var moved = rows[lastIdx];
                            rows[oldPos] = moved;
                            rowPos[moved.Row] = oldPos;
                        }
                        rows.RemoveAt(lastIdx);
                    }
                }
                if (rowPos.TryGetValue(v.Row, out int pos))
                    rows[pos] = v;
                else
                {
                    rowPos[v.Row] = rows.Count;
                    rows.Add(v);
                }
                byRow[v.Row] = v;
                byId[v.Id] = v;
            }
        }
        Apply(shared);
        Apply(local);
        return new(rows, byRow, byId, localRev, sharedRev);
    }

    sealed class Undo(Action undo) : IDisposable
    {
        Action? _undo = undo;
        public void Dispose() => Interlocked.Exchange(ref _undo, null)?.Invoke();
    }

    /// <summary>A MonsterVarieties row by number, or by Id: exact, then the last path segment, then any part of it,
    /// then the display name. Several matches: the first one the blob writer supports.</summary>
    /// <summary>Every MonsterVarieties row (the launcher's suggestions).</summary>
    public IReadOnlyList<Variety> All => CurrentSnapshot().Rows;

    public Variety? Find(string query)
    {
        var snap = CurrentSnapshot();
        if (int.TryParse(query, out int row)) return snap.ByRow.GetValueOrDefault(row);
        if (snap.ById.TryGetValue(query, out var exact)) return exact;
        var rows = snap.Rows;
        foreach (Func<Variety, bool> match in new Func<Variety, bool>[]
        {
            v => v.Id.Equals(query, StringComparison.OrdinalIgnoreCase),
            v => v.Id.EndsWith("/" + query, StringComparison.OrdinalIgnoreCase),
            v => v.Name.Equals(query, StringComparison.OrdinalIgnoreCase),
            v => v.Id.Contains(query, StringComparison.OrdinalIgnoreCase),
        })
        {
            var hits = rows.Where(match).ToList();
            if (hits.Count > 0) return hits.FirstOrDefault(v => v.Supported) ?? hits[0];
        }
        return null;
    }

    private static (List<Variety>, string?) Load(string folder)
    {
        if (!ClientTables.Has("MonsterVarieties")) return ([], $"the MonsterVarieties client table is missing from {ClientTables.DefaultPath}");
        var components = new Dictionary<string, IReadOnlyList<string>>();
        var stateMachines = new Dictionary<string, StateMachineData>();
        var stateMetas = new Dictionary<string, IReadOnlyDictionary<string, ObjectTypes.StateFlagMeta>>();
        var transitionStates = new Dictionary<string, byte>();
        var chains = new Dictionary<string, string[]>();
        var lifeData = new Dictionary<string, MonsterLifeData>();
        var blocking = new Dictionary<string, bool>();
        var phasing = new HashSet<string>();
        var immobile = new HashSet<string>();
        var idleSlowAnimations = new Dictionary<string, bool?>();
        if (ClientTables.TableOrNull("object_types") is { } objectTypes)
        {
            var sharedStates = ObjectTypes.SharedStates();
            var stateMetaLookup = ObjectTypes.StateMeta();
            foreach (var t in objectTypes)
            {
                string type = t.Text("type")!;
                components[type] = ObjectTypes.Components(t);
                chains[type] = t.Texts("chain");
                if (ObjectTypes.Flag(t, "positioned_blocking") is bool block) blocking[type] = block;
                if (t.BoolOrNull("phases") == true) phasing.Add(type);
                if (t.BoolOrNull("immobile") == true) immobile.Add(type);
                idleSlowAnimations[type] = t.BoolOrNull("actor_slow_animations_go_to_idle");
                // Data generated before the StateMachine data has no shared timers: those types stay unsupported.
                if (ObjectTypes.StateMachine(t, sharedStates) is { } machine) stateMachines[type] = machine;
                if (ObjectTypes.StateMetaFor(t, stateMetaLookup) is { } meta) stateMetas[type] = meta;
                if (t.IntOrNull("transitionable_num_states") is int numStates)
                    transitionStates[type] = (byte)Math.Clamp(numStates, 1, 127);
                // A type with Life has its three flags; one without has none.
                if (!t.IsNull("life_corpse_usable") || !t.IsNull("life_prevent_corpse_sinking") || !t.IsNull("life_corpse_use_veto"))
                    lifeData[type] = new(t.BoolOrNull("life_corpse_usable"), t.BoolOrNull("life_prevent_corpse_sinking"),
                        t.BoolOrNull("life_corpse_use_veto"));
            }
        }
        var rangedVisuals = RangedWeaponVisuals(folder);
        // Its art is a base bow's or wand's, or its main hand is of those classes: a unique's art is no base item's
        // (Ainsley Varrich's Windripper, UniqueBow9), and his Default Attack and Elemental Hit swung in melee with no arrow.
        var rangedClasses = ClientTables.Has("ItemClasses")
            ? ClientTables.Table("ItemClasses").Select((r, i) => (Id: r.Text("Id"), i)).Where(p => p.Id is "Bow" or "Wand").Select(p => p.i).ToHashSet()
            : new HashSet<int>();
        bool RangedWeapon(ClientRow row) => row.IntOrNull("MainHand_ItemClassesKey") is int hand && rangedClasses.Contains(hand)
            || new[] { "Weapon1_ItemVisualIdentityKeys", "Weapon2_ItemVisualIdentityKeys" }.Any(name => row.Ints(name).Any(rangedVisuals.Contains));
        static int Column(ClientRow row, string name) =>
            (row.IntOrNull(name) ?? 0);
        var (visualHashes, classStances) = WeaponTables();
        // The ItemClasses rows for bows and armour pieces, and the type and stance of a model entry that is no weapon
        // (as Game.BowSetup's).
        const byte NoWeaponType = 0x68, NoStance = 0xff;
        int bowClass = -1, bodyArmourClass = -1, helmetClass = -1, glovesClass = -1, bootsClass = -1;
        if (ClientTables.Has("ItemClasses"))
        {
            var itemClasses = ClientTables.Table("ItemClasses");
            for (int i = 0; i < itemClasses.Count; i++)
            {
                switch (itemClasses[i].Text("Id"))
                {
                    case "Bow": bowClass = i; break;
                    case "Body Armour": bodyArmourClass = i; break;
                    case "Helmet": helmetClass = i; break;
                    case "Gloves": glovesClass = i; break;
                    case "Boots": bootsClass = i; break;
                }
            }
        }
        static byte ClassType(int row) => row is >= 0 and < 255 ? (byte)(row + 1) : NoWeaponType;
        byte bodyType = ClassType(bodyArmourClass), helmetType = ClassType(helmetClass),
            glovesType = ClassType(glovesClass), bootsType = ClassType(bootsClass);
        var monsterArmours = MonsterArmourVisuals(bodyType, helmetType, glovesType, bootsType);
        IReadOnlyList<GameServer.Core.Protocol.Instance.VisibleItem> Weapons(ClientRow row)
        {
            var held = new List<GameServer.Core.Protocol.Instance.VisibleItem>();
            int usedSlots = 0;
            void AddSlot(GameServer.Core.Protocol.Instance.VisibleItem item)
            {
                if ((usedSlots & (1 << item.Slot)) != 0) return;
                usedSlots |= 1 << item.Slot;
                held.Add(item);
            }
            void Hold(byte slot, string visuals, string itemClass)
            {
                int visual = row.Ints(visuals).FirstOrDefault(-1);
                if (visual < 0 || row.IntOrNull(itemClass) is not int type) return;
                if (visual < visualHashes.Count && type < classStances.Count && type < 255)
                    AddSlot(new(slot, visualHashes[visual], classStances[type], (byte)(type + 1)));
            }
            void Visual(byte slot, int visual, byte type = NoWeaponType)
            {
                if (visual >= 0 && visual < visualHashes.Count)
                    AddSlot(new(slot, visualHashes[visual], NoStance, type));
            }
            Hold(0, "Weapon1_ItemVisualIdentityKeys", "MainHand_ItemClassesKey");
            // A bow is held as a player's is (13-items.md "Bow model records"): the row's Weapon1 is the arrow in the
            // right hand (slot 0, the Bow type and stance), its Weapon2 the bow in the left (slot 1), its Back the quiver
            // (slot 7), both of those no weapon (0x68) and no stance. The row names no off-hand class for it: every one
            // of the 518 bow varieties lists the arrow first and the bow second (Ainsley Varrich held only an arrow).
            if (bowClass >= 0 && row.IntOrNull("MainHand_ItemClassesKey") == bowClass && row.IsNull("OffHand_ItemClassesKey"))
                Visual(1, row.Ints("Weapon2_ItemVisualIdentityKeys").FirstOrDefault(-1));
            else
                Hold(1, "Weapon2_ItemVisualIdentityKeys", "OffHand_ItemClassesKey");
            // Client 0x1416632b0: slot 4 = Head_Attachment (helmet), slot 7 = Back (quiver, cloak, wings).
            if (row.IntOrNull("Helmet_ItemVisualIdentityKey") is int helmet) Visual(4, helmet, helmetType);
            if (row.IntOrNull("Back_ItemVisualIdentityKey") is int back) Visual(7, back);
            if (row.IntOrNull("MonsterArmoursKey") is int armourKey && armourKey >= 0 && armourKey < monsterArmours.Count)
                foreach (var piece in monsterArmours[armourKey]) AddSlot(piece);
            return held;
        }
        var rows = new List<Variety>();
        var varieties = ClientTables.Table("MonsterVarieties");
        {
            foreach (var r in varieties)
            {
                string id = r.Text("Id") ?? "";
                string baseType = r.Text("BaseMonsterTypeIndex") ?? "";
                // The .ot the client builds the object from: a type file under the variety's own Id when there is one
                // (Metadata/Monsters/KitavaBoss/KitavaFinal.ot, whose base column names Kitava1), else the base type. With
                // Kitava1's list, KitavaFinal got a Transitionable byte its own type does not read, which the client took
                // for the task count: "Buffer Underflow" (live 2026-10-30). 17 varieties serialise differently that way.
                string type = components.ContainsKey(id) ? id : baseType;
                string? ao = r.Texts("AOFiles").FirstOrDefault();
                // The add-on types (AddonMonsterTypeIndex) join the client's type after the base: their shared states and
                // timers follow the base's in the StateMachine data. Without them a Coconut Crab (add-on
                // DeepwaterBaseWaveOnly: state size, timer despawn) gave "Buffer Underflow" (live 2026-11-19, the client
                // read wave_monster, size and one timer). Add-on components are not merged (not traced). An add-on the type
                // already inherits adds nothing (Abberath2Gauntlet lists itself).
                var machine = stateMachines.GetValueOrDefault(type);
                var meta = stateMetas.GetValueOrDefault(type);
                Dictionary<string, ObjectTypes.StateFlagMeta>? mergedMeta = null;
                var chain = chains.GetValueOrDefault(type) ?? [];
                foreach (string addon in r.Texts("AddonMonsterTypeIndex"))
                    if (machine is not null && addon != type && !chain.Contains(addon) && stateMachines.GetValueOrDefault(addon) is { } extra)
                    {
                        machine = new StateMachineData([.. machine.SharedStates, .. extra.SharedStates], machine.SharedTimers + extra.SharedTimers,
                            [.. machine.Names ?? machine.SharedStates.Select(_ => ""), .. extra.Names ?? extra.SharedStates.Select(_ => "")]);
                        if (stateMetas.GetValueOrDefault(addon) is { } extraMeta)
                        {
                            if (mergedMeta is null)
                                mergedMeta = meta is null ? new Dictionary<string, ObjectTypes.StateFlagMeta>() : new Dictionary<string, ObjectTypes.StateFlagMeta>(meta);
                            foreach (var (k, v) in extraMeta)
                                mergedMeta.TryAdd(k, v);
                        }
                    }
                int[] speciesMods = new[] { "ModsKeys", "ModsKeys2", "Special_ModsKeys" }
                    .SelectMany(r.Ints)
                    .Distinct().ToArray();
                int[] activeSpeciesMods = new[] { "ModsKeys", "Special_ModsKeys" }
                    .SelectMany(r.Ints)
                    .Distinct().ToArray();
                int[] part1Mods = r.Ints("Part1_ModsKeys");
                int[] part2Mods = r.Ints("Part2_ModsKeys");
                int[] endgameMods = r.Ints("Endgame_ModsKeys");
                int lifeMult = r.Int("LifeMultiplier");
                int ailmentThreshold = r.IntOrNull("AilmentThreshold") is int at and > 0 ? at : lifeMult;
                rows.Add(new Variety(rows.Count, id, r.Text("Name") ?? "", baseType, ao,
                    components.Count == 0 ? null : components.GetValueOrDefault(type),
                    lifeMult,
                    !r.IsNull("MonsterTypesKey") ? r.Int("MonsterTypesKey") : -1,
                    machine, transitionStates.GetValueOrDefault(type, (byte)1),
                    r.Int("MovementSpeed"), r.Int("ObjectSize"), r.Int("MaxAgroRange"),
                    [.. r.Ints("GrantedEffectsKeys")],
                    r.Int("AttackSpeed"), r.Int("MaximumAttackDistance"),
                    r.Int("DamageMultiplier"), r.Int("ExperienceMultiplier"),
                    r.BoolOrNull("BossHealthBar") == true, speciesMods)
                    {
                        LifeData = lifeData.GetValueOrDefault(type) ?? new(null, null, null), RangedWeapon = RangedWeapon(r), Weapons = Weapons(r),
                        PositionedBlocking = blocking.GetValueOrDefault(type, true), Phases = phasing.Contains(type), Immobile = immobile.Contains(type),
                        Chain = chain,
                        StateMeta = mergedMeta ?? meta ?? EmptyStateMeta,
                        SlowAnimationsGoToIdle = r.Texts("AddonMonsterTypeIndex").Length == 0 ? idleSlowAnimations.GetValueOrDefault(type) : null,
                        Hash16 = (ushort)r.Int("HASH16"),
                        SpeedRollMin = Column(r, "__col75"), SpeedRollMax = Column(r, "__col76"),
                        ModelScale = Column(r, "ModelSizeMultiplier") is int scale and > 0 ? scale / 100.0 : 1,
                        MinAgroRange = r.IntOrNull("MinAgroRange") ?? 0,
                        AilmentThreshold = ailmentThreshold,
                        MinimumAttackDistance = r.IntOrNull("MinimumAttackDistance") ?? 0,
                        ActiveSpeciesMods = activeSpeciesMods,
                        Part1Mods = part1Mods,
                        Part2Mods = part2Mods,
                        EndgameMods = endgameMods,
                    });
            }
        }
        // With object-types.json, a type missing from it has no .ot: do not guess.
        if (components.Count > 0)
            rows = [.. rows.Select(v => v.Components is null ? v with { Components = [] } : v)];
        return (rows, null);
    }

    // ItemVisualIdentity HASH16 by row and ItemClasses ItemStance by row (0xff without one).
    static (List<ushort> Visuals, List<byte> Stances) WeaponTables()
    {
        if (!ClientTables.Has("ItemVisualIdentity") || !ClientTables.Has("ItemClasses")) return ([], []);
        return ([.. ClientTables.Table("ItemVisualIdentity").Select(r => (ushort)r.Int("HASH16"))],
            [.. ClientTables.Table("ItemClasses").Select(r => r.IntOrNull("ItemStance") is int s ? (byte)s : (byte)0xff)]);
    }

    static readonly string[] VisualSmColumns =
    [
        "SMFiles", "MarauderSMFiles", "RangerSMFiles", "WitchSMFiles",
        "DuelistDexSMFiles", "TemplarSMFiles", "ShadowSMFiles", "ScionSMFiles",
    ];

    static string? ArmourFolderKey(string normalizedPath)
    {
        int start = normalizedPath.IndexOf("items/armours/", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        string rel = normalizedPath[start..];
        int slash = rel.LastIndexOf('/');
        if (slash <= "items/armours/".Length) return null;
        string folder = rel[..slash];
        string leaf = folder[(folder.LastIndexOf('/') + 1)..];
        if (leaf.Equals("bodyarmours", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("helmets", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("gloves", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("boots", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("unique", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("microtransactions", StringComparison.OrdinalIgnoreCase))
            return null;
        return folder;
    }

    static string? ArmourAoStemKey(string normalizedPath)
    {
        if (!normalizedPath.EndsWith(".ao", StringComparison.OrdinalIgnoreCase)) return null;
        int start = normalizedPath.IndexOf("items/armours/", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        return normalizedPath[start..^3];
    }

    // Resolves MonsterArmours.ArtString_SMFile entries into Actor model slots (2=BodyArmour, 4=Helmet, 5=Gloves, 6=Boots)
    // matching ItemVisualIdentity HASH16 values by exact .sm path first, then by armour folder key.
    static List<IReadOnlyList<GameServer.Core.Protocol.Instance.VisibleItem>> MonsterArmourVisuals(
        byte bodyType, byte helmetType, byte glovesType, byte bootsType)
    {
        if (!ClientTables.Has("MonsterArmours") || !ClientTables.Has("ItemVisualIdentity")) return [];
        const byte noStance = 0xff;
        var bySmFile = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        var byArmourFolder = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in ClientTables.Table("ItemVisualIdentity"))
        {
            ushort hash = (ushort)row.Int("HASH16");
            foreach (string col in VisualSmColumns)
            {
                foreach (string raw in row.Texts(col))
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    string norm = raw.Replace('\\', '/');
                    bySmFile.TryAdd(norm, hash);
                    if (ArmourFolderKey(norm) is string folder) byArmourFolder.TryAdd(folder, hash);
                }
            }
            foreach (string aoCol in new[] { "AOFile", "AOFile2" })
            {
                if (row.Text(aoCol) is not { Length: > 0 } ao) continue;
                string norm = ao.Replace('\\', '/');
                if (ArmourAoStemKey(norm) is string stem) byArmourFolder.TryAdd(stem, hash);
                if (ArmourFolderKey(norm) is string folder) byArmourFolder.TryAdd(folder, hash);
            }
        }
        var result = new List<IReadOnlyList<GameServer.Core.Protocol.Instance.VisibleItem>>();
        foreach (var row in ClientTables.Table("MonsterArmours"))
        {
            string? art = row.Text("ArtString_SMFile");
            if (string.IsNullOrWhiteSpace(art)) { result.Add([]); continue; }
            var pieces = new List<GameServer.Core.Protocol.Instance.VisibleItem>();
            int usedSlots = 0;
            foreach (string group in art.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (string variant in group.Split('!', StringSplitOptions.RemoveEmptyEntries))
                {
                    string clean = variant;
                    int tilde = clean.IndexOf('~');
                    if (tilde >= 0) clean = clean[..tilde];
                    int dash = clean.IndexOf(" -", StringComparison.Ordinal);
                    if (dash >= 0) clean = clean[..dash];
                    string sm = clean.Trim().Replace('\\', '/');
                    if (!sm.EndsWith(".sm", StringComparison.OrdinalIgnoreCase))
                        sm = sm.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                            .FirstOrDefault(p => p.EndsWith(".sm", StringComparison.OrdinalIgnoreCase)) ?? "";
                    if (sm.Length == 0) continue;
                    (byte Slot, byte Type)? target = sm.Contains("/bodyarmours/", StringComparison.OrdinalIgnoreCase)
                        ? ((byte)2, bodyType)
                        : sm.Contains("/helmets/", StringComparison.OrdinalIgnoreCase) ? ((byte)4, helmetType)
                        : sm.Contains("/gloves/", StringComparison.OrdinalIgnoreCase) ? ((byte)5, glovesType)
                        : sm.Contains("/boots/", StringComparison.OrdinalIgnoreCase) ? ((byte)6, bootsType)
                        : null;
                    if (target is not { } t || (usedSlots & (1 << t.Slot)) != 0) continue;
                    if (!bySmFile.TryGetValue(sm, out ushort hash)
                        && (ArmourFolderKey(sm) is not string folder || !byArmourFolder.TryGetValue(folder, out hash)))
                        continue;
                    usedSlots |= 1 << t.Slot;
                    pieces.Add(new(t.Slot, hash, noStance, t.Type));
                    break;
                }
            }
            result.Add(pieces);
        }
        return result;
    }

    static HashSet<int> RangedWeaponVisuals(string folder)
    {
        if (!ClientTables.Has("ItemClasses") || !ClientTables.Has("BaseItemTypes")) return [];
        var c = ClientTables.Table("ItemClasses");
        var b = ClientTables.Table("BaseItemTypes");
        var rangedClasses = c.Select((row, index) => (row, index))
            .Where(p => p.row.Text("Id") is "Bow" or "Wand").Select(p => p.index).ToHashSet();
        return b
            .Where(row => row.IntOrNull("ItemClassesKey") is int k && rangedClasses.Contains(k)
                && !row.IsNull("ItemVisualIdentity"))
            .Select(row => row.Int("ItemVisualIdentity")).ToHashSet();
    }
}
