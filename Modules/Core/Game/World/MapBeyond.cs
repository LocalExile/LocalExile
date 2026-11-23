using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;
using static Game;
using static Program;

/// <summary>
/// Beyond (CoreLeagues row 4, 3.29 Scourge-rework rules): slaying non-Beyond enemies in an active Beyond area has a
/// chance to spawn a small blood orb (BeyondPortal1). Four orbs within the merging radius collapse into a Beyond portal
/// (BeyondPortal2) that spawns a pack from the area's rolled Beyond faction (Demon, Flesh or Pale). After enough packs
/// spawn, merges gain a stacking chance to open a boss portal (BeyondPortal3) spawning the faction's boss (K'Tash,
/// Ghorr or Beidat — or all three with the Beyond Scarab of Resurgence), shutting down further portals in that area
/// (unless Endless Tide prevents bosses from spawning).
/// </summary>
public static partial class MapBeyond
{
    public const string Content = "Beyond";
    const string SaveKey = "Beyond/state";
    const int LeagueRow = 4;
    const uint Salt = 0x42455944; // "BEYD"

    /// <summary>HellscapeLifeScalingPerLevel more-life stat, handed over to MonsterModEffects in GameModule.Load.</summary>
    public static readonly uint LifeStat = Stat.Of("beyond_monster_life_+%_final");

    // 3.19–3.29 Beyond spawn/merge parameters (poewiki "Beyond": 15% base orb chance, 4 orbs merge; 1 screen ~50 cells).
    public const double BasePortalChance = 15.0;
    public const double BaseMergeRadius = 45.0;
    public const double MinMergeRadius = 10.0;
    public const int MergeThreshold = 4;
    // shortcut: server estimate for boss portal ramp — poewiki only states a stacking chance after "a substantial
    // number" of packs; GGG does not publish exact threshold or per-pack step values.
    public const int BossMinPacks = 6;
    public const double BossChanceStep = 8.0;
    const int MaxActiveOrbs = 60;
    const int InvasionScatter = 10;

    // Portal animation timing (seconds).
    const double OrbExitSeconds = 0.8;
    const double PackPortalOpenSeconds = 0.4;
    const double PackPortalCloseSeconds = 1.6;
    const double PackPortalRemoveSeconds = 2.4;
    const double BossPortalOpenSeconds = 0.5;
    const double BossPortalCloseSeconds = 2.0;
    const double BossPortalRemoveSeconds = 3.0;

    public enum PortalKind { Small = 1, Pack = 2, Boss = 3 }

    public sealed class Portal
    {
        public uint Id { get; set; }
        public PortalKind Kind { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public byte Stage { get; set; } = 1;
        public bool Consumed { get; set; }
        public bool Spawned { get; set; }
        public double Age { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public bool Published;
    }

    public sealed class State
    {
        public bool Active { get; set; }
        public bool InvasionOnly { get; set; }
        public int FactionRow { get; set; } = -1;
        public int PacksSpawned { get; set; }
        public bool BossSpawned { get; set; }
        public List<Portal> Portals { get; set; } = [];
    }

    sealed record KillEvent(Player Killer, MonsterRarity Rarity, (int X, int Y) At, int ChanceFinal);

    sealed class Area
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public State? State;
        public readonly ConcurrentQueue<KillEvent> Kills = new();
    }

    static readonly Store<InstanceState, Area> Areas = new();
    static readonly Store<Player, Dictionary<uint, byte>> Presented = new();
    static readonly ConcurrentDictionary<(string Area, int Level), AreaInfo?> AreaInfos = new();

    // Non-core / supporter / Mirage stats not acted on; reported once on map opening if present.
    static readonly uint[] Unsupported =
    [
        Stat.MapBeyondDemonDesecrateOnSpawnDamagePerSecond,
        Stat.MapBeyondBossEnrage,
        Stat.MapBeyondFaridunRecreateEnabled,
    ];

