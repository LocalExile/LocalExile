using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modding;

namespace GameServer.Modules.Delirium;

/// <summary>
/// Map Delirium Mirror encounter (<c>Rulesets</c> row 102 <c>AfflictionEncounterRules</c> via S→C <c>0x1d</c> / <c>0x2c9</c>
/// and skip/end button C→S <c>0x260</c>). State belongs to <see cref="InstanceState"/> (per-<see cref="GameArea"/>),
/// safe across re-entry, party join, and module hot-reload.
/// </summary>
public sealed class DeliriumMirrorEncounter
{
    public const string MirrorPath = "Metadata/MiscellaneousObjects/Affliction/AfflictionInitiator";
    const string StateKey = "Delirium.Mirror";
    const int ActivationRadius = 12;
    const float WaveStart = -1.8f;
    const float OutOfFogGraceSeconds = 5f;
    const int MaxActiveMonsters = 28;
    const int MaxSpawnTotal = 160;
    const double SpawnIntervalSeconds = 2.5;
    static int MaxRewardTiles => MirrorRewardPool.Length;

    static readonly string[] FallbackMirrorComponents = ["Positioned", "BaseEvents", "Functions", "Animated", "MinimapIcon", "StateMachine"];
    internal static readonly int[] MirrorRewardPool = [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 13, 14, 15, 17, 22, 23, 24, 25];
    static readonly string[] DeliriumMonsterIds =
    [
        "AfflictionMinion1", "AfflictionMinion2", "AfflictionMinion3",
        "AfflictionMinion4", "AfflictionMinion5_", "AfflictionMinion6_",
    ];
    static readonly string[] BossVarieties = [SimulacrumRules.Omniphobia, SimulacrumRules.Kosis];

    readonly ConditionalWeakTable<InstanceState, MirrorState> _states = new();
    readonly JsonSerializerOptions _json = new() { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };
    IModuleContext _context = null!;

    public enum MirrorStage { None = 0, Ready = 1, Active = 2, Ended = 3 }

    public sealed class MirrorState
    {
        public MirrorStage Stage { get; set; }
        public uint EntityId { get; set; }
        public int MirrorX { get; set; }
        public int MirrorY { get; set; }
        public byte Level { get; set; }
        public float OuterElapsed { get; set; }
        public float OutOfFogSeconds { get; set; }
        public int LastTimerSeconds { get; set; }
        public bool FogPaused { get; set; }
        public float PauseTimer { get; set; }
        public int DepthPercent { get; set; }
        public int Kills { get; set; }
        public int KillPoints { get; set; }
        public int Splinters { get; set; }
        public int Spawned { get; set; }
        public double NextSpawnAt { get; set; }
        public double LastTickAt { get; set; }
        public int[] Rewards { get; set; } = [];
        public int[] RewardPoints { get; set; } = [];
        public int[] RewardCounts { get; set; } = [];
        public int InitialRewards { get; set; }
        public HashSet<uint> Monsters { get; set; } = [];
        public HashSet<string> SpawnedBosses { get; set; } = [];
        public Dictionary<uint, string> Bosses { get; set; } = [];
        public HashSet<uint> RewardedBosses { get; set; } = [];
        public HashSet<string> RewardedBossVarieties { get; set; } = [];
        public string Why { get; set; } = "";

        [System.Text.Json.Serialization.JsonIgnore] public readonly SemaphoreSlim Gate = new(1, 1);
        [System.Text.Json.Serialization.JsonIgnore] public object? ShownArea;
        [System.Text.Json.Serialization.JsonIgnore] public byte[]? LastTiles;
        [System.Text.Json.Serialization.JsonIgnore] public bool TilesDirty;
        [System.Text.Json.Serialization.JsonIgnore] public bool Spawning;

        public void EnsureRewardArrays()
        {
            if (RewardPoints.Length != Rewards.Length) RewardPoints = new int[Rewards.Length];
            if (RewardCounts.Length != Rewards.Length) RewardCounts = new int[Rewards.Length];
        }

        [System.Text.Json.Serialization.JsonIgnore] public AfflictionRuleset.FogRegion[]? Regions { get; set; }

        [System.Text.Json.Serialization.JsonIgnore] public MirrorFogWave Wave { get; set; }

        public bool IsCellInFog(int x, int y)
        {
            if (Stage != MirrorStage.Active) return false;
            float dx = x - MirrorX, dy = y - MirrorY;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            float outer = Wave.OuterRadius(OuterElapsed), inner = Wave.InnerRadius(OuterElapsed);
            return outer > inner && dist <= outer && dist >= inner;
        }

        public int DepthAt(int x, int y)
        {
            if (Stage != MirrorStage.Active) return 0;
            float dx = x - MirrorX, dy = y - MirrorY;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            return Math.Clamp((int)MathF.Round(dist / 3.2f) + (int)(OuterElapsed * 0.5f), 5, 100);
        }

