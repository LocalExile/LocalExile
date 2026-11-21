using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Crucible;

/// <summary>
/// Crucible's <c>client.db</c> tables, loaded once per process: <c>WeaponPassiveSkills</c>,
/// <c>WeaponPassiveSkillTypes</c>, <c>WeaponPassiveTreeBalancePerItemLevel</c>,
/// <c>WeaponPassiveTreeUniqueBaseTypes</c>, <c>CrucibleEndgameMonsterPacks</c>,
/// <c>CrucibleLifeScalingPerLevel</c>, and Crucible world object types.
/// </summary>
public sealed class CrucibleTables
{
    public const string LeagueStat = "map_crucible_league";
    public const string InZoneStat = "in_crucible_zone";
    public const string ChannelWeaponAnimStat = "crucible_channel_selected_weapon_animation_set";
    public const string ChannelWeaponVisualStat = "crucible_channel_selected_weapon_visual_identity_hash";
    /// <summary>The more life of <c>CrucibleLifeScalingPerLevel</c> (<c>set_additional_life_scaling_index = 14</c> in
    /// <c>CrucibleMonsterBase.ot</c>). The name is ours: the client has no stat for it.</summary>
    public const string LevelMoreLifeStat = "crucible_level_scaling_life_+%_final";
    public const string MonsterMoreDamageStat = "map_crucible_monster_damage_+%_final";
    public const string MonsterDamageTakenStat = "map_crucible_monster_damage_taken_+%_final";

    public const string MagmaticOreId = "Metadata/Items/Currency/CurrencyAddCrucibleExperience";
    public const string IgneousGeodeId = "Metadata/Items/Currency/CrucibleGeode";
    public const string CrystallineGeodeId = "Metadata/Items/Currency/CrucibleGeodeUber";
    public const string PrimevalRemnantId = "Metadata/Items/MapFragments/Crucible";
    public const string PrimordialRemnantId = "Metadata/Items/MapFragments/CrucibleUber";

    /// <summary>Experience in a Magmatic Ore of the sell node. Our own number, GGG never published it.</summary>
    public const uint SellNodeOreExperience = 40_000;

    public const string ForgeAreaId = "CrucibleLeagueArea";
    public const string ForgeUberAreaId = "CrucibleLeagueAreaUber";

    public const string DeviceType = "Metadata/Terrain/Leagues/Crucible/Objects/CrucibleDevice";
    public const string CombiningBenchType = "Metadata/Terrain/Leagues/Crucible/Objects/CrucibleCombiningBench";
    public const string SpawnerBaseType = "Metadata/Terrain/Leagues/Crucible/Objects/CrucibleEndgameMonstersSpawnerBase";
    public const string FissureType = "Metadata/MiscellaneousObjects/Crucible/Fissure";
    public const string MoltenPoolType = "Metadata/MiscellaneousObjects/Crucible/MoltenPool";

    /// <summary>One row of <c>WeaponPassiveSkills.dat</c>.</summary>
    public sealed record SkillRow(
        int Row,
        string Id,
        int Tier,
        int ModRow,
        int[] SpawnColumns,
        int[] CrucibleTagRows);

    /// <summary>One row of <c>WeaponPassiveTreeBalancePerItemLevel.dat</c>. A bar is the cumulative total that unlocks
    /// its column, not a cost: the client compares the same experience against each (<c>0x1424bcd20</c>).</summary>
    public sealed record LevelBalance(int Level, int Bar1, int Bar2, int Bar3, int Bar4, int Bar5)
    {
        /// <summary>The total experience that unlocks the 0-based <paramref name="column0"/>.</summary>
        public int BarToUnlock(int column0) => Bar(column0 + 1);

        public int Bar(int column) => column switch
        {
            1 => Bar1,
            2 => Bar2,
            3 => Bar3,
            4 => Bar4,
            _ => Bar5,
        };
    }