    static IEnumerable<Player> Members(Player player) => player.Instance.PlayersExcept(player.Id)
        .Prepend(player)
        .Where(c => !c.Closed && !c.LeavingArea && c.Instance == player.Instance).Distinct();

    static byte AreaLevel(Player player) =>
        (byte)Math.Clamp(MapRunOf(player) is { Area.Level: > 0 } map ? map.Area.Level : ServerLevels.AreaLevel(player.Layout.Id), 1, 100);

    static int AreaStat(Player player, uint id)
    {
        if (MapModsOf(player) is { } mods) return mods.Get(id);
        if (player.Instance?.KeptAreaStats is { Count: > 0 } kept)
            return kept.GetValueOrDefault(id);
        return 0;
    }

    public static bool IsBoss(MonsterCatalog.Variety? variety) => BeyondCatalog.IsBoss(variety);
    public static StateMachineData? StartStates(MonsterCatalog.Variety variety) => BeyondCatalog.StartStates(variety);

    public static void Opening(MapOpening request)
    {
        if (request.Opening is not { } opening || BeyondCatalog.All.Count == 0) return;
        request.HandledContent.Add(Content);
        // Scarab of the Invasion (ScarabBeyond5) rolls 8..12 portals per rare/unique kill.
        MapScarabPacks.RollFragmentCount(request, Stat.MapRareMonstersSpawnXBeyondPortals, Random.Shared);
        MapScarabPacks.RollFragmentCount(request, Stat.MapUniqueMonstersSpawnXBeyondPortals, Random.Shared);
        var present = Unsupported.Where(s => opening.Mods.Get(s) != 0).Select(Program.Stats.Name).ToArray();
        if (present.Length > 0)
            request.ContentWarnings.Add(("Beyond.Parity",
                $"area={opening.Area.Id} content=Beyond: enabled; not acted on: {string.Join(", ", present)}"));
    }

    /// <summary>Merging radius in grid cells: 45 × (1 + increased%) × (1 + final%), minimum 10 cells.
    /// Endless Tide keystone applies -50% final radius (the Idol already carries its own final stat).</summary>
    public static double MergeRadius(Player player) => MergeRadius(
        AreaStat(player, Stat.MapBeyondPortalSearchRadiusPlusPct),
        AreaStat(player, Stat.MapBeyondPortalSearchRadiusPlusPctFinal),
        AreaStat(player, Stat.AtlasKeystoneEndlessTide) > 0);

    public static double MergeRadius(int increasedPct, int finalPct, bool endlessTide)
    {
        int final = finalPct + (endlessTide ? -50 : 0);
        double radius = BaseMergeRadius
            * Math.Max(0, 100 + increasedPct) / 100.0
            * Math.Max(0, 100 + final) / 100.0;
        return Math.Max(MinMergeRadius, radius);
    }

    /// <summary>Portal spawn chance on a non-Beyond kill (percent, 0..100).</summary>
    public static double PortalChance(int increasedPct) =>
        Math.Clamp(BasePortalChance * Math.Max(0, 100 + increasedPct) / 100.0, 0, 100);

    /// <summary>Chance for the next merged portal to spawn a Beyond boss instead of a pack (percent, 0..100).</summary>
    public static double BossSpawnChance(int packsSpawned, int increasedPct)
    {
        if (packsSpawned < BossMinPacks) return 0;
        double baseChance = (packsSpawned - (BossMinPacks - 1)) * BossChanceStep;
        return Math.Clamp(baseChance * Math.Max(0, 100 + increasedPct) / 100.0, 0, 100);
    }