        public int GoalForTile(int index)
        {
            int completed = index < RewardCounts.Length ? RewardCounts[index] : 0;
            return Math.Max(12, 18 + completed * 8);
        }

        public bool AddReward(Random rng)
        {
            var pool = MirrorRewardPool.Where(p => !Rewards.Contains(p)).ToArray();
            if (pool.Length == 0) return false;
            Rewards = [.. Rewards, pool[rng.Next(pool.Length)]];
            RewardPoints = [.. RewardPoints, 0];
            RewardCounts = [.. RewardCounts, 0];
            InitialRewards = Math.Max(1, InitialRewards) + 1;
            TilesDirty = true;
            return true;
        }

        public void RecordKill(MonsterRarity rarity, int progressBonusPct, int counterBonusPct = 0)
        {
            if (Stage != MirrorStage.Active) return;
            int basePoints = rarity switch
            {
                MonsterRarity.Unique => 14,
                MonsterRarity.Rare => 4,
                MonsterRarity.Magic => 2,
                _ => 1,
            };
            int depthBonus = 100 + DepthPercent / 2 + Math.Max(0, progressBonusPct);
            int points = (int)Math.Clamp(basePoints * (long)depthBonus * Math.Max(0L, 100L + counterBonusPct) / 10000, 1, int.MaxValue / 100);
            Kills++;
            KillPoints += points;
            EnsureRewardArrays();
            int prevTotal = RewardCounts.Sum();
            for (int i = 0; i < Rewards.Length; i++)
            {
                int total = RewardPoints[i] + points;
                int goal = GoalForTile(i);
                while (total >= goal)
                {
                    total -= goal;
                    RewardCounts[i]++;
                    goal = GoalForTile(i);
                }
                RewardPoints[i] = total;
            }
            int nextTotal = RewardCounts.Sum();
            if (nextTotal > prevTotal)
                Splinters += Math.Max(1, (nextTotal - prevTotal) * (1 + DepthPercent / 35));
            else if (rarity is MonsterRarity.Rare or MonsterRarity.Unique)
                Splinters += rarity == MonsterRarity.Unique ? 3 : 1;

            // Unlock an additional reward tile every 2 tiers of the first reward bar (3, 5, 7, 9, ...), matching retail PoE.
            int baseTiles = Math.Clamp(InitialRewards > 0 ? InitialRewards : 1, 1, MaxRewardTiles);
            int firstCount = RewardCounts.Length > 0 ? RewardCounts[0] : 0;
            int tierUnlocks = firstCount >= 3 ? (firstCount - 1) / 2 : 0;
            int desiredTiles = Math.Min(MaxRewardTiles, baseTiles + tierUnlocks);
            if (desiredTiles > Rewards.Length)
            {
                var list = Rewards.ToList();
                var pts = RewardPoints.ToList();
                var cnts = RewardCounts.ToList();
                int seed = HashCode.Combine(MirrorX, MirrorY, Kills, nextTotal);
                while (list.Count < desiredTiles)
                {
                    int pick = MirrorRewardPool[(int)((uint)(seed = unchecked(seed * 31 + 17)) % (uint)MirrorRewardPool.Length)];
                    if (list.Contains(pick)) pick = MirrorRewardPool.First(p => !list.Contains(p));
                    list.Add(pick);
                    pts.Add(0);
                    cnts.Add(0);
                }
                Rewards = [.. list];
                RewardPoints = [.. pts];
                RewardCounts = [.. cnts];
            }
            TilesDirty = true;
        }
    }

    public void Attach(IModuleContext context) => _context = context;

    public void Clear() => _states.Clear();

    public static IReadOnlyList<(ushort Plugin, byte[] State)>? PlanTerrain(AreaStatsRequest request)
    {
        if (AfflictionRuleset.EncounterRow is null || AfflictionRuleset.TerrainPlugin is not { } plugin
            || request.AreaStats is not { } stats || !MapDevices.IsMapArea(request.Area)
            || AreaTransitionCatalog.IdOf(request.Area) is not { } id || SimulacrumRules.IsArena(id)
            || WaypointCatalog.Areas.Find(request.Area) is { IsTown: true } || HideoutCatalog.ByArea(request.Area) is not null
            || LabyrinthTrials.InLabyrinth(id)) return null;

        int S(uint stat) => stats.GetValueOrDefault(stat);
        if (!DeliriumScarabs.Eligible(request.Area, stats) || S(Stat.MapAreaUnderlevelled) > 0) return null;
        if (S(Stat.MapAfflictionLeague) <= 0 && S(Stat.MapSpawnAfflictionMirror) <= 0)
        {
            if (S(Stat.MapDisableDeliriumFromChance) > 0) return null;
            var rng = new Random(AreaPopulation.PopulationSeed(request.Area, request.Seed) ^ 0x44656c69);
            if (LeagueChances.Current.Count(id, LeagueChances.Delirium, rng, alternate: request.HardMode,
                increased: S(Stat.MapExtraContentHasNoBaseChanceToSpawn) > 0 ? -100 : S(Stat.MapLeagueBaseChancePlusPctFinal),
                added: S(Stat.MapLeagueContentAdditionalChancePct) + S(Stat.MapAfflictionEncounterAdditionalChancePct),
                encounterIncrease: S(Stat.MapAfflictionEncounterChancePlusPct)) <= 0) return null;
        }
        return [(plugin, [0])];
    }

