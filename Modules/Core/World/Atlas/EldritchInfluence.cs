using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using static Program;

namespace GameServer.Modules.Atlas;

/// <summary>The two Eldritch Primordial Bosses from <c>AtlasPrimordialBosses</c>.</summary>
public enum PrimordialBoss : byte
{
    None = 0,
    Tangle = 1,        // Eater of Worlds (Boss=0, bossOption=2, area stat 0x2a03 = 1)
    CleansingFire = 2, // Searing Exarch (Boss=1, bossOption=3, area stat 0x2a03 = 2)
}

/// <summary>
/// Eldritch Influence (Eater of Worlds / Tangle &amp; Searing Exarch / Cleansing Fire):
/// influenced monster pack spawning (<c>MonsterPacks 2836..2851</c>), full story progression
/// (<c>AtlasPrimordialBossInfluence</c> T9–T16, quest invitations, Infinite Hunger / Black Star / Eater / Exarch
/// kills, Voidstone Slot 2 <c>LQF 708</c>), repeatable 28-map (50 HardMode) invitation track, and
/// <see cref="KiracProgress"/> synchronization with <see cref="AtlasProgress"/>.
/// </summary>
public static class EldritchInfluence
{
    // AtlasPrimordialBosses metadata (rows 0 = Tangle, 1 = CleansingFire).
    public sealed record BossConfig(
        PrimordialBoss Boss,
        int TableRow,
        string Id,
        byte BossOption,
        int UnlockLeagueFlag,
        int StoryCompleteLeagueFlag,
        int MiniBossInvitationRow,
        int BossInvitationRow,
        int PickUpKeyQuestFlagRow,
        int QuestMiniBossKeyItemRow,
        int QuestBossKeyItemRow,
        int AltarTagRow,
        int OppositeTagRow,
        int AltarMiscObjectRow,
        string AltarMetadataPath);

    // One row of AtlasPrimordialBossInfluence (18 rows: 9 per boss, Progress 0..8).
    public sealed record InfluenceStep(
        PrimordialBoss Boss,
        int Progress,
        int MinMapTier,
        int NextLeagueFlag,
        int RequiredLeagueFlag,
        int? QuestItemRow,
        int? DefeatBossQuestFlagRow);

    // Eldritch pack definition loaded from MonsterPacks + MonsterPackEntries.
    public sealed record EldritchPack(
        int Row,
        string Id,
        PrimordialBoss Boss,
        int MinCount,
        int MaxCount,
        IReadOnlyList<(int VarietyRow, int Weight)> Entries,
        IReadOnlyList<int> BossVarieties);

    // Per-instance live Eldritch state (shared across all players in the map instance).
    public sealed class InstanceContext
    {
        public PrimordialBoss Boss { get; init; }
        public int MapTier { get; init; }
        public bool HardMode { get; init; }
        public readonly object Gate = new();
        public readonly ConcurrentDictionary<uint, int> EldritchMonsters = new(); // monsterId -> packIndex
        public readonly ConcurrentDictionary<int, int> PackRemaining = new();     // packIndex -> remaining alive
        public readonly Dictionary<uint, EldritchAltars.SpawnedAltar> Altars = new();
        public readonly List<EldritchAltars.RolledMod> PlayerUpsides = [];
        public readonly List<EldritchAltars.RolledMod> PlayerDownsides = [];
        public readonly List<EldritchAltars.RolledMod> MinionUpsides = [];
        public readonly List<EldritchAltars.RolledMod> MinionDownsides = [];
        public readonly List<EldritchAltars.RolledMod> BossUpsides = [];
        public readonly List<EldritchAltars.RolledMod> BossDownsides = [];
        public readonly Dictionary<uint, int> ActivePlayerStats = [];
        public readonly Dictionary<uint, int> ActivePlayerDropStats = [];
        public readonly Dictionary<uint, int> ActiveMinionCombatStats = [];
        public readonly Dictionary<uint, int> ActiveMinionDropStats = [];
        public readonly Dictionary<uint, int> ActiveBossCombatStats = [];
        public readonly Dictionary<uint, int> ActiveBossDropStats = [];
        // Immutable snapshots published under Gate on altar activation so hot paths (ApplyDrops) allocate nothing per kill.
        public volatile IReadOnlyDictionary<uint, int>? PlayerDropSnapshot;
        public volatile IReadOnlyDictionary<uint, int>? MinionDropSnapshot;
        public volatile IReadOnlyDictionary<uint, int>? BossDropSnapshot;
        public int AltarsSpawned;
        public int AltarsActivated;
        public int EldritchKills;
        public bool MapBossDead;
        public bool StoryStepAdvanced;
        public bool TrackerStepAdvanced;
        public bool QuestInvitationDropped;
    }

    // Quest / LeagueQuestFlag constants verified against client.db & act11-protocol-spec.md.
    public const int LqfTangleUnlocked = 351;
    public const int LqfCleansingFireUnlocked = 352;
    public const int LqfTangleMiniBossDefeated = 207;
    public const int LqfTangleBossDefeated = 212;
    public const int LqfTangleWatchstone = 221;
    public const int LqfCleansingFireMiniBossDefeated = 323;
    public const int LqfCleansingFireBossDefeated = 328;
    public const int LqfCleansingFireWatchstone = 222;
    public const int LqfEldritchVoidstoneSlot2 = 708; // PrimordialWatchstoneUnlockedExarchEater (requires BOTH 221 & 222)
    const int UnusedLeagueFlagSentinel = 1911;

    public const string TangleTrackerKey = "atlas.tangleProgress";
    public const string CleansingTrackerKey = "atlas.cleansingProgress";

    // Arena WorldAreas Ids & Boss MonsterVarieties Ids (verified in client.db).
    public const string TangleMiniBossArea = "MapWorldsPrimordialBoss1";        // Seething Chyme
    public const string CleansingMiniBossArea = "MapWorldsPrimordialBoss2";     // Polaric Void
    public const string CleansingBossArea = "MapWorldsPrimordialBoss3";         // Absence of Patience and Wisdom
    public const string TangleBossArea = "MapWorldsPrimordialBoss4";            // Absence of Symmetry and Harmony