    /// <summary>One row of <c>CrucibleEndgameMonsterPacks.dat</c>: <c>__col1[i]</c> read as how many of a theme's
    /// monster number i + 1 the pack holds (the row names repeat it: "Tier4_3" is three of the fourth), <c>__col3</c>
    /// as the row's weight. A guess from the values; nothing in the client reads the table.</summary>
    public sealed record EndgamePackRow(int[] TierCounts, int Weight);

    /// <summary>Cached world object metadata for Crucible objects.</summary>
    public sealed record ObjectTypeInfo(IReadOnlyList<string> Components, StateMachineData? States, int Size, bool Blocking, bool HasStationaryCollision = false);

    public IReadOnlyList<SkillRow> Skills { get; }
    public IReadOnlyDictionary<int, SkillRow> SkillByModRow { get; }
    public IReadOnlyDictionary<string, IReadOnlyList<SkillRow>> SkillsByFamilyId { get; }
    /// <summary><c>WeaponPassiveTreeUniqueBaseTypes</c>: the <c>BaseItemTypes</c> row for each value (<c>__col1</c>) of the
    /// "sells for an additional Unique {0}" stat.</summary>
    public IReadOnlyDictionary<int, int> SellUniqueBaseRows { get; }
    public IReadOnlyList<EndgamePackRow> EndgamePacks { get; }
    public IReadOnlyDictionary<string, ObjectTypeInfo> Objects { get; }
    /// <summary><c>BuffDefinitions</c> <c>crucible_frozen</c>: a molten monster held at the Forge (cannot be damaged, takes
    /// no actions), and its art by rarity, <c>BuffVisuals</c> <c>crucible_stasis</c>, <c>_magic</c>, <c>_rare</c>, <c>_unique</c>.</summary>
    public ushort? FrozenBuff { get; }
    public ushort?[] StasisVisuals { get; }

    readonly LevelBalance?[] _balance;
    readonly int[] _moreLife;
    readonly Dictionary<int, int?> _classCategory;

    static readonly Lazy<CrucibleTables> Loaded = new(() => new CrucibleTables());
    public static CrucibleTables Current => Loaded.Value;

    CrucibleTables()
    {
        Skills = [.. ClientTables.TableOrEmpty("WeaponPassiveSkills").Select(r => new SkillRow(
            r.Index,
            r.Text("Id") ?? "",
            r.Int("Tier"),
            r.Int("Mod"),
            [.. r.Ints("NodeSpawnLocation")],
            [.. r.Ints("Tags")]))];

        var byMod = new Dictionary<int, SkillRow>(Skills.Count);
        foreach (var s in Skills) byMod.TryAdd(s.ModRow, s);
        SkillByModRow = byMod;

        SkillsByFamilyId = Skills
            .GroupBy(s => s.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<SkillRow>)[.. g.OrderBy(x => x.Tier)], StringComparer.Ordinal);

        var balances = ClientTables.TableOrEmpty("WeaponPassiveTreeBalancePerItemLevel")
            .Select(r => new LevelBalance(r.Int("Level"), r.Int("Bar1"), r.Int("Bar2"), r.Int("Bar3"), r.Int("Bar4"), r.Int("Bar5")))
            .ToList();
        int maxBal = balances.Count == 0 ? 0 : balances.Max(b => b.Level);
        _balance = new LevelBalance?[maxBal + 1];
        foreach (var b in balances)
            if (b.Level >= 0 && b.Level < _balance.Length) _balance[b.Level] = b;

        SellUniqueBaseRows = ClientTables.TableOrEmpty("WeaponPassiveTreeUniqueBaseTypes")
            .ToDictionary(r => r.Int("__col1"), r => r.Int("UniqueBase"));

        EndgamePacks = [.. ClientTables.TableOrEmpty("CrucibleEndgameMonsterPacks").Select(r =>
            new EndgamePackRow([.. r.Ints("__col1")], r.Int("__col3")))];
        _classCategory = ClientTables.TableOrEmpty("ItemClasses").ToDictionary(r => r.Index, r => r.IntOrNull("ItemClassCategory"));