    public static bool IsEligibleMap(Player player) => player.Layout.Walkable is not null
        && AfflictionRuleset.TerrainPlugin is { } plugin && player.Instance?.HasTerrainPlugin(plugin) == true;

    public MirrorState For(Player player) => _states.GetValue(player.Instance, instance =>
    {
        var state = instance.ContentState.TryGetValue(StateKey, out var text)
            ? JsonSerializer.Deserialize<MirrorState>(text, _json) ?? new() : new MirrorState();
        // Older in-memory snapshots used transient world ids for the award guard.
        foreach (uint id in state.RewardedBosses)
            if (state.Bosses.TryGetValue(id, out var variety)) state.RewardedBossVarieties.Add(variety);
        if (state.Level == 0)
            state.Level = (byte)Math.Clamp(MapRunOf(player) is { Area.Level: > 0 } map ? map.Area.Level : ServerLevels.AreaLevel(player.Layout.Id), 1, 100);
        state.Wave = MirrorFogWave.For(state.Level, instance.AreaStats(() => AreaLeagueStats(player)));
        return state;
    });

    public MirrorState? Existing(Player player)
    {
        if (player.Instance is not { } instance) return null;
        if (_states.TryGetValue(instance, out var state))
        {
            lock (state) return state.Stage != MirrorStage.None ? state : null;
        }
        return instance.ContentState.ContainsKey(StateKey) ? For(player) : null;
    }

    static bool IsActive(MirrorState state) { lock (state) return state.Stage == MirrorStage.Active; }

    // Must be called while holding lock (state).
    void SaveLocked(Player player, MirrorState state) =>
        player.Instance.ContentState[StateKey] = JsonSerializer.Serialize(state, _json);

    static bool Current(Player player) => player.Connection.PlayerSent && !player.LeavingArea && !player.Closed;

    static IEnumerable<Player> Clients(Player player) =>
        InGame.Keys.Where(c => c.HasClient && ReferenceEquals(c.Instance, player.Instance) && Current(c));

    // ---- ruleset & entry ---------------------------------------------------------------------------------------------

    public IEnumerable<(string Log, byte[] Message)> BeforeEnterMessages(Player player)
    {
        if (!IsEligibleMap(player)) return [];
        var state = For(player);
        lock (state)
        {
            if (state.Stage == MirrorStage.None)
            {
                var stats = player.Instance.AreaStats(() => AreaLeagueStats(player));
                var rng = new Random(AreaPopulation.PopulationSeed(player.Layout.Hash, player.Seed) ^ 0x4166666c);
                var spot = PickMirrorSpot(player.Layout, rng);
                int extra = stats.GetValueOrDefault(Stat.MapAfflictionEncounterRewardTypesPlus);
                if (rng.Next(100) < stats.GetValueOrDefault(Stat.MapDeliriumAdditionalRewardTypeChancePct))
                    extra += 1;
                if (rng.Next(100) < stats.GetValueOrDefault(Stat.MapDelirium3AdditionalRewardTypesChancePct))
                    extra += 3;
                int count = Math.Clamp(1 + Math.Max(0, extra), 1, MaxRewardTiles);
                var rewards = new List<int>(count);
                while (rewards.Count < count)
                {
                    int reward = MirrorRewardPool[rng.Next(MirrorRewardPool.Length)];
                    if (!rewards.Contains(reward)) rewards.Add(reward);
                }
                state.Stage = MirrorStage.Ready;
                state.OuterElapsed = WaveStart;
                state.MirrorX = spot.X;
                state.MirrorY = spot.Y;
                state.InitialRewards = count;
                state.Rewards = [.. rewards];
                state.EnsureRewardArrays();
                state.Why = stats.GetValueOrDefault(Stat.MapAfflictionLeague) > 0 ? "forced by map_affliction_league" : "league roll Delirium";
            }
            if (!ReferenceEquals(state.ShownArea, player.Area))
            {
                var objects = player.Instance.World.SceneObjects;
                if (!objects.TryGetValue(StateKey, out var entity))
                    objects[StateKey] = entity = (player.TakeObjectId(), MirrorPath,
                        new QuestObject(state.MirrorX, state.MirrorY, MirrorComponents()) { Awake = true, StateMachine = MirrorStates(false) });
                state.EntityId = entity.Id;
                state.Regions = null;
                state.ShownArea = player.Area;
            }
            SaveLocked(player, state);
            return BuildSnapshotLocked(player, state) is { } snapshot
                ? [($"0x02c9 ruleset AfflictionEncounterRules mode 0 ({state.Stage} on entry)", snapshot)] : [];
        }
    }