    static bool RollLeague(Player player, byte level)
    {
        var info = AreaInfos.GetOrAdd((player.Layout.Id, level), k =>
        {
            try { return AreaInfo.Load(GamePack.Default.TryRead, k.Area, k.Level); }
            catch (Exception e) when (e is ArgumentException or FileNotFoundException) { return null; }
        });
        if (info is not null)
        {
            var stats = player.Instance.KeptAreaStats?.ToDictionary(s => (int)s.Key, s => s.Value) ?? [];
            return ExtraTerrainFeatures.LeagueCount(info with { Stats = stats }, new LevelRandom(player.Seed ^ Salt, false), LeagueRow) > 0;
        }
        int chance = Math.Clamp(AreaStat(player, Stat.MapBeyondRulesChancePct) + AreaStat(player, Stat.MapLeagueContentAdditionalChancePct), 0, 100);
        return chance > 0 && (chance >= 100 || new Random(unchecked((int)(player.Seed ^ Salt))).Next(100) < chance);
    }

    static Area? Of(Player player)
    {
        if (Areas.TryGetValue(player.Instance, out var area)) return area;
        if (!player.Instance.ContentState.TryGetValue(SaveKey, out string? saved)) return null;
        area = Areas.GetValue(player.Instance, _ => new());
        if (area.State is null) Load(area, saved);
        return area;
    }

    static void Load(Area area, string saved)
    {
        if (area.State is not null) return;
        area.State = JsonSerializer.Deserialize<State>(saved) ?? new();
        foreach (var p in area.State.Portals) p.Published = true;
    }

    static void Keep(Player player, Area area) =>
        player.Instance.ContentState[SaveKey] = JsonSerializer.Serialize(area.State);

    public static async Task Enter(Player player)
    {
        if (BeyondCatalog.All.Count == 0 || player.Layout.Walkable is null) return;
        await RestoreBossBuffs(player);
        bool hasMap = MapRunOf(player) is not null;
        bool forced = AreaStat(player, Stat.MapBeyondRules) > 0;
        bool invasion = AreaStat(player, Stat.MapRareMonstersSpawnXBeyondPortals) > 0
            || AreaStat(player, Stat.MapUniqueMonstersSpawnXBeyondPortals) > 0;
        // Fast path outside maps when no Beyond stat or saved state is present.
        if (!hasMap && !forced && !invasion && !player.Instance.ContentState.ContainsKey(SaveKey)) return;

        var area = Areas.GetValue(player.Instance, _ => new());
        await area.Gate.WaitAsync();
        try
        {
            if (area.State is null)
            {
                if (player.Instance.ContentState.TryGetValue(SaveKey, out string? saved)) Load(area, saved);
                else
                {
                    byte level = AreaLevel(player);
                    bool rolled = !forced && hasMap && RollLeague(player, level);
                    bool active = forced || rolled || invasion;
                    int factionRow = -1;
                    if (active)
                    {
                        var rng = new Random(unchecked((int)(player.Seed ^ Salt ^ 0x46414354)));
                        var faction = BeyondCatalog.PickFaction(s => AreaStat(player, s), rng);
                        factionRow = faction?.Row ?? 0;
                    }
                    area.State = new State
                    {
                        Active = active,
                        InvasionOnly = !forced && !rolled && invasion,
                        FactionRow = factionRow,
                    };
                    Keep(player, area);
                    if (active)
                        Console.WriteLine($"[map-content] beyond area={player.Layout.Id} level={level}: active faction={BeyondCatalog.ByRow(factionRow)?.Id ?? "?"}"
                            + (area.State.InvasionOnly ? " (invasion-only)" : ""));
                }
            }
            if (area.State is { Active: true }) await Present(player, area);
        }
        finally { area.Gate.Release(); }
    }

    /// <summary>GameEvents.Kill: fast early-out when Beyond is inactive. Handles Haemophilia rare-mod stealing and
    /// Demonic Power boss buffs on Beyond kills; queues non-Beyond kills for portal spawning and merging.</summary>
    public static void Kill(KillContext kill)
    {
        var player = kill.Player;
        if (player.Instance is null) return;

        // Beyond monsters never spawn portals, but still trigger Haemophilia and Demonic Power.
        if (BeyondCatalog.IsBeyondVariety(kill.VarietyId))
        {
            OnBeyondMonsterKilled(kill);
            return;
        }

        if (!Areas.TryGetValue(player.Instance, out var area) || area.State is not { Active: true, BossSpawned: false }
            || BeyondCatalog.PortalChanceFinal(kill.Monster.Variety) is not int final) return;
        area.Kills.Enqueue(new(player, kill.Monster.Rarity, kill.At, final));
    }

