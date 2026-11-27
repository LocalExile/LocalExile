using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Delirium;

/// <summary>A Simulacrum belongs to the area instance, not to its portal owner or the connection running its AI.</summary>
public sealed partial class DeliriumModule : IModule
{
    const string StateKey = "Delirium.Simulacrum";
    readonly ConditionalWeakTable<InstanceState, Run> runs = new();
    readonly ConditionalWeakTable<Player, View> views = new();
    readonly DeliriumMirrorEncounter mirror = new();
    readonly JsonSerializerOptions serialization = new() { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };
    IModuleContext context = null!;

    public sealed class Run
    {
        public int Wave { get; set; }
        public bool Active { get; set; }
        public bool Rewarded { get; set; }
        public int Spawned { get; set; }
        public int Budget { get; set; }
        public int ProgressGoal { get; set; }
        public int SpawnPoints { get; set; }
        public int KillPoints { get; set; }
        public Dictionary<uint, int> MonsterPoints { get; set; } = [];
        public int Kills { get; set; }
        public int OmniCount { get; set; }
        public int KosisCount { get; set; }
        public bool OmniThisWave { get; set; }
        public bool KosisThisWave { get; set; }
        public bool OmniSpawnedThisWave { get; set; }
        public bool KosisSpawnedThisWave { get; set; }
        public double Started { get; set; }
        public double NextSpawnAt { get; set; }
        public int VisualVersion { get; set; }
        public HashSet<uint> Monsters { get; set; } = [];
        public int[] Rewards { get; set; } = [];
        /// <summary>Per reward type, parallel to <see cref="Rewards"/>: the kill points towards its next cycle, and
        /// how many cycles it completed this wave. A filled bar counts one reward; the wave pays them all when it ends.</summary>
        public int[] RewardPoints { get; set; } = [];
        public int[] RewardCounts { get; set; } = [];
        /// <summary>Sizes the per-type arrays to the wave's types (a save from before they existed has none).</summary>
        public void SizeRewards()
        {
            if (RewardPoints.Length != Rewards.Length) RewardPoints = new int[Rewards.Length];
            if (RewardCounts.Length != Rewards.Length) RewardCounts = new int[Rewards.Length];
        }
        /// <summary>The wave's reward types as the initiator's ObjectMagicProperties mods (AfflictionMapReward1..9,
        /// one value per type).</summary>
        public ItemMod[] RewardMods { get; set; } = [];
        public Dictionary<string, int> Modifiers { get; set; } = new(StringComparer.Ordinal);
        public string[] ModifierNames { get; set; } = [];
        public ItemMod[] Affixes { get; set; } = [];
        public Run? Next { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public bool Busy;
        [System.Text.Json.Serialization.JsonIgnore] public byte[]? LastTiles;
        /// <summary>The wave's monster objects by id, while this module generation runs: a kill whose corpse the
        /// killing blow consumed (an explosion, Devour) has already left conn.Monsters when the kill handlers run, so
        /// it is matched here, and Step counts one that died out of the list.</summary>
        [System.Text.Json.Serialization.JsonIgnore] public Dictionary<uint, SpawnedMonster> Bodies { get; } = [];
        const int OmniSpawnAt = 20, KosisSpawnAt = 40;
        // In existing saved waves a passed spawn ordinal already accounts for the corresponding boss.
        bool OmniPending => OmniThisWave && !OmniSpawnedThisWave && Spawned <= OmniSpawnAt;
        bool KosisPending => KosisThisWave && !KosisSpawnedThisWave && Spawned <= KosisSpawnAt;
        bool SpawnBudgetSpent => ProgressGoal > 0 ? SpawnPoints >= ProgressGoal : Spawned >= Budget;
        [System.Text.Json.Serialization.JsonIgnore] public bool SpawningDone => SpawnBudgetSpent && !OmniPending && !KosisPending;
        public string? DueBoss() => OmniPending && (Spawned == OmniSpawnAt || SpawnBudgetSpent) ? SimulacrumRules.Omniphobia
            : KosisPending && (Spawned == KosisSpawnAt || SpawnBudgetSpent) ? SimulacrumRules.Kosis : null;
        public bool CanSpawnDueBoss(double now, int activeLimit) =>
            Active && now >= NextSpawnAt && Monsters.Count < activeLimit && DueBoss() is not null;
        public int SpawnAllowance(double now, int packSize, int activeLimit) => !Active || now < NextSpawnAt ? 0
            : Math.Max(0, Math.Min(Math.Min(packSize, ProgressGoal > 0 ? ProgressGoal - SpawnPoints : Budget - Spawned), activeLimit - Monsters.Count));
        public bool RecordKill(uint id)
        {
            Bodies.Remove(id);
            if (!Monsters.Remove(id)) return false;
            Kills++;
            if (!MonsterPoints.Remove(id, out int points)) return true;
            KillPoints += points;
            SizeRewards();
            // Every type's bar advances by the same kill points and fills at its own goal, so the common types pay
            // more often than the rare ones. The wave pays what the bars counted when it is over.
            for (int i = 0; i < Rewards.Length; i++)
            {
                int goal = RewardGoal(this, i), total = RewardPoints[i] + points;
                int paid = total / goal;
                RewardPoints[i] = total - paid * goal;
                if (paid > 0) RewardCounts[i] += paid;
            }
            return true;
        }
        public bool TryComplete()
        {
            if (!Active || Rewarded || !SpawningDone || Monsters.Count != 0 || ProgressGoal > 0 && KillPoints < ProgressGoal) return false;
            Active = false; Rewarded = true;
            return true;
        }
    }
    sealed class View { public uint Entity, Stash; public (int X, int Y) At; public bool Native, Repairing; }
    sealed class Cleanup(Action clean) : IDisposable { public void Dispose() => clean(); }