    public async Task OnPlayerEntered(Player player)
    {
        if (!IsEligibleMap(player) || Existing(player) is not { } state) return;
        await state.Gate.WaitAsync();
        try
        {
            MirrorStage stage;
            int timerSeconds;
            lock (state)
            {
                stage = state.Stage;
                timerSeconds = state.LastTimerSeconds;
            }
            if (stage == MirrorStage.Active)
            {
                if (AfflictionRuleset.FogEnvironment(true) is { } fog)
                    await PlayerOf(player).Send(fog, "0x00df restore Delirium fog on entry");
                await WorldOf(player).RefreshStats();
                if (timerSeconds > 0)
                    await PlayerOf(player).Send(InstanceTimers.MissionTimer(InstanceTimers.MissionTimerType.Affliction, (uint)timerSeconds),
                        $"0x0163 Delirium fog timer {timerSeconds}s");
            }
        }
        finally { state.Gate.Release(); }
    }

    // ---- presentation ------------------------------------------------------------------------------------------------

    static (int X, int Y) PickMirrorSpot(AreaLayout layout, Random rng)
    {
        double baseAngle = rng.NextDouble() * Math.PI * 2;
        for (int attempt = 0; attempt < 24; attempt++)
        {
            double a = baseAngle + attempt * (Math.PI / 6);
            int dist = 16 + (attempt % 4) * 5;
            var candidate = layout.Near((layout.SpawnX + (int)Math.Round(Math.Cos(a) * dist),
                layout.SpawnY + (int)Math.Round(Math.Sin(a) * dist)));
            int dx = candidate.X - layout.SpawnX, dy = candidate.Y - layout.SpawnY;
            if (dx * dx + dy * dy >= 64) return candidate;
        }
        return layout.Near((layout.SpawnX + 14, layout.SpawnY + 10));
    }

    static IReadOnlyList<string> MirrorComponents() =>
        QuestObjectTypes.Find(MirrorPath) is { Components.Count: > 0 } info && QuestObject.CanSerialize(info.Components, true)
            ? info.Components
            : FallbackMirrorComponents;

    static StateMachineData MirrorStates(bool interacted)
    {
        var initial = new StateMachineData([0], 0, ["interacted"]);
        return interacted ? initial.Set("interacted", 1) : initial;
    }

    public (uint Id, string Type, QuestObject Object)? EntryObject(Player player)
    {
        if (!IsEligibleMap(player) || Existing(player) is not { } state) return null;
        lock (state)
        {
            if (!player.Instance.World.SceneObjects.TryGetValue(StateKey, out var entity)) return null;
            return state.Stage == MirrorStage.Ready ? entity
                : (entity.Id, entity.Type, entity.Object with { StateMachine = MirrorStates(true) });
        }
    }

    // Must be called while holding lock (state).
    static AfflictionRuleset.RewardTile[] BuildTilesLocked(MirrorState state)
    {
        if (state.Stage != MirrorStage.Active || state.Rewards.Length == 0) return [];
        state.EnsureRewardArrays();
        var tiles = new AfflictionRuleset.RewardTile[state.Rewards.Length];
        for (int i = 0; i < state.Rewards.Length; i++)
        {
            uint cur = (uint)Math.Max(0, state.RewardPoints[i]);
            uint max = (uint)state.GoalForTile(i);
            uint cnt = (uint)Math.Max(0, state.RewardCounts[i]);
            tiles[i] = new((byte)state.Rewards[i], cur, max, cnt);
        }
        return tiles;
    }

    // Must be called while holding lock (state).
    static byte[]? BuildSnapshotLocked(Player player, MirrorState state)
    {
        bool started = state.Stage is MirrorStage.Active or MirrorStage.Ended;
        bool ended = state.Stage == MirrorStage.Ended;
        // A finite time accelerates the trailing edge to close the encounter. The client draws both normal edges on its own.
        float dissipationTime = ended ? state.OuterElapsed : float.PositiveInfinity;
        var regions = state.Regions ??= MirrorFogRegions.Build(player.Layout, state.MirrorX, state.MirrorY);
        return AfflictionRuleset.EncounterSnapshot(started, ended, state.OuterElapsed, state.OuterElapsed,
            dissipationTime, state.PauseTimer, state.FogPaused, state.MirrorX, state.MirrorY, state.EntityId,
            regions, BuildTilesLocked(state));
    }

    // ---- activation, tick, kills, skip -------------------------------------------------------------------------------

    public async Task<bool> OnObjectUse(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (Existing(player) is not { } state) return false;
        uint entityId;
        MirrorStage stage;
        int mirrorX, mirrorY;
        lock (state)
        {
            entityId = state.EntityId;
            stage = state.Stage;
            mirrorX = state.MirrorX;
            mirrorY = state.MirrorY;
        }
        if (entityId == 0 || use.TargetId != entityId) return false;
        if (!Current(player) || player.PlayerLife == 0) return true;
        if (stage == MirrorStage.Ready && await WorldOf(player).WalkUpTo(mirrorX, mirrorY, ActivationRadius, requestedAt))
            await Activate(player, state);
        return true;
    }