    public static async Task Tick(Player player, double seconds)
    {
        if (!player.Closed && !player.LeavingArea) await TickBossBuffs(player);
        if (player.Closed || player.LeavingArea || !player.Connection.PlayerSent || player.Instance is null
            || Of(player) is not { State.Active: true } area) return;

        await area.Gate.WaitAsync();
        try
        {
            if (area.State is not { Active: true } || player.Closed || player.LeavingArea) return;
            bool changed = false;
            if (IsMonsterRunner(player))
                changed = await Step(player, area, Math.Clamp(seconds, 0, 1));
            if (changed)
            {
                Keep(player, area);
                foreach (var member in Members(player)) await Present(member, area);
            }
            else
                await Present(player, area);
        }
        finally { area.Gate.Release(); }
    }

    static async Task<bool> Step(Player player, Area area, double seconds)
    {
        var state = area.State!;
        bool changed = false;

        // 1. Drain queued non-Beyond kills -> spawn blood orbs & merge clusters of 4.
        if (!state.BossSpawned && !area.Kills.IsEmpty)
        {
            double radius = MergeRadius(player);
            double chance = state.InvasionOnly ? 0
                : PortalChance(AreaStat(player, Stat.MapBeyondPortalChancePlusPct) + AreaStat(player, Stat.MapMonsterBeyondPortalChancePlusPct));

            int rareInvasion = AreaStat(player, Stat.MapRareMonstersSpawnXBeyondPortals);
            int uniqueInvasion = AreaStat(player, Stat.MapUniqueMonstersSpawnXBeyondPortals);
            var rng = Random.Shared;

            while (!state.BossSpawned && area.Kills.TryDequeue(out var kill))
            {
                double own = chance * Math.Max(0, 100 + kill.ChanceFinal) / 100.0;
                if (own > 0 && (own >= 100 || rng.NextDouble() * 100.0 < own))
                {
                    AddOrbAndMerge(player, state, kill.At, radius, rng);
                    changed = true;
                }
                int extra = kill.Rarity switch
                {
                    MonsterRarity.Rare => rareInvasion,
                    MonsterRarity.Unique => uniqueInvasion,
                    _ => 0,
                };
                for (int i = 0; i < extra && !state.BossSpawned; i++)
                {
                    var scatter = (kill.At.X + rng.Next(-InvasionScatter, InvasionScatter + 1),
                        kill.At.Y + rng.Next(-InvasionScatter, InvasionScatter + 1));
                    AddOrbAndMerge(player, state, scatter, radius, rng);
                    changed = true;
                }
            }
            // Discard any kills left in the queue once a boss has spawned.
            if (state.BossSpawned)
                while (area.Kills.TryDequeue(out _)) { }
        }
        else if (state.BossSpawned)
            while (area.Kills.TryDequeue(out _)) { }

        // 2. Advance active portals: open -> spawn monsters -> close -> remove.
        for (int i = state.Portals.Count - 1; i >= 0; i--)
        {
            var p = state.Portals[i];
            if (p.Kind == PortalKind.Small)
            {
                if (!p.Consumed) continue;
                if (!p.Published) { state.Portals.RemoveAt(i); changed = true; continue; }
                p.Age += seconds;
                if (p.Age >= OrbExitSeconds) { state.Portals.RemoveAt(i); changed = true; }
                continue;
            }

            p.Age += seconds;
            double openAt = p.Kind == PortalKind.Boss ? BossPortalOpenSeconds : PackPortalOpenSeconds;
            double closeAt = p.Kind == PortalKind.Boss ? BossPortalCloseSeconds : PackPortalCloseSeconds;
            double removeAt = p.Kind == PortalKind.Boss ? BossPortalRemoveSeconds : PackPortalRemoveSeconds;

            if (!p.Spawned && p.Age >= openAt)
            {
                p.Stage = 2;
                p.Spawned = true;
                changed = true;
                if (p.Kind == PortalKind.Boss) await SpawnBosses(player, state, p);
                else await SpawnPack(player, state, p);
            }
            if (p.Stage < 3 && p.Age >= closeAt)
            {
                p.Stage = 3;
                p.Consumed = true;
                changed = true;
            }
            if (p.Age >= removeAt)
            {
                state.Portals.RemoveAt(i);
                changed = true;
            }
        }
        return changed;
    }

