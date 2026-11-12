using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core;

/// <summary>
/// The monsters /spawn_mob can create: MonsterVarieties rows from the extracted tables
/// (the MonsterVarieties client table), with each type's
/// deserialized components from data/object-types.json (`python -I scripts/re/ot_components.py --all --json
/// data/object-types.json`). Without that file every variety is assumed to have the base Monster list, which is wrong
/// for about half of them (StateMachine, DiesAfterTime …) and then the client shows "Buffer Underflow".
/// Both files are game data: they stay in data/ and are loaded on first use.
/// </summary>
public sealed class MonsterCatalog(string folder = "data")
{
    public sealed record Variety(int Row, string Id, string Name, string BaseType, string? AnimatedObject, IReadOnlyList<string>? Components,
        int LifeMultiplier = 100, int MonsterType = -1, StateMachineData? StateMachine = null, byte TransitionStates = 1,
        int MovementSpeed = 30, int ObjectSize = 3, int AggroRange = 60, IReadOnlyList<int>? GrantedEffects = null,
        int AttackMs = 1500, int AttackRange = 8, int DamageMultiplier = 100, int ExperienceMultiplier = 100, bool BossHealthBar = false)
    {
        public MonsterLifeData LifeData { get; init; } = new(null, null, null);
        public bool RangedWeapon { get; init; }
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
        public IReadOnlyList<string> ComponentsOrBase => Components ?? MonsterObject.BaseComponents;
        public bool Supported => MonsterObject.CanSerialize(ComponentsOrBase, StateMachine is not null);
        public override string ToString() => $"{Name} ({Id}, row {Row})";
    }

    private readonly Lazy<(List<Variety> Rows, string? Error)> _rows = new(() => Load(folder));
    private readonly Lazy<LifeTables?> _life = new(() => LifeTables.Load(folder));
    private readonly Lazy<MapBossScalingTable?> _mapBoss = new(() => MapBossScalingTable.Load(folder));
    private readonly MonsterResistanceCatalog _resistance = new(folder);
    private readonly Lazy<Dictionary<string, HitAnimation[]>> _takeHit = new(LoadTakeHit);

    public string? Error => _rows.Value.Error;

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
    public IReadOnlyList<HitAnimation> TakeHit(string id) => _takeHit.Value.GetValueOrDefault(id) ?? [];

    /// <summary>
    /// When the variety's death clip clears the corpse's body from the client's paths, in ms from the clip's start (its
    /// remove_collidability event; client.db's monster_takehit death_unblock), null when it has none: the client then
    /// clears the body with the death itself. 150 without the table (most varieties' value).
    /// </summary>
    public int? CorpseUnblockMs(string id) => _corpse.Value is { } known ? known.GetValueOrDefault(id) : 150;

    private readonly Lazy<Dictionary<string, int?>?> _corpse = new(LoadCorpse);

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
        if (_life.Value is not { } t || level < 1 || level > t.Levels.Count) return null;
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
        if (_life.Value is not { } t || level < 1 || level > t.Levels.Count) return null;
        var type = v.MonsterType >= 0 && v.MonsterType < t.Types.Count ? t.Types[v.MonsterType] : default;
        var row = t.Levels[level - 1];
        double d = (type.Minion ? row.MinionDamage : row.Damage) * v.DamageMultiplier / 100.0 * (type.IgnoresAttackSpeed ? 1 : v.AttackMs / 1000.0);
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
        _life.Value is { } t && level >= 1 && level <= t.Levels.Count ? t.Levels[level - 1].Experience : null;

    /// <summary>A normal monster's damage at this level, before a variety's multiplier: DefaultMonsterStats `Damage`.
    /// Used by the defence probe; the actual hit spread is in <see cref="BaseDamage(Variety, byte)"/>.</summary>
    public int? BaseDamage(int level) =>
        _life.Value is { } t && level >= 1 && level <= t.Levels.Count ? (int)Math.Round(t.Levels[level - 1].Damage) : null;

    /// <summary>A monster's accuracy at this level: DefaultMonsterStats `Accuracy`.</summary>
    public int? BaseAccuracy(int level) =>
        _life.Value is { } t && level >= 1 && level <= t.Levels.Count ? t.Levels[level - 1].Accuracy : null;

    /// <summary>A monster's evasion rating at this level: DefaultMonsterStats `Evasion` (the same table PoB uses for
    /// <c>monsterEvasionTable</c>). Null when the tables are missing.</summary>
    public int? BaseEvasion(int level) =>
        _life.Value is { } t && level >= 1 && level <= t.Levels.Count ? t.Levels[level - 1].Evasion : null;

    /// <summary>The evasion of an evasive (high-dexterity) monster at this level: DefaultMonsterStats `EvasiveEvasion`.
    /// The character panel's "Chance to Hit Evasive Monsters" row uses it.</summary>
    public int? BaseEvasiveEvasion(int level) =>
        _life.Value is { } t && level >= 1 && level <= t.Levels.Count ? t.Levels[level - 1].EvasiveEvasion : null;