    public const string InfiniteHungerVariety = "Metadata/Monsters/AtlasInvaders/DoomMonsters/DoomBoss_";
    public const string BlackStarVariety = "Metadata/Monsters/AtlasInvaders/BlackStarMonsters/BlackStarBoss";
    public const string EaterOfWorldsVariety = "Metadata/Monsters/AtlasInvaders/ConsumeMonsters/ConsumeBoss";
    public const string SearingExarchVariety = "Metadata/Monsters/AtlasInvaders/CleansingMonsters/CleansingBoss";

    // BaseItemTypes Ids for quest and tradeable invitations (4673..4680).
    public const string QuestWrithingInvitationId = "Metadata/Items/MapFragments/Primordial/QuestTangleKey";
    public const string TradeWrithingInvitationId = "Metadata/Items/MapFragments/Primordial/CurrencyTangleKey";
    public const string QuestScreamingInvitationId = "Metadata/Items/MapFragments/Primordial/QuestTangleBossKey";
    public const string TradeScreamingInvitationId = "Metadata/Items/MapFragments/Primordial/CurrencyTangleBossKey";
    public const string QuestPolaricInvitationId = "Metadata/Items/MapFragments/Primordial/QuestCleansingFireKey";
    public const string TradePolaricInvitationId = "Metadata/Items/MapFragments/Primordial/CurrencyCleansingFireKey";
    public const string QuestIncandescentInvitationId = "Metadata/Items/MapFragments/Primordial/QuestCleansingFireBossKey";
    public const string TradeIncandescentInvitationId = "Metadata/Items/MapFragments/Primordial/CurrencyCleansingFireBossKey";

    // QuestFlag names (verified in client.db).
    public const string QfTangleDefeatMapBoss = "TangleDefeatMapBoss";                // 1996
    public const string QfCleansingDefeatMapBoss = "CleansingFireDefeatMapBoss";      // 1997
    public const string QfPickUpTangleKey = "PickUpTangleKey";                        // 957
    public const string QfPickUpCleansingKey = "PickUpCleansingFireKey";              // 958
    public const string QfHaveTangleKey1 = "HaveTangleKey1";                          // 953
    public const string QfHaveTangleKey2 = "HaveTangleKey2";                          // 954
    public const string QfHaveCleansingKey1 = "HaveCleansingFireKey1";                // 955
    public const string QfHaveCleansingKey2 = "HaveCleansingFireKey2";                // 956
    public const string QfTanglePortalsOpen = "TanglePortalsOpen";                    // 986
    public const string QfCleansingPortalsOpen = "CleansingFirePortalsOpen";          // 987
    public const string QfInTangleArena = "InTangleArena";                            // 1900
    public const string QfInCleansingArena = "InCleansingFireArena";                  // 1901

    static readonly string[] TangleArenaFlags = [QfInTangleArena];
    static readonly string[] CleansingArenaFlags = [QfInCleansingArena];
    static readonly string[] TanglePortalFlags = [QfTanglePortalsOpen];
    static readonly string[] CleansingPortalFlags = [QfCleansingPortalsOpen];
    static readonly string[] TangleArenaAndPortalFlags = [QfInTangleArena, QfTanglePortalsOpen];
    static readonly string[] CleansingArenaAndPortalFlags = [QfInCleansingArena, QfCleansingPortalsOpen];
    static readonly string[] NoAreaFlags = [];

    static readonly ConditionalWeakTable<InstanceState, InstanceContext> Instances = new();

    static Dictionary<PrimordialBoss, BossConfig> _bosses = [];
    static Dictionary<PrimordialBoss, IReadOnlyList<InfluenceStep>> _steps = [];
    static Dictionary<PrimordialBoss, IReadOnlyList<EldritchPack>> _packs = [];
    static Dictionary<PrimordialBoss, HashSet<string>> _varietyIds = [];
    static Dictionary<int, string> _questFlagNames = [];
    static Dictionary<int, string> _baseItemIds = [];

    public static IReadOnlyDictionary<PrimordialBoss, BossConfig> Bosses => _bosses;
    public static IReadOnlyDictionary<PrimordialBoss, IReadOnlyList<InfluenceStep>> Steps => _steps;
    public static IReadOnlyDictionary<PrimordialBoss, IReadOnlyList<EldritchPack>> Packs => _packs;

    public static PrimordialBoss BossFromOption(byte bossOption) => bossOption switch
    {
        2 => PrimordialBoss.Tangle,
        3 => PrimordialBoss.CleansingFire,
        _ => PrimordialBoss.None,
    };

    public static PrimordialBoss BossFromAreaStat(int areaStatValue) => areaStatValue switch
    {
        1 => PrimordialBoss.Tangle,
        2 => PrimordialBoss.CleansingFire,
        _ => PrimordialBoss.None,
    };

    public static InstanceContext? ContextOf(Player player) =>
        player.Instance is { } inst && Instances.TryGetValue(inst, out var ctx) ? ctx : null;

    public static InstanceContext GetOrCreateContext(Player player, PrimordialBoss boss, int mapTier) =>
        Instances.GetValue(player.Instance, _ => new InstanceContext
        {
            Boss = boss,
            MapTier = mapTier,
            HardMode = LeagueFor(player).HardMode,
        });

    public static bool IsEldritchMonster(InstanceContext ctx, uint objectId, string varietyId) =>
        (objectId != 0 && ctx.EldritchMonsters.ContainsKey(objectId))
        || (_varietyIds.TryGetValue(ctx.Boss, out var set) && set.Contains(varietyId));

