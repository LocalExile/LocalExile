using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modding;

namespace GameServer.Modules.Delirium;

/// <summary>
/// Map Delirium Mirror encounter (<c>Rulesets</c> row 102 <c>AfflictionEncounterRules</c> via S→C <c>0x1f</c> / <c>0x2c9</c>
/// and skip/end button C→S <c>0x260</c>). State belongs to <see cref="InstanceState"/> (per-<see cref="GameArea"/>),
/// safe across re-entry, party join, and module hot-reload.
/// </summary>
public sealed class DeliriumMirrorEncounter
{
    public const string MirrorPath = "Metadata/MiscellaneousObjects/Affliction/AfflictionInitiator";
    const string StateKey = "Delirium.Mirror";
    const int ActivationRadius = 12;
    const float OuterInitialRadius = 48f;
    const float OuterSpeedCellsPerSec = 14f;
    const float InnerDelaySeconds = 25f;
    const float InnerSpeedCellsPerSec = 10f;
    const float OutOfFogGraceSeconds = 5f;
    const int MaxActiveMonsters = 28;
    const int MaxSpawnTotal = 160;
    const double SpawnIntervalSeconds = 2.5;
    const int MaxRewardTiles = 3;

    static readonly string[] FallbackMirrorComponents = ["Positioned", "Animated", "StateMachine", "MinimapIcon"];
    static readonly int[] MirrorRewardPool = [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 13, 14, 15, 17, 22, 23, 24, 25];
    static readonly string[] DeliriumMonsterIds =
    [
        "AfflictionMinion1", "AfflictionMinion2", "AfflictionMinion3",
        "AfflictionMinion4", "AfflictionMinion5_", "AfflictionMinion6_",
    ];

    readonly ConditionalWeakTable<InstanceState, MirrorState> _states = new();
    readonly JsonSerializerOptions _json = new() { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };
    IModuleContext _context = null!;

    public enum MirrorStage { None = 0, Ready = 1, Active = 2, Ended = 3 }

    public sealed class MirrorState
    {
        public bool Placed { get; set; }
        public MirrorStage Stage { get; set; }
        public uint EntityId { get; set; }
        public int MirrorX { get; set; }
        public int MirrorY { get; set; }
        public byte Level { get; set; }
        public float OuterElapsed { get; set; }
        public float InnerElapsed { get; set; }
        public float OutOfFogSeconds { get; set; }
        public int LastTimerSeconds { get; set; }
        public bool FogPaused { get; set; }
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
        public HashSet<uint> Monsters { get; set; } = [];
        public string Why { get; set; } = "";

        [System.Text.Json.Serialization.JsonIgnore] public readonly SemaphoreSlim Gate = new(1, 1);
        [System.Text.Json.Serialization.JsonIgnore] public object? ShownArea;
        [System.Text.Json.Serialization.JsonIgnore] public byte[]? LastTiles;
        [System.Text.Json.Serialization.JsonIgnore] public bool TilesDirty;

        public void EnsureRewardArrays()
        {
            if (RewardPoints.Length != Rewards.Length) RewardPoints = new int[Rewards.Length];
            if (RewardCounts.Length != Rewards.Length) RewardCounts = new int[Rewards.Length];
        }

        public float OuterRadius => OuterInitialRadius + OuterElapsed * OuterSpeedCellsPerSec;
        public float InnerRadius => InnerElapsed <= 0f ? 0f : InnerElapsed * InnerSpeedCellsPerSec;