    public static void LoadData()
    {
        AfflictionRuleset.LoadGameData();
        DeliriumScarabs.Load();
    }

    public void Load(IModuleContext module)
    {
        LoadData();
        context = module;
        mirror.Attach(module);
        module.Lifetime.Own(new Cleanup(() => { runs.Clear(); views.Clear(); mirror.Clear(); }));
        GameEvents.MapOpening.Add(module.Manifest.Id, request => SimulacrumRules.Resolve(request, global::Game.AreaRng(request.Player)));
        GameEvents.MapOpening.Add(module.Manifest.Id, DeliriumScarabs.Opening);
        GameEvents.GroundItemPreparing.Add(module.Manifest.Id, DeliriumScarabs.Prepare);
        InstanceEvents.TerrainPluginStates.Add(module.Manifest.Id, DeliriumMirrorEncounter.PlanTerrain);
        GameEvents.AreaRuleset.Add(module.Manifest.Id, player =>
            DeliriumMirrorEncounter.IsEligibleMap(player) && AfflictionRuleset.EncounterRow is { } row
                ? (row, AfflictionRuleset.PreloadGroup) : null);
        GameEvents.AreaHeader.Add(module.Manifest.Id, player => SimulacrumRules.IsArena(player.Layout.Id)
            ? new AreaHeader(null, null) { Level = (byte)SimulacrumRules.Level(State(player).Wave == 0 ? 1 : State(player).Wave) } : null);
        GameEvents.BeforeEnterMessages.Add(module.Manifest.Id, mirror.BeforeEnterMessages);
        GameEvents.BeforeEnterObject.Add(module.Manifest.Id, mirror.EntryObject);
        GameEvents.EnterMessages.Add(module.Manifest.Id, player =>
            SimulacrumRules.IsArena(player.Layout.Id) && AfflictionRuleset.Row is { } rules
                ? [($"0x001f ruleset AfflictionEndGameRules (row {rules})", InstanceProtocol.AddRuleset(rules))]
                : []);
        // A Simulacrum is entered through its map device and left through its own portal; the area's authored waypoint
        // would offer a way out the encounter does not have.
        InstanceEvents.AreaWaypoint.Add(module.Manifest.Id, (player, waypoint) =>
            SimulacrumRules.IsArena(player.Layout.Id) ? null : waypoint);
        GameEvents.PlayerEntered.Add(module.Manifest.Id, async player =>
        {
            await Enter(player);
            await mirror.OnPlayerEntered(player);
        });
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (player, why) =>
        {
            if (SimulacrumRules.IsArena(player.Layout.Id))
            {
                if (ViewOf(player) is { } v)
                    { player.Area.Monsters.Remove(v.Entity); player.MinionOrigins.TryRemove(v.Entity, out _); }
                player.Instance.ContentState.TryRemove($"{StateKey}.view.{player.Id}", out _);
            }
            views.Remove(player);
            return Task.CompletedTask;
        });
        GameEvents.AreaTick.Add(module.Manifest.Id, start =>
        {
            if (SimulacrumRules.IsArena(start.Layout.Id))
                return async (player, _) =>
                {
                    if (!Current(player)) return;
                    if (ViewOf(player) is not { Native: true }) await Enter(player);
                    if (player.Paused || player.PlayerLife == 0
                        || !ReferenceEquals(Clients(player).Where(c => c.PlayerLife > 0).MinBy(c => c.Id), player)) return;
                    await Step(player);
                };
            return DeliriumMirrorEncounter.IsEligibleMap(start) ? mirror.OnTick : null;
        });
        GameEvents.ObjectUse.Add(module.Manifest.Id, async (player, use, requestedAt) =>
        {
            if (SimulacrumRules.IsArena(player.Layout.Id) && ViewOf(player) is { } view && use.TargetId == view.Entity)
            {
                if (await WorldOf(player).WalkUpTo(view.At.X, view.At.Y, 12, requestedAt)) await Start(player);
                return true;
            }
            return await mirror.OnObjectUse(player, use, requestedAt);
        });
        GameEvents.NpcTalk.Add(module.Manifest.Id, async (player, id, _) =>
        {
            if (!SimulacrumRules.IsArena(player.Layout.Id) || ViewOf(player) is not { } v || v.Entity != id) return false;
            await Start(player); return true;
        });
        GameEvents.Packet(AfflictionRuleset.EndEncounterPacketId).Add(module.Manifest.Id, async (player, _) =>
            await mirror.OnEndRequested(player));
        GameEvents.Drops.Add(module.Manifest.Id, (Action<DropRoll>)(drop =>
        {
            if (SimulacrumRules.IsArena((string)drop.Player.Layout.Id)) { drop.Cancel = true; drop.Gold = 0; }
            else mirror.OnDrop(drop);
        }));
        GameEvents.Experience.Add(module.Manifest.Id, (Action<ExperienceGain>)(gain =>
        {
            if (SimulacrumRules.IsArena((string)gain.Player.Layout.Id))
                gain.Experience = (uint)Math.Min(uint.MaxValue, (ulong)((ulong)gain.Experience * (uint)(100 + Math.Min(300, (int)(Math.Max(0, (int)(State((Player)gain.Player).Wave - 1)) * 100 / 3))) / 100));
            else
                mirror.OnExperience(gain);
        }));
        GameEvents.Kill.Add(module.Manifest.Id, kill =>
        {
            if (!SimulacrumRules.IsArena(kill.Player.Layout.Id))
            {
                mirror.OnKill(kill);
                return;
            }
            var run = State(kill.Player);
            bool last;
            // The ordinary hit path runs kill handlers outside the area's frame, while the spawner and Step change the
            // same run under it (and another player's kill may run at once).
            uint id = run.Monsters.FirstOrDefault(id => run.Bodies.TryGetValue(id, out var body) && ReferenceEquals(body, kill.Monster)
                || kill.Player.Area.Monsters.TryGetValue(id, out var m) && ReferenceEquals(m, kill.Monster));
            if (id == 0 || !run.RecordKill(id)) return;
            Save(kill.Player, run);
            last = run.Active && run.SpawningDone && run.Monsters.Count == 0;
            if (last)
                kill.Later(() => Clients(kill.Player).MinBy(c => c.Id) is { } live ? Step(live) : Task.CompletedTask);
        });
        GameEvents.StateBuilding.Add(module.Manifest.Id, (player, json) =>
        {
            if (SimulacrumRules.IsArena(player.Layout.Id))
                json["simulacrum"] = JsonSerializer.SerializeToNode(State(player), serialization);
            else if (mirror.Existing(player) is { } m)
                json["delirium_mirror"] = JsonSerializer.SerializeToNode(m, serialization);
        });
        // Simulacrum arenas and active Delirium Mirrors set the fog flags the client's Delirium logic reads.
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, (player, stats) =>
            SimulacrumRules.IsArena(player.Layout.Id) ? PlayerStats(player, stats) : mirror.PlayerStats(player, stats));
    }

    static Dictionary<uint, int> PlayerStats(Player player, Dictionary<uint, int> stats)
    {
        if (!SimulacrumRules.IsArena(player.Layout.Id)) return stats;
        if (StatCatalog.IsClientId(Stat.AffectedByDelirium)) stats[Stat.AffectedByDelirium] = 1;
        if (StatCatalog.IsClientId(Stat.IsInAfflictionFog)) stats[Stat.IsInAfflictionFog] = 1;
        return stats;
    }

    static byte[]? BuildTilesPacket(Run run)
    {
        if (!run.Active || run.Rewards.Length == 0) return AfflictionRuleset.ClearRewardTiles();
        var tiles = new AfflictionRuleset.RewardTile[run.Rewards.Length];
        for (int i = 0; i < run.Rewards.Length; i++)
        {
            uint cur = (uint)Math.Max(0, i < run.RewardPoints.Length ? run.RewardPoints[i] : 0);
            uint max = (uint)RewardGoal(run, i);
            uint count = (uint)Math.Max(0, i < run.RewardCounts.Length ? run.RewardCounts[i] : 0);
            tiles[i] = new((byte)run.Rewards[i], cur, max, count);
        }
        return AfflictionRuleset.RewardTiles(tiles);
    }

    static async Task PublishTiles(Player player, Run run, bool force = false)
    {
        if (BuildTilesPacket(run) is not { } packet) return;
        if (!force && run.LastTiles is { } prev && prev.AsSpan().SequenceEqual(packet)) return;
        run.LastTiles = packet;
        int count = run.Active ? run.Rewards.Length : 0;
        string log = $"0x02c9 ruleset AfflictionEndGameRules mode 1 ({count} tile(s))";
        foreach (var peer in Clients(player))
            await PlayerOf(peer).Send((byte[])packet.Clone(), log);
    }

    Run State(Player player) => runs.GetValue(player.Instance, instance =>
        instance.ContentState.TryGetValue(StateKey, out var text) ? JsonSerializer.Deserialize<Run>(text, serialization) ?? new() : new());
    void Save(Player player, Run run) => player.Instance.ContentState[StateKey] = JsonSerializer.Serialize(run, serialization);
    static bool Current(Player player) => player.Connection.PlayerSent && !player.LeavingArea && !player.Closed;
    static IEnumerable<Player> Clients(Player player) => InGame.Keys.Where(c => c.HasClient && ReferenceEquals(c.Instance, player.Instance) && Current(c));
    View? ViewOf(Player player)
    {
        if (views.TryGetValue(player, out var view)) return view;
        if (!player.Instance.ContentState.TryGetValue($"{StateKey}.view.{player.Id}", out var text)) return null;
        var parts = text.Split(',');
        if (parts.Length is not (4 or 5) || !uint.TryParse(parts[0], out uint entity) || !uint.TryParse(parts[1], out uint stash)
            || !int.TryParse(parts[2], out int x) || !int.TryParse(parts[3], out int y)) return null;
        return views.GetValue(player, _ => new View { Entity = entity, Stash = stash, At = (x, y), Native = parts.Length == 5 && parts[4] == "1" });
    }

    async Task Enter(Player player)
    {
        if (!SimulacrumRules.IsArena(player.Layout.Id)) return;
        var view = ViewOf(player) ?? views.GetOrCreateValue(player);
        if (view.Native || view.Repairing) return;
        view.Repairing = true;
        try { await CreateStarter(player, view, State(player)); }
        finally { view.Repairing = false; }
        if (view.Stash == 0)
        {
            var stashAt = player.Layout.Near(StashSpot(player.Layout.Id) is { } spot ? (spot.X, spot.Y)
                : (player.Layout.SpawnX - 15, player.Layout.SpawnY + 8));
            uint stash = player.TakeObjectId(); view.Stash = stash;
            await SendWorldCreate(player, stash, () => InstanceEntities.CreateObject(stash,
                InstanceEntities.ObjectTypeHash(StashObject.MetadataPath),
                new StashObject(stashAt.X, stashAt.Y, StashSpot(player.Layout.Id)?.Turn ?? 0).Blob()),
                    "Simulacrum: stash between waves", wake: true, awaitBirth: false);
            player.StashObject = stash; player.StashAt = stashAt; global::Game.SetAreaStash(player, stash, stashAt);
        }
        SaveView(player, view);
        var run = State(player);
        if (run.Active && run.Rewards.Length > 0 && BuildTilesPacket(run) is { } tiles)
            await PlayerOf(player).Send(tiles, $"0x02c9 ruleset AfflictionEndGameRules mode 1 ({run.Rewards.Length} tile(s))");
        await PlayerOf(player).Tell(run.Wave == SimulacrumRules.Waves && run.Rewarded ? "Simulacrum complete. All 15 waves defeated."
            : run.Active ? $"Simulacrum: wave {run.Wave}/15 continues."
            : $"Simulacrum: interact with the familiar entity to begin wave {run.Wave + 1}/15.");
    }

    async Task Start(Player player)
    {
        var run = State(player);
        if (run.Wave == SimulacrumRules.Waves && run.Rewarded)
        {
            if (Descendancies.Find("Delirious") is { } bloodline)
                await OpenDescendancySelector(player, bloodline.Index);
            return;
        }
        if (!Current(player) || player.PlayerLife == 0 || run.Active || run.Busy || run.Wave >= SimulacrumRules.Waves) return;
        run.Busy = true;
        List<uint> cleared = [], shown = [];
        try
        {
            var prepared = run.Next ?? PrepareWave(player, run.Wave + 1, run);
            // A preview retained across this module upgrade keeps its affixes, but adopts the new population model and
            // gains the reward-type mods added after it was saved.
            if (prepared.ProgressGoal == 0) prepared.ProgressGoal = WaveProgressGoal(run.Wave + 1);
            if (prepared.RewardMods.Length == 0 && prepared.Rewards.Length > 0) prepared.RewardMods = RewardModsFor(player, prepared.Rewards);
            if (!Current(player) || player.PlayerLife == 0) return;
            // No automatic advance; this is the only transition into a new wave.
            run.Wave++; run.Active = true; run.Rewarded = false; run.Spawned = run.Kills = 0;
            run.Budget = prepared.Budget; run.Rewards = prepared.Rewards; run.Modifiers = prepared.Modifiers;
            run.ProgressGoal = prepared.ProgressGoal; run.SpawnPoints = run.KillPoints = 0; run.MonsterPoints.Clear();
            run.RewardPoints = new int[run.Rewards.Length]; run.RewardCounts = new int[run.Rewards.Length];
            run.Affixes = prepared.Affixes; run.RewardMods = prepared.RewardMods; run.Next = null;
            run.ModifierNames = prepared.ModifierNames; run.OmniThisWave = prepared.OmniThisWave; run.KosisThisWave = prepared.KosisThisWave;
            run.OmniSpawnedThisWave = run.KosisSpawnedThisWave = false;
            run.Monsters.Clear(); run.Bodies.Clear(); run.Started = AreaTime(player); run.NextSpawnAt = run.Started;
            cleared = ClearGround(player.Instance.Ground);
            // Every client hears of it: the removals are the area's records.
            shown.AddRange(cleared);
            var strays = ClearMonsters(player, run);
            Save(player, run);
            foreach (var peer in Clients(player)) await WorldOf(peer).RefreshStats();
            if (cleared.Count > 0) Console.WriteLine($"[gateway] Simulacrum wave {run.Wave}: {cleared.Count} items on the ground destroyed");
            foreach (uint id in shown)
                await SendWorldRemoval(player, id, $"0x02bd remove world item 0x{id:x} (Simulacrum wave {run.Wave} begins)");
            foreach (uint id in strays)
                await SendWorldRemoval(player, id, $"0x02bd remove monster 0x{id:x} (Simulacrum wave {run.Wave} begins)");
            await PublishTiles(player, run, force: true);
            await PublishStarter(player, run, recreate: false);
        }
        finally { run.Busy = false; }
        await Step(player);
    }

    /// <summary>Every item on the arena's ground goes when the next wave begins, the players' own drops too (3.29.3c
    /// ClientStrings ObjectDisplayAfflictionatorWarning: "All items on the ground are destroyed when the next wave
    /// begins."). Returns the ids that went.</summary>
    public static List<uint> ClearGround(InstanceGround ground)
    {
        List<uint> cleared = [];
        foreach (var (id, item) in ground.All())
            if (ground.Take(id, item) is not null) cleared.Add(id);
        return cleared;
    }

    /// <summary>
    /// Living hostile monsters the area still holds when a wave ends or the next begins (including unrisen dormant
    /// ones). A boss's summons and its phased bodies are not in the wave's list, so nothing in the wave's accounting
    /// removes them and their entrance grace leaves them unkillable. Dead corpses and friendly player minions
    /// (<see cref="SpawnedMonster.OriginallyHostile"/> false) are kept. Returns the ids that went.
    /// </summary>
    static List<uint> ClearMonsters(Player player, Run run)
    {
        List<uint> cleared = [.. player.Area.Monsters
            .Where(pair => pair.Value.OriginallyHostile && !pair.Value.Dead && pair.Value.Life > 0)
            .Select(pair => pair.Key)];
        List<uint> dormant = [.. player.Area.DormantMonsters
            .Where(pair => pair.Value.OriginallyHostile && !pair.Value.Dead && pair.Value.Life > 0)
            .Select(pair => pair.Key)];
        foreach (uint id in cleared)
        {
            if (player.Area.Monsters.Remove(id, out var m)) { m.Dead = true; m.Life = 0; m.ActionGeneration++; }
            player.Instance.World.MonsterOrigins.TryRemove(id, out _);
            run.Monsters.Remove(id); run.Bodies.Remove(id); run.MonsterPoints.Remove(id);
        }
        foreach (uint id in dormant)
        {
            if (player.Area.DormantMonsters.Remove(id, out var m)) { m.Dead = true; m.Life = 0; m.ActionGeneration++; }
            player.Instance.World.MonsterOrigins.TryRemove(id, out _);
            run.Monsters.Remove(id); run.Bodies.Remove(id); run.MonsterPoints.Remove(id);
            cleared.Add(id);
        }
        if (cleared.Count > 0) player.Area.MonstersVersion++;
        return cleared;
    }

    async Task Step(Player player)
    {
        var run = State(player);
        if (!context.Lifetime.IsActive || !Current(player) || !run.Active || run.Busy) return;
        run.Busy = true;
        run.SizeRewards();
        bool killed = false;
        foreach (uint id in run.Monsters.ToArray())
        {
            // Bodies is not saved: after a module reload it is learnt again from the list here, so a later
            // corpse-consuming kill is still matched.
            if (player.Area.Monsters.TryGetValue(id, out var monster)) run.Bodies.TryAdd(id, monster);
            if ((monster is not null ? monster.Dead || monster.Life == 0
                : run.Bodies.TryGetValue(id, out var body) && (body.Dead || body.Life == 0)) && run.RecordKill(id))
                killed = true;
        }
        if (killed) Save(player, run);
        try
        {
            if (run.VisualVersion == 0) await RevealExisting(player, run);
            if (!run.SpawningDone) await SpawnBatch(player, run);
            if (run.SpawningDone && run.Monsters.Count == 0) await Complete(player, run);
            else await PublishTiles(player, run);
        }
        finally { run.Busy = false; }
    }

    static async Task Broadcast(Player player, string text)
    {
        foreach (var peer in Clients(player))
            await PlayerOf(peer).Tell(text);
    }
}