    async Task Activate(Player player, MirrorState state)
    {
        uint entityId = 0;
        byte[]? snap = null;
        await state.Gate.WaitAsync();
        try
        {
            lock (state)
            {
                if (state.Stage != MirrorStage.Ready || !Current(player)) return;
                state.Stage = MirrorStage.Active;
                state.OuterElapsed = WaveStart;
                state.OutOfFogSeconds = 0f;
                state.FogPaused = false;
                state.PauseTimer = 0f;
                state.LastTimerSeconds = 0;
                state.DepthPercent = 10;
                state.LastTickAt = AreaTime(player);
                state.NextSpawnAt = state.LastTickAt + SpawnIntervalSeconds;
                state.TilesDirty = false;
                entityId = state.EntityId;
                SaveLocked(player, state);
                snap = BuildSnapshotLocked(player, state);
            }
            var fog = AfflictionRuleset.FogEnvironment(true);
            int smIdx = MirrorComponents().ToList().IndexOf("StateMachine");
            var animation = smIdx >= 0 && entityId != 0
                ? InstanceEntities.StateMachineUpdate(entityId, (byte)smIdx, MirrorStates(true)) : null;
            foreach (var peer in Clients(player))
            {
                if (fog is not null)
                    await PlayerOf(peer).Send((byte[])fog.Clone(), "0x00df blend in Delirium fog");
                if (snap is not null)
                    await PlayerOf(peer).Send((byte[])snap.Clone(), "0x02c9 ruleset AfflictionEncounterRules mode 0 (mirror activated)");
                if (animation is not null)
                    await PlayerOf(peer).Send((byte[])animation.Clone(), $"0x02bc Delirium Mirror 0x{entityId:x} interacted = 1");
            }
        }
        finally { state.Gate.Release(); }

        foreach (var peer in Clients(player))
        {
            await WorldOf(peer).RefreshStats();
            await PlayerOf(peer).Tell("The Delirium fog rolls across the map.");
        }
        await SpawnPack(player, state);
    }