        var moreLife = ClientTables.TableOrEmpty("CrucibleLifeScalingPerLevel")
            .Select(r => (Level: r.Int("Level"), More: r.Int("MoreLife")))
            .ToList();
        int maxLife = moreLife.Count == 0 ? 0 : moreLife.Max(l => l.Level);
        _moreLife = new int[maxLife + 1];
        foreach (var (level, more) in moreLife)
            if (level >= 0 && level < _moreLife.Length) _moreLife[level] = more;

        FrozenBuff = ClientTables.TableOrEmpty("BuffDefinitions").FirstOrDefault(r => r.Text("Id") == "crucible_frozen")
            is { Table: not null } frozen ? (ushort)frozen.Index : null;
        var visuals = ClientTables.TableOrEmpty("BuffVisuals").Where(r => r.Text("Id")?.StartsWith("crucible_stasis", StringComparison.Ordinal) == true)
            .ToDictionary(r => r.Text("Id")!, r => (ushort)r.Index);
        StasisVisuals = [.. new[] { "", "_magic", "_rare", "_unique" }
            .Select(suffix => visuals.TryGetValue("crucible_stasis" + suffix, out ushort row) ? row : (ushort?)null)];

        var shared = ObjectTypes.SharedStates();
        var objects = new Dictionary<string, ObjectTypeInfo>(StringComparer.Ordinal);
        foreach (var (type, row) in ObjectTypes.All())
        {
            if (type.StartsWith("Metadata/Terrain/Leagues/Crucible/Objects/", StringComparison.Ordinal)
                || type.StartsWith("Metadata/MiscellaneousObjects/Crucible/", StringComparison.Ordinal))
            {
                int size = int.TryParse(row.Text("positioned_object_size"), out int s) ? s : 1;
                bool blocking = ObjectTypes.Flag(row, "positioned_blocking") == true;
                bool hasCollision = row.BoolOrNull("positioned") == true;
                objects[type] = new(ObjectTypes.Components(row), ObjectTypes.StateMachine(row, shared), size, blocking, hasCollision);
            }
        }
        // Fallback descriptors for CrucibleDevice and CrucibleCombiningBench when absent from client.db object_types
        // (verified from their .ot files in data/game-files.zip; canonical component read order matches client.db).
        objects.TryAdd(DeviceType, new(
            ["Positioned", "BaseEvents", "InteractionAction", "Animated", "TriggerableBlockage", "MinimapIcon", "StateMachine", "ControlZone"],
            new StateMachineData([0, 0, 0], 0, ["activated", "channelling", "in_zone"]),
            4,
            true));
        objects.TryAdd(CombiningBenchType, new(
            ["Positioned", "BaseEvents", "Animated", "InteractionAction", "Transitionable", "MinimapIcon"],
            null,
            5,
            false));
        Objects = objects;
    }

    /// <summary>XP bars for an item level (clamped to the table's 1..100).</summary>
    public LevelBalance BalanceFor(int itemLevel) => _balance[Math.Clamp(itemLevel, 1, _balance.Length - 1)]!;

    /// <summary>CrucibleLifeScalingPerLevel MoreLife percentage for an area level.</summary>
    public int MoreLife(int areaLevel) =>
        _moreLife.Length == 0 ? 0 : _moreLife[Math.Clamp(areaLevel, 0, _moreLife.Length - 1)];

    /// <summary>What two item classes must share to be combined: the <c>ItemClasses</c> row's ItemClassCategory, or the
    /// class itself (negative) when it has none (<c>0x1424bb850</c>).</summary>
    public int ClassCategory(int classRow) => _classCategory.GetValueOrDefault(classRow) ?? -1 - classRow;

    /// <summary>Whether this area id is Forge of the Titans (normal or uber).</summary>
    public static bool IsForgeArea(string areaId) =>
        areaId.Equals(ForgeAreaId, StringComparison.Ordinal) || areaId.Equals(ForgeUberAreaId, StringComparison.Ordinal);

    /// <summary>Whether this area id is Uber Forge of the Titans.</summary>
    public static bool IsUberForgeArea(string areaId) =>
        areaId.Equals(ForgeUberAreaId, StringComparison.Ordinal);
}