    static void AddOrbAndMerge(Player player, State state, (int X, int Y) at, double radius, Random rng)
    {
        var cell = player.Layout.Near(at);
        var orb = new Portal
        {
            Id = player.Instance.World.NewMonsterId(),
            Kind = PortalKind.Small,
            X = cell.X,
            Y = cell.Y,
            Stage = 2, // mini_idle blood orb on ground
        };
        state.Portals.Add(orb);

        // Trim oldest idle orbs if the area accumulates too many without merging.
        int idleCount = state.Portals.Count(p => p.Kind == PortalKind.Small && !p.Consumed);
        if (idleCount > MaxActiveOrbs)
        {
            var oldest = state.Portals.First(p => p.Kind == PortalKind.Small && !p.Consumed && !ReferenceEquals(p, orb));
            oldest.Consumed = true;
            oldest.Stage = 4;
            oldest.Age = 0;
        }

        // Check for a cluster of 4 unconsumed small orbs within merge radius of the new orb.
        double radiusSq = radius * radius;
        var near = state.Portals
            .Where(p => p.Kind == PortalKind.Small && !p.Consumed)
            .Select(p => (Portal: p, DistSq: (double)(p.X - orb.X) * (p.X - orb.X) + (double)(p.Y - orb.Y) * (p.Y - orb.Y)))
            .Where(e => e.DistSq <= radiusSq)
            .OrderBy(e => e.DistSq)
            .Take(MergeThreshold)
            .ToArray();
        if (near.Length < MergeThreshold) return;

        int cx = (int)Math.Round(near.Average(e => (double)e.Portal.X));
        int cy = (int)Math.Round(near.Average(e => (double)e.Portal.Y));
        var center = player.Layout.Near((cx, cy));
        foreach (var (p, _) in near)
        {
            p.Consumed = true;
            p.Stage = 4; // exit collapse
            p.Age = 0;
        }

        bool canSpawnBoss = !state.BossSpawned
            && AreaStat(player, Stat.MapBeyondBossesCannotSpawn) <= 0
            && AreaStat(player, Stat.AtlasKeystoneEndlessTide) <= 0;
        double bossChance = canSpawnBoss ? BossSpawnChance(state.PacksSpawned, AreaStat(player, Stat.MapBeyondBossSpawnChancePlusPct)) : 0;
        bool isBoss = bossChance > 0 && (bossChance >= 100 || rng.NextDouble() * 100.0 < bossChance);

        if (isBoss)
        {
            state.BossSpawned = true;
            // Shut down all remaining unmerged orbs in the area (3.29 rule).
            foreach (var p in state.Portals)
                if (p.Kind == PortalKind.Small && !p.Consumed)
                {
                    p.Consumed = true;
                    p.Stage = 4;
                    p.Age = 0;
                }
            var portal = new Portal
            {
                Id = player.Instance.World.NewMonsterId(),
                Kind = PortalKind.Boss,
                X = center.X,
                Y = center.Y,
                Stage = 1,
            };
            state.Portals.Add(portal);
            Console.WriteLine($"[beyond] boss portal 0x{portal.Id:x} opening at ({center.X}, {center.Y}) after {state.PacksSpawned} packs");
        }
        else
        {
            state.PacksSpawned++;
            var portal = new Portal
            {
                Id = player.Instance.World.NewMonsterId(),
                Kind = PortalKind.Pack,
                X = center.X,
                Y = center.Y,
                Stage = 1,
            };
            state.Portals.Add(portal);
            Console.WriteLine($"[beyond] pack portal #{state.PacksSpawned} 0x{portal.Id:x} opening at ({center.X}, {center.Y})");
        }
    }