    public async Task OnTick(Player player, double dt)
    {
        if (!IsEligibleMap(player) || !Current(player) || player.Paused || player.PlayerLife == 0) return;
        if (!ReferenceEquals(Clients(player).Where(c => c.PlayerLife > 0).MinBy(c => c.Id), player)) return;
        if (Existing(player) is not { } state) return;

        MirrorStage stage;
        int mirrorX, mirrorY;
        lock (state)
        {
            stage = state.Stage;
            mirrorX = state.MirrorX;
            mirrorY = state.MirrorY;
        }

        if (stage == MirrorStage.Ready)
        {
            double now = AreaTime(player);
            foreach (var peer in Clients(player))
            {
                if (peer.PlayerLife == 0) continue;
                var pos = peer.Walk.Position(now);
                long dx = pos.X - mirrorX, dy = pos.Y - mirrorY;
                if (dx * dx + dy * dy <= (long)ActivationRadius * ActivationRadius)
                {
                    await Activate(peer, state);
                    return;
                }
            }
            return;
        }

        if (stage != MirrorStage.Active) return;

        bool pausedNow = IsDeliriumEncounterPaused(player);
        bool shouldEnd = false;
        bool shouldSpawn = false;
        byte[]? tilesPacket = null;
        byte[]? pausePacket = null;
        int? timerChange = null;
        double nowTime = AreaTime(player);
        float step = (float)Math.Clamp(dt, 0.0, 0.5);
        var alivePositions = Clients(player).Where(c => c.PlayerLife > 0).Select(c => c.Walk.Position(nowTime)).ToArray();

        await state.Gate.WaitAsync();
        try
        {
            lock (state)
            {
                if (state.Stage != MirrorStage.Active) return;
                state.LastTickAt = nowTime;
                bool pauseChanged = pausedNow != state.FogPaused;
                if (pauseChanged)
                {
                    state.FogPaused = pausedNow;
                    if (pausedNow && state.LastTimerSeconds != 0)
                    {
                        state.LastTimerSeconds = 0;
                        timerChange = 0;
                    }
                    pausePacket = AfflictionRuleset.EncounterPause(state.PauseTimer, pausedNow);
                }
                if (!state.FogPaused)
                    state.OuterElapsed += step;
                else
                    state.PauseTimer += step;

                // Reap dead Delirium monsters from our active tracking set.
                foreach (uint id in state.Monsters.ToArray())
                    if (!player.Area.Monsters.TryGetValue(id, out var m) || m.Dead || m.Life == 0)
                        state.Monsters.Remove(id);

                bool anyInFog = false;
                int maxDepth = 0;
                foreach (var pos in alivePositions)
                {
                    if (state.IsCellInFog(pos.X, pos.Y))
                    {
                        anyInFog = true;
                        maxDepth = Math.Max(maxDepth, state.DepthAt(pos.X, pos.Y));
                    }
                }
                if (anyInFog)
                {
                    state.DepthPercent = Math.Max(state.DepthPercent, maxDepth);
                    state.OutOfFogSeconds = 0f;
                    if (state.LastTimerSeconds != 0)
                    {
                        state.LastTimerSeconds = 0;
                        timerChange = 0;
                    }
                    if (!state.FogPaused && nowTime >= state.NextSpawnAt && state.Monsters.Count < MaxActiveMonsters && state.Spawned < MaxSpawnTotal)
                    {
                        state.NextSpawnAt = nowTime + SpawnIntervalSeconds;
                        shouldSpawn = true;
                    }
                }
                else if (!state.FogPaused && state.OuterElapsed > 0)
                {
                    state.OutOfFogSeconds += step;
                    int remaining = Math.Max(0, (int)Math.Ceiling(OutOfFogGraceSeconds - state.OutOfFogSeconds));
                    if (remaining != state.LastTimerSeconds)
                    {
                        state.LastTimerSeconds = remaining;
                        timerChange = remaining;
                    }
                    if (state.OutOfFogSeconds >= OutOfFogGraceSeconds)
                        shouldEnd = true;
                }

                bool needSave = timerChange.HasValue || pauseChanged;
                if (state.TilesDirty)
                {
                    state.TilesDirty = false;
                    var pkt = AfflictionRuleset.EncounterRewardTiles(BuildTilesLocked(state));
                    if (pkt is not null && (state.LastTiles is null || !state.LastTiles.AsSpan().SequenceEqual(pkt)))
                    {
                        state.LastTiles = pkt;
                        tilesPacket = pkt;
                    }
                    needSave = true;
                }
                if (needSave) SaveLocked(player, state);
            }
        }
        finally { state.Gate.Release(); }

        if (shouldEnd)
        {
            await EndEncounter(player, state, byPlayer: false);
            return;
        }
        if (pausePacket is not null)
            foreach (var peer in Clients(player))
                await PlayerOf(peer).Send((byte[])pausePacket.Clone(), pausedNow ? "0x02c9 Delirium fog paused" : "0x02c9 Delirium fog resumed");
        if (timerChange is int sec)
        {
            var pkt = sec > 0
                ? InstanceTimers.MissionTimer(InstanceTimers.MissionTimerType.Affliction, (uint)sec)
                : InstanceTimers.HideMissionTimer(InstanceTimers.MissionTimerType.Affliction);
            foreach (var peer in Clients(player))
                await PlayerOf(peer).Send((byte[])pkt.Clone(), sec > 0 ? $"0x0163 Delirium fog countdown {sec}s" : "0x0163 hide Delirium fog timer");
        }
        if (tilesPacket is not null)
            foreach (var peer in Clients(player))
                await PlayerOf(peer).Send((byte[])tilesPacket.Clone(), "0x02c9 ruleset AfflictionEncounterRules mode 1 (reward tiles)");
        if (shouldSpawn)
            await SpawnPack(player, state);
    }

    static readonly ConditionalWeakTable<ItemCatalog, string[]> ClusterJewelBases = [];

    static string[] ClusterBases(Player player) => ClusterJewelBases.GetValue(ItemsFor(player),
        catalog => [.. catalog.All.Where(t => t.Supported && t.Cluster is not null).Select(t => t.Id)]);

    public void OnDrop(DropRoll drop)
    {
        if (!IsEligibleMap(drop.Player) || Existing(drop.Player) is not { Stage: MirrorStage.Active } state) return;
        string variety = drop.Monster.VarietyId;
        bool boss = variety is SimulacrumRules.Omniphobia or SimulacrumRules.Kosis;
        if (!boss && !variety.Contains("LeagueAffliction/", StringComparison.Ordinal)) return;
        var clusters = ClusterBases(drop.Player);
        if (clusters.Length == 0) return;
        var rng = AreaRng(drop.Player);
        if (boss)
        {
            drop.RolledItems.Add(clusters[rng.Next(clusters.Length)]);
            return;
        }
        double chance = (drop.Monster.Rarity switch
        {
            MonsterRarity.Unique => 0.25,
            MonsterRarity.Rare => 0.04,
            MonsterRarity.Magic => 0.012,
            _ => 0.004,
        }) + Math.Max(0, state.DepthPercent) * 0.0004;
        if (rng.NextDouble() < chance)
            drop.RolledItems.Add(clusters[rng.Next(clusters.Length)]);
    }