        public bool IsCellInFog(int x, int y)
        {
            if (Stage != MirrorStage.Active) return false;
            float dx = x - MirrorX, dy = y - MirrorY;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            return dist <= OuterRadius && dist >= InnerRadius;
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

        public void RecordKill(MonsterRarity rarity, int progressBonusPct)
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
            int points = Math.Max(1, (basePoints * depthBonus) / 100);
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

            // Unlock additional reward tiles at cumulative tier 3 and 5 (up to 3 tiles in a map mirror).
            int desiredTiles = Math.Min(MaxRewardTiles, 1 + (nextTotal >= 3 ? 1 : 0) + (nextTotal >= 5 ? 1 : 0));
            if (desiredTiles > Rewards.Length)
            {
                var list = Rewards.ToList();
                var pts = RewardPoints.ToList();
                var cnts = RewardCounts.ToList();
                int seed = HashCode.Combine(MirrorX, MirrorY, Kills, nextTotal);
                while (list.Count < desiredTiles)
                {
                    int pick = MirrorRewardPool[Math.Abs(seed = unchecked(seed * 31 + 17)) % MirrorRewardPool.Length];
                    if (list.Contains(pick)) pick = MirrorRewardPool[(Array.IndexOf(MirrorRewardPool, pick) + 1) % MirrorRewardPool.Length];
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

    public static bool IsEligibleMap(Player player) =>
        player.Layout.Walkable is not null
        && !SimulacrumRules.IsArena(player.Layout.Id)
        && !InTown(player)
        && HideoutCatalog.ByArea(player.Layout.Hash) is null
        && MapDevices.IsMapArea(player.Layout.Hash)
        && !LabyrinthTrials.InLabyrinth(player.Layout.Id);

    public MirrorState For(Player player) => _states.GetValue(player.Instance, instance =>
        instance.ContentState.TryGetValue(StateKey, out var text)
            ? JsonSerializer.Deserialize<MirrorState>(text, _json) ?? new()
            : new());

    public MirrorState? Existing(Player player)
    {
        if (player.Instance is not { } instance) return null;
        if (_states.TryGetValue(instance, out var state)) return state.Stage != MirrorStage.None ? state : null;
        return instance.ContentState.ContainsKey(StateKey) ? For(player) : null;
    }

    void Save(Player player, MirrorState state) =>
        player.Instance.ContentState[StateKey] = JsonSerializer.Serialize(state, _json);

    static bool Current(Player player) => player.Connection.PlayerSent && !player.LeavingArea && !player.Closed;

    static IEnumerable<Player> Clients(Player player) =>
        InGame.Keys.Where(c => !c.Controlled && ReferenceEquals(c.Instance, player.Instance) && Current(c));

    // ---- ruleset & entry ---------------------------------------------------------------------------------------------

    public IEnumerable<(string Log, byte[] Message)> EnterMessages(Player player)
    {
        if (!IsEligibleMap(player) || AfflictionRuleset.EncounterRow is not { } row) return [];
        return [($"0x001f ruleset AfflictionEncounterRules (row {row})", InstanceProtocol.AddRuleset(row))];
    }

    public async Task OnAreaPopulated(AreaPopulated area)
    {
        var player = area.Player;
        if (!IsEligibleMap(player)) return;
        var state = For(player);
        await state.Gate.WaitAsync();
        try
        {
            if (state.Placed) return;
            state.Placed = true;
            var stats = player.Instance.AreaStats(() => AreaLeagueStats(player));
            int S(uint id) => stats.GetValueOrDefault(id);
            if (S(Stat.MapDisableLeagues) > 0 || S(Stat.AtlasKeystoneNoExtraContent) > 0
                || S(Stat.MapNukeEverything) > 0 || S(Stat.MapAreaUnderlevelled) > 0)
            {
                state.Why = "disabled by map stats";
                return;
            }
            bool forced = S(Stat.MapAfflictionLeague) > 0;
            int count;
            if (forced)
            {
                count = 1;
                state.Why = "forced by map_affliction_league";
            }
            else if (S(Stat.MapDisableDeliriumFromChance) > 0)
            {
                state.Why = "disabled by map_disable_delirium_from_chance";
                return;
            }
            else
            {
                var rng = new Random(area.Seed ^ 0x44656c69);
                bool hardMode = player.LeagueModule?.HardMode == true;
                count = LeagueChances.Current.Count(player.Layout.Id, LeagueChances.Delirium, rng, alternate: hardMode,
                    increased: S(Stat.MapExtraContentHasNoBaseChanceToSpawn) > 0 ? -100 : S(Stat.MapLeagueBaseChancePlusPctFinal),
                    added: S(Stat.MapLeagueContentAdditionalChancePct) + S(Stat.MapAfflictionEncounterAdditionalChancePct),
                    encounterIncrease: S(Stat.MapAfflictionEncounterChancePlusPct));
                state.Why = $"league roll Delirium {count}";
            }
            if (count <= 0) return;

            var placeRng = new Random(area.Seed ^ 0x4166666c);
            var spot = PickMirrorSpot(player.Layout, placeRng);
            int initialTypes = Math.Clamp(1 + Math.Max(0, S(Stat.MapAfflictionEncounterRewardTypesPlus)), 1, MaxRewardTiles);
            var pickedRewards = new List<int>(initialTypes);
            while (pickedRewards.Count < initialTypes)
            {
                int r = MirrorRewardPool[placeRng.Next(MirrorRewardPool.Length)];
                if (!pickedRewards.Contains(r)) pickedRewards.Add(r);
            }

            state.Stage = MirrorStage.Ready;
            state.MirrorX = spot.X;
            state.MirrorY = spot.Y;
            state.Level = area.Level;
            state.Rewards = [.. pickedRewards];
            state.RewardPoints = new int[pickedRewards.Count];
            state.RewardCounts = new int[pickedRewards.Count];
            await PresentMirror(player, state);
            Save(player, state);
            _context.Log($"Delirium Mirror {player.Layout.Id}: {state.Why} at ({spot.X}, {spot.Y})");
        }
        finally { state.Gate.Release(); }
    }

    public async Task OnPlayerEntered(Player player)
    {
        if (!IsEligibleMap(player) || Existing(player) is not { } state) return;
        await state.Gate.WaitAsync();
        try
        {
            if (state.Stage == MirrorStage.None) return;
            if (!ReferenceEquals(state.ShownArea, player.Area))
            {
                if (state.EntityId != 0 && player.Area.Objects.Has(state.EntityId)) state.ShownArea = player.Area;
                else { state.EntityId = 0; await PresentMirror(player, state); Save(player, state); }
            }
            if (state.Stage is MirrorStage.Active or MirrorStage.Ended && BuildSnapshot(player, state) is { } snap)
                await PlayerOf(player).Send(snap, $"0x02c9 ruleset AfflictionEncounterRules mode 0 ({state.Stage})");
            if (state.Stage == MirrorStage.Active)
            {
                await WorldOf(player).RefreshStats();
                if (state.LastTimerSeconds > 0)
                    await PlayerOf(player).Send(InstanceTimers.MissionTimer(InstanceTimers.MissionTimerType.Affliction, (uint)state.LastTimerSeconds),
                        $"0x0163 Delirium fog timer {state.LastTimerSeconds}s");
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

    static StateMachineData MirrorStates(bool interacted) =>
        new([interacted ? 1 : 0], 0, ["interacted"]) { Overridden = new HashSet<int> { 0 } };

    async Task PresentMirror(Player player, MirrorState state)
    {
        if (state.EntityId == 0) state.EntityId = WorldOf(player).NewObjectId();
        var components = MirrorComponents();
        var obj = new QuestObject(state.MirrorX, state.MirrorY, components)
        {
            StateMachine = MirrorStates(state.Stage != MirrorStage.Ready),
        };
        await SendWorldCreate(player, state.EntityId, () =>
            InstanceEntities.CreateObject(state.EntityId, InstanceEntities.ObjectTypeHash(MirrorPath), obj.Blob()),
            $"Delirium Mirror 0x{state.EntityId:x} at ({state.MirrorX}, {state.MirrorY}) ({state.Stage})",
            wake: true, awaitBirth: false);
        state.ShownArea = player.Area;
    }

    static AfflictionRuleset.RewardTile[] BuildTiles(MirrorState state)
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

    static byte[]? BuildSnapshot(Player player, MirrorState state)
    {
        bool active = state.Stage == MirrorStage.Active;
        bool ended = state.Stage == MirrorStage.Ended;
        float innerDelay = state.InnerElapsed > 0f ? 0f : float.PositiveInfinity;
        float pauseTimer = state.OutOfFogSeconds > 0f ? MathF.Max(0f, OutOfFogGraceSeconds - state.OutOfFogSeconds) : 0f;
        var regions = new[]
        {
            new AfflictionRuleset.FogRegion(0, 0, Math.Max(1, player.Layout.GridW), Math.Max(1, player.Layout.GridH),
                state.MirrorX, state.MirrorY, 0f),
        };
        return AfflictionRuleset.EncounterSnapshot(active, ended, state.OuterElapsed, state.InnerElapsed,
            innerDelay, pauseTimer, state.FogPaused, state.MirrorX, state.MirrorY, state.EntityId,
            regions, BuildTiles(state));
    }

    // ---- activation, tick, kills, skip -------------------------------------------------------------------------------

    public async Task<bool> OnObjectUse(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (Existing(player) is not { } state || state.EntityId == 0 || use.TargetId != state.EntityId) return false;
        if (!Current(player) || player.PlayerLife == 0) return true;
        if (state.Stage == MirrorStage.Ready && await WorldOf(player).WalkUpTo(state.MirrorX, state.MirrorY, ActivationRadius, requestedAt))
            await Activate(player, state);
        return true;
    }

    async Task Activate(Player player, MirrorState state)
    {
        bool activated = false;
        byte[]? snap = null;
        await state.Gate.WaitAsync();
        try
        {
            if (state.Stage != MirrorStage.Ready || !Current(player)) return;
            state.Stage = MirrorStage.Active;
            state.OuterElapsed = 0.05f;
            state.InnerElapsed = 0f;
            state.OutOfFogSeconds = 0f;
            state.LastTimerSeconds = 0;
            state.DepthPercent = 10;
            state.LastTickAt = AreaTime(player);
            state.NextSpawnAt = state.LastTickAt;
            state.TilesDirty = false;
            Save(player, state);
            snap = BuildSnapshot(player, state);
            activated = true;
        }
        finally { state.Gate.Release(); }

        if (!activated) return;
        int smIdx = MirrorComponents().ToList().IndexOf("StateMachine");
        if (smIdx >= 0 && state.EntityId != 0)
            await SendWorldRecord(player, state.EntityId,
                InstanceEntities.StateMachineUpdate(state.EntityId, (byte)smIdx, MirrorStates(true)),
                $"0x02bc Delirium Mirror 0x{state.EntityId:x} interacted = 1");
        if (snap is not null)
            foreach (var peer in Clients(player))
                await PlayerOf(peer).Send(snap, "0x02c9 ruleset AfflictionEncounterRules mode 0 (mirror activated)");
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

        if (state.Stage == MirrorStage.Ready)
        {
            double now = AreaTime(player);
            foreach (var peer in Clients(player))
            {
                if (peer.PlayerLife == 0) continue;
                var pos = peer.Walk.Position(now);
                long dx = pos.X - state.MirrorX, dy = pos.Y - state.MirrorY;
                if (dx * dx + dy * dy <= (long)ActivationRadius * ActivationRadius)
                {
                    await Activate(peer, state);
                    return;
                }
            }
            return;
        }

        if (state.Stage != MirrorStage.Active) return;

        bool shouldEnd = false;
        bool shouldSpawn = false;
        byte[]? tilesPacket = null;
        int? timerChange = null;
        double nowTime = AreaTime(player);
        float step = (float)Math.Clamp(dt, 0.0, 0.5);

        await state.Gate.WaitAsync();
        try
        {
            if (state.Stage != MirrorStage.Active) return;
            state.LastTickAt = nowTime;
            if (!state.FogPaused)
            {
                state.OuterElapsed += step;
                if (state.OuterElapsed >= InnerDelaySeconds)
                    state.InnerElapsed += step;
            }

            // Reap dead Delirium monsters from our active tracking set.
            lock (state)
            {
                foreach (uint id in state.Monsters.ToArray())
                    if (!player.Area.Monsters.TryGetValue(id, out var m) || m.Dead || m.Life == 0)
                        state.Monsters.Remove(id);
            }

            bool anyInFog = false;
            int maxDepth = 0;
            foreach (var peer in Clients(player))
            {
                if (peer.PlayerLife == 0) continue;
                var pos = peer.Walk.Position(nowTime);
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
                int activeCount;
                lock (state) activeCount = state.Monsters.Count;
                if (nowTime >= state.NextSpawnAt && activeCount < MaxActiveMonsters && state.Spawned < MaxSpawnTotal)
                {
                    state.NextSpawnAt = nowTime + SpawnIntervalSeconds;
                    shouldSpawn = true;
                }
            }
            else
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

            lock (state)
            {
                if (state.TilesDirty)
                {
                    state.TilesDirty = false;
                    var pkt = AfflictionRuleset.EncounterRewardTiles(BuildTiles(state));
                    if (pkt is not null && (state.LastTiles is null || !state.LastTiles.AsSpan().SequenceEqual(pkt)))
                    {
                        state.LastTiles = pkt;
                        tilesPacket = pkt;
                    }
                    Save(player, state);
                }
            }
        }
        finally { state.Gate.Release(); }

        if (shouldEnd)
        {
            await EndEncounter(player, state, byPlayer: false);
            return;
        }
        if (timerChange is int sec)
        {
            var pkt = sec > 0
                ? InstanceTimers.MissionTimer(InstanceTimers.MissionTimerType.Affliction, (uint)sec)
                : InstanceTimers.HideMissionTimer(InstanceTimers.MissionTimerType.Affliction);
            foreach (var peer in Clients(player))
                await PlayerOf(peer).Send(pkt, sec > 0 ? $"0x0163 Delirium fog countdown {sec}s" : "0x0163 hide Delirium fog timer");
        }
        if (tilesPacket is not null)
            foreach (var peer in Clients(player))
                await PlayerOf(peer).Send(tilesPacket, "0x02c9 ruleset AfflictionEncounterRules mode 1 (reward tiles)");
        if (shouldSpawn)
            await SpawnPack(player, state);
    }

    public void OnKill(KillContext kill)
    {
        if (!IsEligibleMap(kill.Player) || Existing(kill.Player) is not { Stage: MirrorStage.Active } state) return;
        var stats = kill.Player.Instance.AreaStats(() => AreaLeagueStats(kill.Player));
        int bonusPct = stats.GetValueOrDefault(Stat.MapAfflictionRewardProgressOnKillPlusPct);
        uint deadId = kill.Player.Area.Monsters.FirstOrDefault(p => ReferenceEquals(p.Value, kill.Monster)).Key;
        lock (state)
        {
            if (state.Stage != MirrorStage.Active) return;
            state.RecordKill(kill.Monster.Rarity, bonusPct);
            if (deadId != 0) state.Monsters.Remove(deadId);
            Save(kill.Player, state);
        }
    }

    public void OnExperience(ExperienceGain gain)
    {
        if (!IsEligibleMap(gain.Player) || Existing(gain.Player) is not { Stage: MirrorStage.Active } state) return;
        uint pct = (uint)(100 + Math.Clamp(state.DepthPercent / 2, 0, 50));
        gain.Experience = (uint)Math.Min(uint.MaxValue, (ulong)gain.Experience * pct / 100);
    }

    public Dictionary<uint, int> PlayerStats(Player player, Dictionary<uint, int> stats)
    {
        if (!IsEligibleMap(player) || Existing(player) is not { Stage: MirrorStage.Active }) return stats;
        if (StatCatalog.IsClientId(Stat.AffectedByDelirium)) stats[Stat.AffectedByDelirium] = 1;
        if (StatCatalog.IsClientId(Stat.IsInAfflictionFog)) stats[Stat.IsInAfflictionFog] = 1;
        return stats;
    }

    /// <summary>Handles C→S <c>0x0260</c> (skip/end Delirium encounter button on the HUD).</summary>
    public async Task<bool> OnEndRequested(Player player)
    {
        if (!Current(player) || player.PlayerLife == 0 || !IsEligibleMap(player)) return false;
        if (Existing(player) is not { Stage: MirrorStage.Active } state) return false;
        await EndEncounter(player, state, byPlayer: true);
        return true;
    }

    async Task EndEncounter(Player player, MirrorState state, bool byPlayer)
    {
        byte level;
        int[] rewards;
        int[] counts;
        int splinters;
        byte[]? snap;
        await state.Gate.WaitAsync();
        try
        {
            if (state.Stage != MirrorStage.Active) return;
            state.Stage = MirrorStage.Ended;
            state.LastTimerSeconds = 0;
            state.OutOfFogSeconds = 0f;
            state.EnsureRewardArrays();
            level = state.Level > 0 ? state.Level : (byte)Math.Clamp(ServerLevels.AreaLevel(player.Layout.Id), 68, 84);
            rewards = [.. state.Rewards];
            counts = [.. state.RewardCounts];
            splinters = state.Splinters;
            state.Monsters.Clear();
            Save(player, state);
            snap = BuildSnapshot(player, state);
        }
        finally { state.Gate.Release(); }

        var hideTimer = InstanceTimers.HideMissionTimer(InstanceTimers.MissionTimerType.Affliction);
        foreach (var peer in Clients(player))
        {
            await PlayerOf(peer).Send(hideTimer, "0x0163 hide Delirium fog timer (encounter ended)");
            if (snap is not null)
                await PlayerOf(peer).Send(snap, "0x02c9 ruleset AfflictionEncounterRules mode 0 (ended)");
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
        _context.Log($"Delirium Mirror {player.Layout.Id} ended (byPlayer={byPlayer}): {state.Kills} kills, {loot.Count} reward items");
    }

    async Task SpawnPack(Player player, MirrorState state)
    {
        if (!_context.Lifetime.IsActive || !Current(player) || state.Stage != MirrorStage.Active) return;
        var pool = DeliriumMonsterIds
            .Select(id => Monsters.Find("Metadata/Monsters/LeagueAffliction/" + id))
            .OfType<MonsterCatalog.Variety>()
            .Where(v => v.Supported)
            .ToArray();
        if (pool.Length == 0) return;

        var rng = AreaRng(player);
        var centre = player.Walk.Position(AreaTime(player));
        byte level = state.Level > 0 ? state.Level : (byte)Math.Clamp(ServerLevels.AreaLevel(player.Layout.Id), 68, 84);
        int packSize = Math.Min(5, Math.Min(MaxActiveMonsters - state.Monsters.Count, MaxSpawnTotal - state.Spawned));
        if (packSize <= 0) return;
        double delirious = Math.Clamp(state.DepthPercent / 100.0, 0.05, 1.0);

        for (int i = 0; i < packSize; i++)
        {
            if (!_context.Lifetime.IsActive || !Current(player) || state.Stage != MirrorStage.Active) break;
            var variety = pool[rng.Next(pool.Length)];
            variety = variety with { StateMachine = DeliriumModule.MonsterStates(variety.StateMachine), AggroRange = Math.Max(90, variety.AggroRange) };
            int roll = rng.Next(100);
            var rarity = roll < 8 ? MonsterRarity.Rare : roll < 30 ? MonsterRarity.Magic : MonsterRarity.Normal;
            var rolledMods = MonsterModsFor(player, variety, level, rarity, null, true);
            var extraStats = new Dictionary<uint, int>
            {
                [Stat.MaximumLifePlusPct] = (int)Math.Round(120 * delirious),
                [Stat.MonsterRarityDamagePlusPctFinal] = (int)Math.Round(30 * delirious),
            };
            if (StatCatalog.IsClientId(Stat.MonsterIsInAfflictionFog)) extraStats[Stat.MonsterIsInAfflictionFog] = 1;
            if (StatCatalog.IsClientId(Stat.IsInAfflictionFog)) extraStats[Stat.IsInAfflictionFog] = 1;
            var extra = new MonsterModSet([], [], new MonsterModEffects(extraStats));
            double a = (state.Spawned + i) * 2.399963229728653;
            int radius = 26 + rng.Next(24);
            var at = player.Layout.Near((centre.X + (int)Math.Round(Math.Cos(a) * radius),
                centre.Y + (int)Math.Round(Math.Sin(a) * radius)));
            await SpawnMonster(player, variety, level, rarity, at.X, at.Y,
                mods: rolledMods, extraMods: extra, unrisen: false, log: false,
                ownedCurrent: () => _context.Lifetime.IsActive && Current(player) && state.Stage == MirrorStage.Active,
                owned: (spawnedId, monster) =>
                {
                    monster.Aggro = true;
                    state.Monsters.Add(spawnedId);
                    state.Spawned++;
                });
        }
        Save(player, state);
    }
}