    static async Task SpawnPack(Player player, State state, Portal portal)
    {
        var faction = BeyondCatalog.ByRow(state.FactionRow) ?? BeyondCatalog.All.FirstOrDefault();
        if (faction is null) return;
        var rng = Random.Shared;
        bool useElite = faction.ElitePacks.Count > 0 && (faction.FodderPacks.Count == 0 || rng.Next(2) == 0);
        var pool = useElite ? faction.ElitePacks : faction.FodderPacks;
        if (pool.Count == 0) return;
        var pack = pool[rng.Next(pool.Count)];

        byte level = AreaLevel(player);
        double packFactor = Math.Max(0, 100 + AreaStat(player, Stat.MapBeyondDemonPackSizePlusPct)) / 100.0;
        int rawCount = rng.Next(Math.Max(1, pack.MinCount), Math.Max(pack.MinCount, pack.MaxCount) + 1);
        int count = Math.Max(1, (int)Math.Round(rawCount * packFactor));
        var extraMods = LifeExtraMods(level);

        // Spawn elite leader (if this is an elite pack) as Rare (60%) or Magic (40%).
        if (pack.BossVarieties.Count > 0)
        {
            int leaderRow = pack.BossVarieties[rng.Next(pack.BossVarieties.Count)];
            if (Monsters.Find(leaderRow.ToString()) is { Supported: true } leader)
            {
                var cell = player.Layout.Near((portal.X + rng.Next(-4, 5), portal.Y + rng.Next(-4, 5)));
                var leaderRarity = rng.Next(100) < 60 ? MonsterRarity.Rare : MonsterRarity.Magic;
                await SpawnMonster(player, leader, level, leaderRarity, cell.X, cell.Y,
                    extraMods: extraMods, unrisen: false);
            }
        }

        // Pack rarity for the followers.
        int roll = rng.Next(100);
        MonsterRarity followerRarity = pack.BossVarieties.Count > 0
            ? (roll < 25 ? MonsterRarity.Magic : MonsterRarity.Normal)
            : (roll < 30 ? MonsterRarity.Magic : MonsterRarity.Normal);
        bool rareLeader = pack.BossVarieties.Count == 0 && roll >= 90;

        var entries = pack.Entries.Where(e => Monsters.Find(e.Variety.ToString()) is { Supported: true }).ToArray();
        if (entries.Length == 0) return;
        int totalWeight = entries.Sum(e => Math.Max(1, e.Weight));

        for (int i = 0; i < count; i++)
        {
            int pick = rng.Next(totalWeight);
            var entry = entries[^1];
            foreach (var e in entries) if ((pick -= Math.Max(1, e.Weight)) < 0) { entry = e; break; }
            var variety = Monsters.Find(entry.Variety.ToString())!;
            var cell = player.Layout.Near((portal.X + rng.Next(-7, 8), portal.Y + rng.Next(-7, 8)));
            var rarity = rareLeader && i == 0 ? MonsterRarity.Rare : followerRarity;
            await SpawnMonster(player, variety, level, rarity, cell.X, cell.Y,
                extraMods: extraMods, unrisen: false);
        }
    }