    public void OnKill(KillContext kill)
    {
        if (!IsEligibleMap(kill.Player) || Existing(kill.Player) is not { } state) return;
        if (!IsActive(state)) return;
        var stats = kill.Player.Instance.AreaStats(() => AreaLeagueStats(kill.Player));
        int bonusPct = stats.GetValueOrDefault(Stat.MapAfflictionRewardProgressOnKillPlusPct);
        lock (state)
        {
            if (state.Stage != MirrorStage.Active) return;
            string? boss = state.Bosses.GetValueOrDefault(kill.Monster.ObjectId);
            if (boss is null && state.SpawnedBosses.Contains(kill.VarietyId) && Array.IndexOf(BossVarieties, kill.VarietyId) >= 0)
                boss = kill.VarietyId;
            if (boss is not null)
            {
                if (!state.RewardedBossVarieties.Add(boss)) return;
                state.RewardedBosses.Add(kill.Monster.ObjectId);
            }
            state.RecordKill(kill.Monster.Rarity, bonusPct, stats.GetValueOrDefault(Stat.MapAfflictionRewardKillsPlusPct));
            if (boss is not null
                && RandomRolls.Chance(stats.GetValueOrDefault(Stat.MapAfflictionPctChanceAdditionalRewardOnAfflictionUniqueMonsterKilled), 100, AreaRng(kill.Player)))
                state.AddReward(AreaRng(kill.Player));
            state.Monsters.Remove(kill.Monster.ObjectId);
            SaveLocked(kill.Player, state);
        }
    }

    public void OnExperience(ExperienceGain gain)
    {
        if (!IsEligibleMap(gain.Player) || Existing(gain.Player) is not { } state) return;
        int depth;
        lock (state)
        {
            if (state.Stage != MirrorStage.Active) return;
            depth = state.DepthPercent;
        }
        uint pct = (uint)(100 + Math.Clamp(depth / 2, 0, 50));
        gain.Experience = (uint)Math.Min(uint.MaxValue, (ulong)gain.Experience * pct / 100);
    }

    public Dictionary<uint, int> PlayerStats(Player player, Dictionary<uint, int> stats)
    {
        if (!IsEligibleMap(player) || Existing(player) is not { } state || !IsActive(state)) return stats;
        if (StatCatalog.IsClientId(Stat.AffectedByDelirium)) stats[Stat.AffectedByDelirium] = 1;
        if (StatCatalog.IsClientId(Stat.IsInAfflictionFog)) stats[Stat.IsInAfflictionFog] = 1;
        return stats;
    }

    /// <summary>Handles C→S <c>0x0260</c> (skip/end Delirium encounter button on the HUD).</summary>
    public async Task<bool> OnEndRequested(Player player)
    {
        if (!Current(player) || player.PlayerLife == 0 || !IsEligibleMap(player)) return false;
        if (Existing(player) is not { } state || !IsActive(state)) return false;
        await EndEncounter(player, state, byPlayer: true);
        return true;
    }

    async Task EndEncounter(Player player, MirrorState state, bool byPlayer)
    {
        byte level;
        int[] rewards;
        int[] counts;
        int splinters;
        int kills;
        byte[]? snap;
        await state.Gate.WaitAsync();
        try
        {
            lock (state)
            {
                if (state.Stage != MirrorStage.Active) return;
                state.Stage = MirrorStage.Ended;
                state.FogPaused = false;
                state.LastTimerSeconds = 0;
                state.OutOfFogSeconds = 0f;
                state.EnsureRewardArrays();
                level = state.Level > 0 ? state.Level : (byte)Math.Clamp(ServerLevels.AreaLevel(player.Layout.Id), 68, 84);
                rewards = [.. state.Rewards];
                counts = [.. state.RewardCounts];
                splinters = state.Splinters;
                kills = state.Kills;
                state.Monsters.Clear();
                SaveLocked(player, state);
                snap = BuildSnapshotLocked(player, state);
            }
        }
        finally { state.Gate.Release(); }

        var hideTimer = InstanceTimers.HideMissionTimer(InstanceTimers.MissionTimerType.Affliction);
        var fog = AfflictionRuleset.FogEnvironment(false);
        foreach (var peer in Clients(player))
        {
            await PlayerOf(peer).Send((byte[])hideTimer.Clone(), "0x0163 hide Delirium fog timer (encounter ended)");
            if (fog is not null)
                await PlayerOf(peer).Send((byte[])fog.Clone(), "0x00df blend out Delirium fog");
            if (snap is not null)
                await PlayerOf(peer).Send((byte[])snap.Clone(), "0x02c9 ruleset AfflictionEncounterRules mode 0 (ended)");
            await WorldOf(peer).RefreshStats();
        }

        var loot = DeliriumModule.RollMirrorRewards(player, level, rewards, counts, splinters);
        var spot = player.Walk.Position(AreaTime(player));
        for (int i = 0; i < loot.Count; i++)
        {
            double a = i * 2.399963229728653, r = 2 + Math.Sqrt(i) * 1.4;
            var at = player.Layout.Near((spot.X + (int)Math.Round(Math.Cos(a) * r), spot.Y + (int)Math.Round(Math.Sin(a) * r)));
            var ground = new WorldItemObject(at.X, at.Y, loot[i]);
            uint id = player.Instance.Ground.Add(ground, player);
            await SendGroundItem(player, id, ground, null, justDropped: true);
        }

        string reason = byPlayer ? "Delirium encounter ended." : "The Delirium fog has dissipated.";
        foreach (var peer in Clients(player))
            await PlayerOf(peer).Tell($"{reason} ({loot.Count} reward item(s) dropped.)");
        _context.Log($"Delirium Mirror {player.Layout.Id} ended (byPlayer={byPlayer}): {kills} kills, {loot.Count} reward items");
    }