    /// <summary>Loads <c>AtlasPrimordialBosses</c>, <c>AtlasPrimordialBossInfluence</c>, and Eldritch
    /// <c>MonsterPacks</c> / <c>MonsterPackEntries</c> from <c>client.db</c>.</summary>
    public static void LoadData()
    {
        var qfNames = new Dictionary<int, string>();
        if (ClientTables.Has("QuestFlags"))
        {
            int idx = 0;
            foreach (var r in ClientTables.Table("QuestFlags"))
                qfNames[idx++] = r.Text("Id") ?? "";
        }
        _questFlagNames = qfNames;

        var itemIds = new Dictionary<int, string>();
        if (ClientTables.Has("BaseItemTypes"))
        {
            int idx = 0;
            foreach (var r in ClientTables.Table("BaseItemTypes"))
                itemIds[idx++] = r.Text("Id") ?? "";
        }
        _baseItemIds = itemIds;

        var questItemItemRows = new Dictionary<int, int>();
        if (ClientTables.Has("QuestItems"))
        {
            int idx = 0;
            foreach (var r in ClientTables.Table("QuestItems"))
            {
                if (r.IntOrNull("Item") is int baseItemRow)
                    questItemItemRows[idx] = baseItemRow;
                idx++;
            }
        }

        var miscObjectPaths = new Dictionary<int, string>();
        if (ClientTables.Has("MiscObjects"))
        {
            int idx = 0;
            foreach (var r in ClientTables.Table("MiscObjects"))
                miscObjectPaths[idx++] = r.Has("EffectVirtualPath") ? r.Text("EffectVirtualPath") ?? "" : "";
        }

        var bosses = new Dictionary<PrimordialBoss, BossConfig>();
        if (ClientTables.Has("AtlasPrimordialBosses"))
        {
            int idx = 0;
            foreach (var r in ClientTables.Table("AtlasPrimordialBosses"))
            {
                int row = idx++;
                var boss = row switch
                {
                    0 => PrimordialBoss.Tangle,
                    1 => PrimordialBoss.CleansingFire,
                    _ => PrimordialBoss.None,
                };
                if (boss == PrimordialBoss.None) continue;
                int altarMiscRow = r.IntOrNull("Altar") ?? (boss == PrimordialBoss.Tangle ? 615 : 616);
                string altarPath = miscObjectPaths.GetValueOrDefault(altarMiscRow,
                    boss == PrimordialBoss.Tangle ? PrimordialAltarObject.TanglePath : PrimordialAltarObject.CleansingFirePath);
                if (altarPath.Length == 0)
                    altarPath = boss == PrimordialBoss.Tangle ? PrimordialAltarObject.TanglePath : PrimordialAltarObject.CleansingFirePath;
                int minorKeyQi = r.IntOrNull("MinorBossKey") ?? -1;
                int bossKeyQi = r.IntOrNull("BossQuestKey") ?? -1;
                bosses[boss] = new BossConfig(
                    Boss: boss,
                    TableRow: row,
                    Id: r.Text("Id") ?? boss.ToString(),
                    BossOption: (byte)(r.IntOrNull("__col1") ?? (boss == PrimordialBoss.Tangle ? 2 : 3)),
                    UnlockLeagueFlag: r.IntOrNull("__col2") ?? (boss == PrimordialBoss.Tangle ? LqfTangleUnlocked : LqfCleansingFireUnlocked),
                    StoryCompleteLeagueFlag: r.IntOrNull("__col3") ?? (boss == PrimordialBoss.Tangle ? LqfTangleBossDefeated : LqfCleansingFireBossDefeated),
                    MiniBossInvitationRow: r.IntOrNull("MiniBossInvitation") ?? (boss == PrimordialBoss.Tangle ? 4674 : 4678),
                    BossInvitationRow: r.IntOrNull("BossInvitation") ?? (boss == PrimordialBoss.Tangle ? 4676 : 4680),
                    PickUpKeyQuestFlagRow: r.IntOrNull("PickUpKey") ?? (boss == PrimordialBoss.Tangle ? 957 : 958),
                    QuestMiniBossKeyItemRow: questItemItemRows.GetValueOrDefault(minorKeyQi, boss == PrimordialBoss.Tangle ? 4673 : 4677),
                    QuestBossKeyItemRow: questItemItemRows.GetValueOrDefault(bossKeyQi, boss == PrimordialBoss.Tangle ? 4675 : 4679),
                    AltarTagRow: r.IntOrNull("Tag") ?? (boss == PrimordialBoss.Tangle ? 962 : 961),
                    OppositeTagRow: r.IntOrNull("OppositeTag") ?? (boss == PrimordialBoss.Tangle ? 961 : 962),
                    AltarMiscObjectRow: altarMiscRow,
                    AltarMetadataPath: altarPath);
            }
        }
        _bosses = bosses;

        var stepsByBoss = new Dictionary<PrimordialBoss, List<InfluenceStep>>
        {
            [PrimordialBoss.Tangle] = [],
            [PrimordialBoss.CleansingFire] = [],
        };
        if (ClientTables.Has("AtlasPrimordialBossInfluence"))
        {
            foreach (var r in ClientTables.Table("AtlasPrimordialBossInfluence"))
            {
                var boss = r.IntOrNull("Boss") switch
                {
                    0 => PrimordialBoss.Tangle,
                    1 => PrimordialBoss.CleansingFire,
                    _ => PrimordialBoss.None,
                };
                if (boss == PrimordialBoss.None) continue;
                int? qiRow = r.IntOrNull("QuestItem");
                int? itemRow = qiRow is int q && questItemItemRows.TryGetValue(q, out int br) ? br : null;
                stepsByBoss[boss].Add(new InfluenceStep(
                    Boss: boss,
                    Progress: r.IntOrNull("Progress") ?? 0,
                    MinMapTier: r.IntOrNull("MinMapTier") ?? 9,
                    NextLeagueFlag: r.IntOrNull("__col3") ?? UnusedLeagueFlagSentinel,
                    RequiredLeagueFlag: r.IntOrNull("__col4") ?? 0,
                    QuestItemRow: itemRow,
                    DefeatBossQuestFlagRow: r.IntOrNull("QuestFlag")));
            }
        }
        _steps = stepsByBoss.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<InfluenceStep>)[.. kv.Value.OrderBy(s => s.Progress)]);

        // Load Eldritch-influenced MonsterPacks (atlasprimordialinfluence-*) & their MonsterPackEntries.
        var packsByBoss = new Dictionary<PrimordialBoss, List<EldritchPack>>
        {
            [PrimordialBoss.Tangle] = [],
            [PrimordialBoss.CleansingFire] = [],
        };
        var varietyIdsByBoss = new Dictionary<PrimordialBoss, HashSet<string>>
        {
            [PrimordialBoss.Tangle] = new(StringComparer.Ordinal),
            [PrimordialBoss.CleansingFire] = new(StringComparer.Ordinal),
        };
        if (ClientTables.Has("MonsterPacks") && ClientTables.Has("MonsterPackEntries"))
        {
            var packEntries = new Dictionary<int, List<(int VarietyRow, int Weight)>>();
            foreach (var e in ClientTables.Table("MonsterPackEntries"))
            {
                if (e.IntOrNull("MonsterPacksKey") is not int packKey
                    || e.IntOrNull("MonsterVarietiesKey") is not int varKey) continue;
                int weight = Math.Max(1, e.IntOrNull("Weight") ?? 100);
                if (!packEntries.TryGetValue(packKey, out var list))
                    packEntries[packKey] = list = [];
                list.Add((varKey, weight));
            }
            var varietyTable = ClientTables.Has("MonsterVarieties") ? ClientTables.Table("MonsterVarieties") : null;
            int pIdx = 0;
            foreach (var p in ClientTables.Table("MonsterPacks"))
            {
                int row = pIdx++;
                string id = p.Text("Id") ?? "";
                if (!id.StartsWith("atlasprimordialinfluence-", StringComparison.OrdinalIgnoreCase)) continue;
                PrimordialBoss boss = id.Contains("tangle", StringComparison.OrdinalIgnoreCase)
                    || id.Contains("doom", StringComparison.OrdinalIgnoreCase)
                    ? PrimordialBoss.Tangle
                    : id.Contains("cleansingfire", StringComparison.OrdinalIgnoreCase)
                        || id.Contains("blackstar", StringComparison.OrdinalIgnoreCase)
                        ? PrimordialBoss.CleansingFire
                        : PrimordialBoss.None;
                if (boss == PrimordialBoss.None) continue;
                var entries = packEntries.GetValueOrDefault(row) ?? [];
                if (entries.Count == 0) continue;
                int minCount = Math.Max(1, p.IntOrNull("MinCount") ?? 3);
                int maxCount = Math.Max(minCount, p.IntOrNull("MaxCount") ?? 6);
                int[] bossVars = [.. p.Ints("BossMonster_MonsterVarietiesKeys")];
                packsByBoss[boss].Add(new EldritchPack(row, id, boss, minCount, maxCount, entries, bossVars));
                if (varietyTable is not null)
                {
                    foreach (var (vRow, _) in entries)
                        if (vRow >= 0 && vRow < varietyTable.Count && varietyTable[vRow].Text("Id") is { Length: > 0 } vid)
                            varietyIdsByBoss[boss].Add(vid);
                    foreach (int bRow in bossVars)
                        if (bRow >= 0 && bRow < varietyTable.Count && varietyTable[bRow].Text("Id") is { Length: > 0 } vid)
                            varietyIdsByBoss[boss].Add(vid);
                }
            }
        }
        _packs = packsByBoss.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<EldritchPack>)kv.Value);
        _varietyIds = varietyIdsByBoss;
    }

    // ---- Transient Area Quest Flags ----

    /// <summary>Transient <c>GameEvents.AreaQuestFlags</c>: grants <c>InTangleArena</c> (1900) / <c>InCleansingFireArena</c>
    /// (1901) inside the Primordial boss arenas and <c>TanglePortalsOpen</c> (986) / <c>CleansingFirePortalsOpen</c> (987)
    /// whenever the character has live portals open to those arenas (zero per-call allocation).</summary>
    public static IReadOnlyList<string> AreaQuestFlags(Player player)
    {
        string areaId = player.Layout.Id;
        bool inTangle = areaId is TangleMiniBossArea or TangleBossArea;
        bool inCleansing = !inTangle && areaId is CleansingMiniBossArea or CleansingBossArea;
        var open = MapDevice.OpenOf(CharacterName(player));
        bool tangleOpen = open is { PortalsLeft: > 0, Area.Id: TangleMiniBossArea or TangleBossArea };
        bool cleansingOpen = open is { PortalsLeft: > 0, Area.Id: CleansingMiniBossArea or CleansingBossArea };
        if (inTangle) return tangleOpen ? TangleArenaAndPortalFlags : TangleArenaFlags;
        if (inCleansing) return cleansingOpen ? CleansingArenaAndPortalFlags : CleansingArenaFlags;
        if (tangleOpen) return TanglePortalFlags;
        if (cleansingOpen) return CleansingPortalFlags;
        return NoAreaFlags;
    }

    // ---- Eldritch Pack Spawning (AreaPopulated) ----

    /// <summary>Spawns Eldritch-influenced monster packs into an eligible Tangle or Cleansing Fire Atlas map.</summary>
    public static async Task OnAreaPopulated(AreaPopulated area)
    {
        var player = area.Player;
        if (global::Game.MapRunOf(player) is not { Atlas: true } run) return;
        var boss = BossFromOption(run.BossOption);
        if (boss == PrimordialBoss.None) return;
        if (!global::Game.IsBossOptionEligible(run.BossOption, run.Area.Tier, run.Mods.MapRarity)) return;

        var ctx = GetOrCreateContext(player, boss, run.Area.Tier);
        if (!_packs.TryGetValue(boss, out var packPool) || packPool.Count == 0) return;

        var layout = player.Layout;
        if (layout.Walkable is not { } walkable) return;

        var mods = global::Game.MapModsOf(player);
        int influencePackSizePct = mods?.Get(boss == PrimordialBoss.Tangle
            ? Stat.MapTangleInfluencePackSizePlusPct
            : Stat.MapCleansingFireInfluencePackSizePlusPct) ?? 0;
        if (influencePackSizePct == 0) influencePackSizePct = mods?.Get(Stat.MapInfluencePackSizePlusPct) ?? 0;
        double packSizeFactor = (mods?.PackSizeFactor ?? 1.0) * Math.Max(0.25, 1.0 + influencePackSizePct / 100.0);
        int extraBossChancePct = mods?.Get(boss == PrimordialBoss.Tangle
            ? Stat.MapTangleInfluencedPacksAdditionalTentacleMassChancePct
            : Stat.MapCleansingFireInfluencedPacksAdditionalFlameWraithChancePct) ?? 0;

        // Place ~12 Eldritch packs spread across walkable cells away from spawn.
        const int basePackCount = 12;
        int packCount = Math.Clamp((int)Math.Round(basePackCount * Math.Min(2.0, Math.Max(0.75, packSizeFactor))), 6, 20);
        var rng = new Random(HashCode.Combine(area.Seed, (int)boss, 0x454C4452));
        var centers = PickPackCenters(layout, walkable, area.Taken, packCount, rng);
        byte monsterLevel = (byte)Math.Clamp(area.Level > 0 ? area.Level : (run.Area.Level > 0 ? run.Area.Level : 68), 1, 100);

        int packIndex = 0;
        int totalSpawned = 0;
        foreach (var center in centers)
        {
            packIndex++;
            var pack = packPool[rng.Next(packPool.Count)];
            int rawCount = rng.Next(pack.MinCount, pack.MaxCount + 1);
            int memberCount = Math.Clamp((int)Math.Round(rawCount * packSizeFactor), 2, 16);
            var rarity = rng.Next(100) < 25 ? MonsterRarity.Magic : MonsterRarity.Normal;
            int spawnedInPack = 0;

            for (int m = 0; m < memberCount; m++)
            {
                int varRow = PickWeightedVariety(pack.Entries, rng);
                var variety = global::Game.Monsters.Find(varRow.ToString());
                if (variety is not { Supported: true }) continue;
                var cell = layout.Near((center.X + rng.Next(-4, 5), center.Y + rng.Next(-4, 5)));
                if (!walkable(cell.X, cell.Y)) continue;
                uint id = await global::Game.SpawnMonster(player, variety, monsterLevel, rarity, cell.X, cell.Y,
                    alone: false, instanceOwned: true, bulk: true, log: false);
                if (id != 0)
                {
                    ctx.EldritchMonsters[id] = packIndex;
                    spawnedInPack++;
                    totalSpawned++;
                }
            }

            // Optional rare pack champion (Tentacle Mass / Flame Wraith) from BossMonster_MonsterVarietiesKeys.
            if (pack.BossVarieties.Count > 0 && rng.Next(100) < (15 + extraBossChancePct))
            {
                int bossRow = pack.BossVarieties[rng.Next(pack.BossVarieties.Count)];
                var bossVariety = global::Game.Monsters.Find(bossRow.ToString());
                if (bossVariety is { Supported: true })
                {
                    var cell = layout.Near((center.X, center.Y));
                    if (walkable(cell.X, cell.Y))
                    {
                        uint id = await global::Game.SpawnMonster(player, bossVariety, monsterLevel, MonsterRarity.Rare,
                            cell.X, cell.Y, alone: false, instanceOwned: true, bulk: true, log: false);
                        if (id != 0)
                        {
                            ctx.EldritchMonsters[id] = packIndex;
                            spawnedInPack++;
                            totalSpawned++;
                        }
                    }
                }
            }

            if (spawnedInPack > 0)
                ctx.PackRemaining[packIndex] = spawnedInPack;
        }

        if (totalSpawned > 0)
            Console.WriteLine($"[atlas] {player.Layout.Id}: spawned {totalSpawned} {boss} Eldritch monster(s) across {ctx.PackRemaining.Count} pack(s)");
    }

    static List<(int X, int Y)> PickPackCenters(AreaLayout layout, Func<int, int, bool> walkable,
        IReadOnlyList<(int X, int Y)> taken, int count, Random rng)
    {
        var centers = new List<(int X, int Y)>(count);
        int w = layout.GridW, h = layout.GridH;
        if (w <= 0 || h <= 0) return centers;
        int attempts = count * 40;
        for (int a = 0; a < attempts && centers.Count < count; a++)
        {
            int x = rng.Next(4, Math.Max(5, w - 4));
            int y = rng.Next(4, Math.Max(5, h - 4));
            if (!walkable(x, y)) continue;
            long dxSpawn = x - layout.SpawnX, dySpawn = y - layout.SpawnY;
            if (dxSpawn * dxSpawn + dySpawn * dySpawn < 20 * 20) continue;
            bool tooClose = false;
            foreach (var c in centers)
            {
                long dx = x - c.X, dy = y - c.Y;
                if (dx * dx + dy * dy < 18 * 18) { tooClose = true; break; }
            }
            if (!tooClose) centers.Add((x, y));
        }
        // Fallback near existing pack spots if map geometry is sparse.
        if (centers.Count == 0 && taken.Count > 0)
        {
            int step = Math.Max(1, taken.Count / Math.Max(1, count));
            for (int i = 0; i < taken.Count && centers.Count < count; i += step)
            {
                var cell = layout.Near((taken[i].X + 3, taken[i].Y + 3));
                if (walkable(cell.X, cell.Y)) centers.Add(cell);
            }
        }
        return centers;
    }

    static int PickWeightedVariety(IReadOnlyList<(int VarietyRow, int Weight)> entries, Random rng)
    {
        if (entries.Count == 1) return entries[0].VarietyRow;
        int total = 0;
        for (int i = 0; i < entries.Count; i++) total += entries[i].Weight;
        int roll = rng.Next(Math.Max(1, total));
        for (int i = 0; i < entries.Count; i++)
        {
            roll -= entries[i].Weight;
            if (roll < 0) return entries[i].VarietyRow;
        }
        return entries[^1].VarietyRow;
    }

    // ---- Player Entry: Set Story Defeat-Boss Quest Flag if on Invitation Step ----

    public static async Task OnPlayerEntered(Player player)
    {
        // Clean up PickUp*Key flag if the player is already holding the key.
        await ReconcileCarriedInvitationFlags(player);

        if (global::Game.MapRunOf(player) is not { Atlas: true } run) return;
        var boss = BossFromOption(run.BossOption);
        if (boss == PrimordialBoss.None) return;
        if (!global::Game.IsBossOptionEligible(run.BossOption, run.Area.Tier, run.Mods.MapRarity)) return;

        var ctx = GetOrCreateContext(player, boss, run.Area.Tier);
        // Replay any already-spawned Eldritch Altars in this instance for re-entering or late-joining players.
        await EldritchAltars.ReplayAltarsToPlayer(player, ctx);

        // If on a story invitation step (Progress 2 = T11+ or Progress 7 = T16), set TangleDefeatMapBoss / CleansingFireDefeatMapBoss.
        var step = CurrentStoryStep(player, boss);
        if (step is { DefeatBossQuestFlagRow: int qfRow } && run.Area.Tier >= step.MinMapTier
            && _questFlagNames.TryGetValue(qfRow, out string? qfName) && qfName.Length > 0
            && !ClientHasQuestFlag(player, qfName))
        {
            await SetQuestFlags(player, [qfName], on: true);
        }
    }

    static async Task ReconcileCarriedInvitationFlags(Player player)
    {
        var items = ItemsOf(player);
        var catalog = ItemsFor(player);
        bool hasTangleKey1 = false, hasTangleKey2 = false, hasCleansingKey1 = false, hasCleansingKey2 = false;
        foreach (var (_, entry) in QuestItems.Carried(items))
        {
            switch (catalog.ByHash(entry.Item.TypeHash)?.Id)
            {
                case QuestWrithingInvitationId: hasTangleKey1 = true; break;
                case QuestScreamingInvitationId: hasTangleKey2 = true; break;
                case QuestPolaricInvitationId: hasCleansingKey1 = true; break;
                case QuestIncandescentInvitationId: hasCleansingKey2 = true; break;
            }
        }
        var clear = new List<string>();
        if ((hasTangleKey1 || hasTangleKey2) && player.Character.HasQuestFlag(QfPickUpTangleKey)) clear.Add(QfPickUpTangleKey);
        if ((hasCleansingKey1 || hasCleansingKey2) && player.Character.HasQuestFlag(QfPickUpCleansingKey)) clear.Add(QfPickUpCleansingKey);
        if (!hasTangleKey1 && player.Character.HasQuestFlag(QfHaveTangleKey1)) clear.Add(QfHaveTangleKey1);
        if (!hasTangleKey2 && player.Character.HasQuestFlag(QfHaveTangleKey2)) clear.Add(QfHaveTangleKey2);
        if (!hasCleansingKey1 && player.Character.HasQuestFlag(QfHaveCleansingKey1)) clear.Add(QfHaveCleansingKey1);
        if (!hasCleansingKey2 && player.Character.HasQuestFlag(QfHaveCleansingKey2)) clear.Add(QfHaveCleansingKey2);
        if (clear.Count > 0) await SetQuestFlags(player, clear, on: false);
    }

    // ---- Story Progression & Repeatable 28-Map Tracker ----

    /// <summary>Resolves the player's current <see cref="InfluenceStep"/> (<c>Progress 0..8</c>) from
    /// <c>AtlasPrimordialBossInfluence</c> and <c>Player.Character.LeagueQuestFlags</c>, or null if not yet unlocked.</summary>
    public static InfluenceStep? CurrentStoryStep(Player player, PrimordialBoss boss)
    {
        if (!_steps.TryGetValue(boss, out var steps) || steps.Count == 0) return null;
        var flags = player.Character.LeagueQuestFlags;
        // Walk from highest step downward: the highest step whose RequiredLeagueFlag is set is the active stage.
        for (int i = steps.Count - 1; i >= 0; i--)
        {
            var step = steps[i];
            if (step.RequiredLeagueFlag == 0 || flags.Contains(step.RequiredLeagueFlag))
            {
                // For non-boss story steps (0, 1, 3..6), if NextLeagueFlag is already set, move on.
                if (step.Progress < 8 && step.NextLeagueFlag != UnusedLeagueFlagSentinel && flags.Contains(step.NextLeagueFlag))
                    continue;
                return step;
            }
        }
        return null;
    }

    /// <summary>Advances Eldritch story progression on an Eldritch monster kill (non-boss story steps 0, 1, 3..6).</summary>
    public static async Task OnEldritchMonsterKilled(Player player, InstanceContext ctx, MonsterInfo monster)
    {
        if (ctx.StoryStepAdvanced) return;
        if (global::Game.MapRunOf(player) is not { Atlas: true } run) return;
        await TryAdvanceStoryNonBossStep(player, ctx, run.Area.Tier);
    }

    /// <summary>Handles Eldritch story progression, quest invitation drops, and repeatable 28-map tracker advancement
    /// when a map boss is killed in an Eldritch-influenced map.</summary>
    public static async Task OnMapBossKilled(Player player, MonsterInfo monster, IReadOnlyList<Player> peers)
    {
        if (global::Game.MapRunOf(player) is not { Atlas: true } run) return;
        var boss = BossFromOption(run.BossOption);
        if (boss == PrimordialBoss.None) return;
        if (!global::Game.IsBossOptionEligible(run.BossOption, run.Area.Tier, run.Mods.MapRarity)) return;

        var ctx = GetOrCreateContext(player, boss, run.Area.Tier);
        ctx.MapBossDead = true;
        int tier = run.Area.Tier;
        var at = (monster.X, monster.Y);

        // 1. Story non-boss step (if not already advanced earlier by an Eldritch pack kill).
        if (!ctx.StoryStepAdvanced)
            await TryAdvanceStoryNonBossStep(player, ctx, tier, peers);

        // 2. Story boss invitation step (Progress 2 T11+ -> Writhing/Polaric; Progress 7 T16 -> Screaming/Incandescent).
        if (!ctx.QuestInvitationDropped)
        {
            var step = CurrentStoryStep(player, boss);
            if (step is { QuestItemRow: int questItemRow } && tier >= step.MinMapTier
                && _baseItemIds.TryGetValue(questItemRow, out string? itemId) && itemId.Length > 0)
            {
                ctx.QuestInvitationDropped = true;
                string pickUpFlag = boss == PrimordialBoss.Tangle ? QfPickUpTangleKey : QfPickUpCleansingKey;
                string defeatFlag = boss == PrimordialBoss.Tangle ? QfTangleDefeatMapBoss : QfCleansingDefeatMapBoss;
                string haveFlag = (boss, step.Progress) switch
                {
                    (PrimordialBoss.Tangle, 2) => QfHaveTangleKey1,
                    (PrimordialBoss.Tangle, _) => QfHaveTangleKey2,
                    (PrimordialBoss.CleansingFire, 2) => QfHaveCleansingKey1,
                    _ => QfHaveCleansingKey2,
                };
                foreach (var peer in peers)
                {
                    if (CurrentStoryStep(peer, boss)?.Progress != step.Progress) continue;
                    if (ClientHasQuestFlag(peer, haveFlag)) continue;
                    await global::Game.DropQuestItem(peer, itemId, at, monster.Name);
                    if (ClientHasQuestFlag(peer, defeatFlag))
                        await SetQuestFlags(peer, [defeatFlag], on: false);
                    await SetQuestFlags(peer, [pickUpFlag], on: true);
                }
                return;
            }
        }

        // 3. Repeatable 28-map track (Progress 8: LQF 212 Tangle / LQF 328 Cleansing Fire already set, T14+).
        if (!ctx.TrackerStepAdvanced)
        {
            await TryAdvanceRepeatableTrack(player, ctx, boss, tier, monster, peers);
        }
    }

    static async Task TryAdvanceStoryNonBossStep(Player player, InstanceContext ctx, int tier, IReadOnlyList<Player>? peers = null)
    {
        var step = CurrentStoryStep(player, ctx.Boss);
        if (step is null || step.Progress >= 8 || step.QuestItemRow is not null
            || step.NextLeagueFlag == UnusedLeagueFlagSentinel || tier < step.MinMapTier)
            return;

        bool claimed;
        lock (ctx.Gate)
        {
            if (ctx.StoryStepAdvanced) return;
            ctx.StoryStepAdvanced = true;
            claimed = true;
        }
        if (!claimed) return;

        foreach (var peer in peers ?? [player])
        {
            var peerStep = CurrentStoryStep(peer, ctx.Boss);
            if (peerStep is null || peerStep.Progress != step.Progress || peerStep.NextLeagueFlag == UnusedLeagueFlagSentinel)
                continue;
            await SetLeagueQuestFlags(peer, [peerStep.NextLeagueFlag], on: true);
            await AtlasFeature.Refresh(peer);
        }
    }

    /// <summary>Advances the post-story 28-map (50 HardMode) repeatable Eldritch tracker, drops tradeable
    /// mini-boss/pinnacle invitations at 14 and 28 (25 and 50 HardMode), resets at cap, and refreshes <c>0x01a3</c>.</summary>
    public static async Task TryAdvanceRepeatableTrack(Player player, InstanceContext ctx, PrimordialBoss boss,
        int tier, MonsterInfo monster, IReadOnlyList<Player> peers, Random? rng = null)
    {
        if (!_bosses.TryGetValue(boss, out var config)) return;
        var repeatStep = _steps.TryGetValue(boss, out var list) ? list.FirstOrDefault(s => s.Progress == 8) : null;
        int minTier = repeatStep?.MinMapTier ?? 14;
        if (tier < minTier) return;

        bool claimed;
        lock (ctx.Gate)
        {
            if (ctx.TrackerStepAdvanced) return;
            ctx.TrackerStepAdvanced = true;
            claimed = true;
        }
        if (!claimed) return;

        rng ??= Random.Shared;
        var areaStats = global::Game.MapModsOf(player);
        int doubleChance = areaStats?.Get(boss == PrimordialBoss.Tangle
            ? Stat.MapTangleDoubleProgressChancePct
            : Stat.MapCleansingFireDoubleProgressChancePct) ?? 0;
        int delta = doubleChance > 0 && rng.Next(100) < doubleChance ? 2 : 1;
        string trackerKey = boss == PrimordialBoss.Tangle ? TangleTrackerKey : CleansingTrackerKey;
        int maxSteps = ctx.HardMode ? 50 : 28;
        int miniStep = maxSteps / 2; // 14 (25 HardMode)

        string miniInvId = _baseItemIds.GetValueOrDefault(config.MiniBossInvitationRow,
            boss == PrimordialBoss.Tangle ? TradeWrithingInvitationId : TradePolaricInvitationId);
        string bossInvId = _baseItemIds.GetValueOrDefault(config.BossInvitationRow,
            boss == PrimordialBoss.Tangle ? TradeScreamingInvitationId : TradeIncandescentInvitationId);

        foreach (var peer in peers)
        {
            if (!peer.Character.LeagueQuestFlags.Contains(config.StoryCompleteLeagueFlag)) continue;
            var data = WorldOf(PlayerOf(peer)).CharacterData;
            int before = 0, after = 0;
            bool dropMini = false, dropBoss = false;
            Saves.Atomically(() =>
            {
                int cur = int.TryParse(data.Get(trackerKey), out int parsed) ? Math.Clamp(parsed, 0, maxSteps) : 0;
                before = cur;
                int next = cur + delta;
                if (cur < miniStep && next >= miniStep)
                {
                    dropMini = true;
                    next = miniStep; // never skip past the mini-boss invitation milestone
                }
                else if (next >= maxSteps)
                {
                    dropBoss = true;
                    next = 0;
                }
                after = next;
                data.Change(trackerKey, _ => next.ToString());
            });
            if (dropMini && miniInvId.Length > 0)
                await global::Game.DropGroundItem(peer, miniInvId, (monster.X, monster.Y), monster.Name);
            if (dropBoss && bossInvId.Length > 0)
                await global::Game.DropGroundItem(peer, bossInvId, (monster.X, monster.Y), monster.Name);
            await AtlasFeature.Refresh(peer);
        }
    }

    /// <summary>Handles kills of The Infinite Hunger, The Black Star, The Eater of Worlds, and The Searing Exarch:
    /// sets completion LQFs (<c>207</c>, <c>323</c>, <c>212+221</c>, <c>328+222</c>), clears transient arena/portal
    /// quest flags, and unlocks Voidstone Slot 2 (<c>LQF 708</c>) once BOTH <c>221</c> and <c>222</c> are earned.</summary>
    public static async Task OnPrimordialBossKilled(Player player, string varietyId, IReadOnlyList<Player> peers)
    {
        if (varietyId is not (InfiniteHungerVariety or BlackStarVariety or EaterOfWorldsVariety or SearingExarchVariety))
            return;

        foreach (var peer in peers)
        {
            var setLqfs = new List<int>();
            var clearQfs = new List<string>();
            switch (varietyId)
            {
                case InfiniteHungerVariety:
                    if (!peer.Character.LeagueQuestFlags.Contains(LqfTangleMiniBossDefeated))
                        setLqfs.Add(LqfTangleMiniBossDefeated);
                    clearQfs.AddRange([QfTanglePortalsOpen, QfPickUpTangleKey, QfHaveTangleKey1, QfTangleDefeatMapBoss]);
                    break;
                case BlackStarVariety:
                    if (!peer.Character.LeagueQuestFlags.Contains(LqfCleansingFireMiniBossDefeated))
                        setLqfs.Add(LqfCleansingFireMiniBossDefeated);
                    clearQfs.AddRange([QfCleansingPortalsOpen, QfPickUpCleansingKey, QfHaveCleansingKey1, QfCleansingDefeatMapBoss]);
                    break;
                case EaterOfWorldsVariety:
                    if (!peer.Character.LeagueQuestFlags.Contains(LqfTangleBossDefeated))
                        setLqfs.Add(LqfTangleBossDefeated);
                    if (!peer.Character.LeagueQuestFlags.Contains(LqfTangleWatchstone))
                        setLqfs.Add(LqfTangleWatchstone);
                    if (peer.Character.LeagueQuestFlags.Contains(LqfCleansingFireWatchstone)
                        && !peer.Character.LeagueQuestFlags.Contains(LqfEldritchVoidstoneSlot2))
                        setLqfs.Add(LqfEldritchVoidstoneSlot2);
                    clearQfs.AddRange([QfTanglePortalsOpen, QfPickUpTangleKey, QfHaveTangleKey2, QfTangleDefeatMapBoss]);
                    break;
                case SearingExarchVariety:
                    if (!peer.Character.LeagueQuestFlags.Contains(LqfCleansingFireBossDefeated))
                        setLqfs.Add(LqfCleansingFireBossDefeated);
                    if (!peer.Character.LeagueQuestFlags.Contains(LqfCleansingFireWatchstone))
                        setLqfs.Add(LqfCleansingFireWatchstone);
                    if (peer.Character.LeagueQuestFlags.Contains(LqfTangleWatchstone)
                        && !peer.Character.LeagueQuestFlags.Contains(LqfEldritchVoidstoneSlot2))
                        setLqfs.Add(LqfEldritchVoidstoneSlot2);
                    clearQfs.AddRange([QfCleansingPortalsOpen, QfPickUpCleansingKey, QfHaveCleansingKey2, QfCleansingDefeatMapBoss]);
                    break;
            }
            if (setLqfs.Count > 0)
                await SetLeagueQuestFlags(peer, setLqfs, on: true);
            var activeClear = clearQfs.Where(f => peer.Character.HasQuestFlag(f)).ToList();
            if (activeClear.Count > 0)
                await SetQuestFlags(peer, activeClear, on: false);
            if (setLqfs.Count > 0)
                await AtlasFeature.Refresh(peer);
        }
    }

    // ---- Kirac Shop Progression Sync ----

    /// <summary>Synchronizes <see cref="KiracProgress"/> with <see cref="AtlasProgress.Completed"/> so Kirac's
    /// map vendor tier (<c>KiracLevels</c>) and gold reroll cost stay connected to Atlas map completions across
    /// boss kills, minion kills, <c>/atlas_complete</c>, and login restores.</summary>
    public static int SyncKiracProgress(Player player, ISavedData data, int? completedTier = null)
    {
        var stash = PersonalStashOf(player);
        var atlasCompleted = AtlasProgress.Completed(data);
        if (atlasCompleted.Count == 0) return 0;
        var kiracCompleted = KiracProgress.Completed(stash);
        int synced = 0;
        int fallbackTier = Math.Clamp(completedTier ?? 1, 1, 16);
        foreach (string nodeId in atlasCompleted)
        {
            if (kiracCompleted.Contains(nodeId)) continue;
            var node = AtlasProgress.Find(nodeId);
            string areaKey = node?.Areas.FirstOrDefault() ?? nodeId;
            if (kiracCompleted.Contains(areaKey)) continue;
            if (KiracProgress.Complete(stash, areaKey, fallbackTier))
            {
                kiracCompleted.Add(areaKey);
                synced++;
            }
        }
        return synced;
    }
}