    static async Task SpawnBosses(Player player, State state, Portal portal)
    {
        bool three = AreaStat(player, Stat.MapBeyondSpawnThreeBossesInsteadOfOne) > 0;
        var factions = three
            ? BeyondCatalog.All
            : BeyondCatalog.ByRow(state.FactionRow) is { } single ? [single] : BeyondCatalog.All.Take(1).ToArray();
        byte level = AreaLevel(player);
        var extraMods = LifeExtraMods(level);

        for (int i = 0; i < factions.Count; i++)
        {
            var faction = factions[i];
            if (Monsters.Find(faction.BossVariety) is not { Supported: true } variety) continue;
            var offset = factions.Count == 1 ? (portal.X, portal.Y)
                : (portal.X + (int)Math.Round(6 * Math.Cos(Math.Tau * i / factions.Count)),
                   portal.Y + (int)Math.Round(6 * Math.Sin(Math.Tau * i / factions.Count)));
            var cell = player.Layout.Near(offset);
            uint id = await SpawnMonster(player, variety, level, MonsterRarity.Unique, cell.X, cell.Y,
                extraMods: extraMods, alone: true, unrisen: false);
            if (id != 0)
                Console.WriteLine($"[beyond] spawned boss {variety.Name} ({variety.Id}) 0x{id:x} at ({cell.X}, {cell.Y})");
        }
    }

    static MonsterModSet LifeExtraMods(byte level) =>
        new([], [], new MonsterModEffects(new Dictionary<uint, int> { [LifeStat] = BeyondCatalog.LifePct(level) }));

    static string PortalTypePath(PortalKind kind) => kind switch
    {
        PortalKind.Small => BeyondCatalog.SmallPortalType,
        PortalKind.Pack => BeyondCatalog.PackPortalType,
        PortalKind.Boss => BeyondCatalog.BossPortalType,
        _ => BeyondCatalog.SmallPortalType,
    };

    static async Task Present(Player player, Area area)
    {
        if (player.Closed || player.LeavingArea || area.State is not { } state) return;
        var shown = Presented.GetValue(player, _ => []);
        if (state.Portals.Count == 0 && shown.Count == 0) return;

        // Remove portals that are no longer in state.Portals.
        var live = new HashSet<uint>(state.Portals.Select(p => p.Id));
        foreach (uint old in shown.Keys.Where(id => !live.Contains(id)).ToArray())
        {
            shown.Remove(old);
            await SendWorldRemoval(player, old, $"[beyond] portal 0x{old:x} removed");
        }

        // Create new portals and transition any whose stage changed.
        foreach (var p in state.Portals)
        {
            string path = PortalTypePath(p.Kind);
            if (BeyondCatalog.Portal(path) is not { } spec) continue;
            if (!shown.TryGetValue(p.Id, out byte sent))
            {
                var obj = new QuestObject(p.X, p.Y, spec.Components, TransitionState: p.Stage) { StateMachine = spec.StateMachine };
                await SendWorldCreate(player, p.Id,
                    InstanceEntities.CreateObject(p.Id, InstanceEntities.ObjectTypeHash(path), obj.Blob()),
                    $"beyond portal {p.Kind} 0x{p.Id:x} stage={p.Stage} at ({p.X}, {p.Y})");
                await SendWorldRecord(player, p.Id, InstanceProtocol.EnterWakeZone(p.Id), null);
                shown[p.Id] = p.Stage;
                p.Published = true;
            }
            else if (sent != p.Stage)
            {
                await SendWorldRecord(player, p.Id,
                    InstanceEntities.TransitionableUpdate(p.Id, spec.TransitionableIndex, p.Stage), null);
                shown[p.Id] = p.Stage;
            }
        }
    }

    public static void Describe(Player player, JsonObject state)
    {
        if (player.Instance is null || !Areas.TryGetValue(player.Instance, out var area) || area.State is not { Active: true } beyond) return;
        state["beyond"] = new JsonObject
        {
            ["faction"] = BeyondCatalog.ByRow(beyond.FactionRow)?.Id,
            ["packsSpawned"] = beyond.PacksSpawned,
            ["bossSpawned"] = beyond.BossSpawned,
            ["portals"] = beyond.Portals.Count,
        };
    }

    public static void Leave(Player player)
    {
        Presented.Remove(player);
        BossBuffs.Remove(player);
    }
}