    async Task SpawnPack(Player player, MirrorState state)
    {
        if (!_context.Lifetime.IsActive || !Current(player)) return;
        lock (state)
        {
            if (state.Spawning || state.Stage != MirrorStage.Active) return;
            state.Spawning = true;
        }
        try { await SpawnPackCore(player, state); }
        finally { lock (state) state.Spawning = false; }
    }

    async Task SpawnPackCore(Player player, MirrorState state)
    {
        var stats = player.Instance.AreaStats(() => AreaLeagueStats(player));
        byte level;
        int packSize;
        int spawnedBase;
        double delirious;
        lock (state)
        {
            if (state.Stage != MirrorStage.Active) return;
            level = state.Level > 0 ? state.Level : (byte)Math.Clamp(ServerLevels.AreaLevel(player.Layout.Id), 68, 84);
            packSize = Math.Min(5, Math.Min(MaxActiveMonsters - state.Monsters.Count, MaxSpawnTotal - state.Spawned));
            spawnedBase = state.Spawned;
            delirious = Math.Max(0.05, DeliriumScarabs.MonsterDepth(state.DepthPercent,
                stats.GetValueOrDefault(Stat.MapAfflictionEncounterMonsterDepthPlusPct)) / 100.0);
        }
        if (packSize <= 0) return;

        var pool = DeliriumMonsterIds
            .Select(id => Monsters.Find("Metadata/Monsters/LeagueAffliction/" + id))
            .OfType<MonsterCatalog.Variety>()
            .Where(v => v.Supported)
            .ToArray();
        if (pool.Length == 0) return;

        var rng = AreaRng(player);
        var centre = player.Walk.Position(AreaTime(player));
        bool spawnedAny = false;

        for (int i = 0; i < packSize; i++)
        {
            if (!_context.Lifetime.IsActive || !Current(player) || !IsActive(state)) break;
            string? boss = null;
            lock (state)
                if (stats.GetValueOrDefault(Stat.MapAfflictionContainsBosses) > 0)
                    boss = BossVarieties.FirstOrDefault(b => !state.SpawnedBosses.Contains(b));
            var variety = boss is null ? pool[rng.Next(pool.Length)] : Monsters.Find(boss);
            if (variety is not { Supported: true }) break;
            variety = variety with { StateMachine = DeliriumModule.MonsterStates(variety.StateMachine), AggroRange = Math.Max(90, variety.AggroRange) };
            int roll = rng.Next(100);
            var rarity = boss is not null ? MonsterRarity.Unique : roll < 8 ? MonsterRarity.Rare : roll < 30 ? MonsterRarity.Magic : MonsterRarity.Normal;
            var rolledMods = MonsterModsFor(player, variety, level, rarity, null, true);
            var extraStats = new Dictionary<uint, int>
            {
                [Stat.MaximumLifePlusPct] = (int)Math.Round(120 * delirious),
                [Stat.MonsterRarityDamagePlusPctFinal] = (int)Math.Round(30 * delirious),
            };
            if (StatCatalog.IsClientId(Stat.MonsterIsInAfflictionFog)) extraStats[Stat.MonsterIsInAfflictionFog] = 1;
            if (StatCatalog.IsClientId(Stat.IsInAfflictionFog)) extraStats[Stat.IsInAfflictionFog] = 1;
            var extra = new MonsterModSet([], [], new MonsterModEffects(extraStats));
            double a = (spawnedBase + i) * 2.399963229728653;
            int radius = 26 + rng.Next(24);
            var at = player.Layout.Near((centre.X + (int)Math.Round(Math.Cos(a) * radius),
                centre.Y + (int)Math.Round(Math.Sin(a) * radius)));
            await SpawnMonster(player, variety, level, rarity, at.X, at.Y,
                mods: rolledMods, extraMods: extra, unrisen: false, log: false,
                ownedCurrent: () => _context.Lifetime.IsActive && Current(player) && IsActive(state),
                owned: (spawnedId, monster) =>
                {
                    monster.Aggro = true;
                    lock (state)
                    {
                        if (state.Stage != MirrorStage.Active) return;
                        state.Monsters.Add(spawnedId);
                        state.Spawned++;
                        if (boss is not null)
                        {
                            state.SpawnedBosses.Add(boss);
                            state.Bosses[spawnedId] = boss;
                        }
                        spawnedAny = true;
                    }
                });
        }
        if (spawnedAny)
            lock (state) SaveLocked(player, state);
    }
}