    /// <summary>
    /// The life and damage a unique map boss has more than its base life and hit at this map level: MonsterMapBossDifficulty
    /// <c>BossLifePercentIncrease</c>/<c>BossDamagePercentIncrease</c> plus MonsterMapDifficulty
    /// <c>LifePercentIncrease</c>/<c>DamagePercentIncrease</c>, added as percentages (level 83: 1 + 18.64 + 1.09 = 20.73
    /// life). Poedb's level-83 King in the Mists life (45,614,731) is this multiplier over 15,319 x 18 x 7.98.
    /// </summary>
    public (double Life, double Damage) MapBossScaling(int level) =>
        _mapBoss.Value is { } s && level >= 1 && level < s.BossLife.Length
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
        int Experience, int Accuracy, int Evasion, int EvasiveEvasion, double MinionDamage)> Levels,
        List<(bool Minion, bool AltLife1, bool AltLife2, int DamageSpread, bool IgnoresAttackSpeed)> Types)
    {
        public static LifeTables? Load(string folder)
        {
            if (!ClientTables.Has("DefaultMonsterStats") || !ClientTables.Has("MonsterTypes")) return null;
            var s = ClientTables.Table("DefaultMonsterStats");
            var t = ClientTables.Table("MonsterTypes");
            return new LifeTables(
                [.. s.Select(r => (r.Int("MonsterLife"), r.Int("MinionLife"),
                    r.Int("AltLife1"), r.Int("AltLife2"), r.Real("Damage"),
                    r.Int("Experience"), r.Int("Accuracy"), r.Int("Evasion"),
                    r.Int("EvasiveEvasion"), r.Real("MinionDamage")))],
                [.. t.Select(r => (r.Bool("IsPlayerMinion"), r.Bool("AltLife1"),
                    r.Bool("AltLife2"),
                    (r.IntOrNull("DamageSpread") ?? 0),
                    r.BoolOrNull("BaseDamageIgnoresAttackSpeed") == true))]);
        }
    }

    /// <summary>A MonsterVarieties row by number, or by Id: exact, then the last path segment, then any part of it,
    /// then the display name. Several matches: the first one the blob writer supports.</summary>
    /// <summary>Every MonsterVarieties row (the launcher's suggestions).</summary>
    public IReadOnlyList<Variety> All => _rows.Value.Rows;

    public Variety? Find(string query)
    {
        var rows = _rows.Value.Rows;
        if (int.TryParse(query, out int row)) return row >= 0 && row < rows.Count ? rows[row] : null;
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
        var transitionStates = new Dictionary<string, byte>();
        var lifeData = new Dictionary<string, MonsterLifeData>();
        var blocking = new Dictionary<string, bool>();
        var phasing = new HashSet<string>();
        var immobile = new HashSet<string>();
        if (ClientTables.TableOrNull("object_types") is { } objectTypes)
        {
            var sharedStates = ObjectTypes.SharedStates();
            foreach (var t in objectTypes)
            {
                string type = t.Text("type")!;
                components[type] = ObjectTypes.Components(t);
                if (ObjectTypes.Flag(t, "positioned_blocking") is bool block) blocking[type] = block;
                if (t.BoolOrNull("phases") == true) phasing.Add(type);
                if (t.BoolOrNull("immobile") == true) immobile.Add(type);
                // Data generated before the StateMachine data has no shared timers: those types stay unsupported.
                if (ObjectTypes.StateMachine(t, sharedStates) is { } machine) stateMachines[type] = machine;
                if (t.IntOrNull("transitionable_num_states") is int numStates)
                    transitionStates[type] = (byte)Math.Clamp(numStates, 1, 127);
                // A type with Life has its three flags; one without has none.
                if (!t.IsNull("life_corpse_usable") || !t.IsNull("life_prevent_corpse_sinking") || !t.IsNull("life_corpse_use_veto"))
                    lifeData[type] = new(t.BoolOrNull("life_corpse_usable"), t.BoolOrNull("life_prevent_corpse_sinking"),
                        t.BoolOrNull("life_corpse_use_veto"));
            }
        }
        var rangedVisuals = RangedWeaponVisuals(folder);
        bool RangedWeapon(ClientRow row) => new[] { "Weapon1_ItemVisualIdentityKeys", "Weapon2_ItemVisualIdentityKeys" }
            .Any(name => row.Ints(name).Any(rangedVisuals.Contains));
        static int Column(ClientRow row, string name) =>
            (row.IntOrNull(name) ?? 0);
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
                rows.Add(new Variety(rows.Count, id, r.Text("Name") ?? "", baseType, ao,
                    components.Count == 0 ? null : components.GetValueOrDefault(type),
                    r.Int("LifeMultiplier"),
                    !r.IsNull("MonsterTypesKey") ? r.Int("MonsterTypesKey") : -1,
                    stateMachines.GetValueOrDefault(type), transitionStates.GetValueOrDefault(type, (byte)1),
                    r.Int("MovementSpeed"), r.Int("ObjectSize"), r.Int("MaxAgroRange"),
                    [.. r.Ints("GrantedEffectsKeys")],
                    r.Int("AttackSpeed"), r.Int("MaximumAttackDistance"),
                    r.Int("DamageMultiplier"), r.Int("ExperienceMultiplier"),
                    r.BoolOrNull("BossHealthBar") == true)
                    {
                        LifeData = lifeData.GetValueOrDefault(type) ?? new(null, null, null), RangedWeapon = RangedWeapon(r),
                        PositionedBlocking = blocking.GetValueOrDefault(type, true), Phases = phasing.Contains(type), Immobile = immobile.Contains(type),
                        Hash16 = (ushort)r.Int("HASH16"),
                        SpeedRollMin = Column(r, "__col75"), SpeedRollMax = Column(r, "__col76"),
                        ModelScale = Column(r, "ModelSizeMultiplier") is int scale and > 0 ? scale / 100.0 : 1,
                    });
            }
        }
        // With object-types.json, a type missing from it has no .ot: do not guess.
        if (components.Count > 0)
            rows = [.. rows.Select(v => v.Components is null ? v with { Components = [] } : v)];
        return (rows, null);
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
