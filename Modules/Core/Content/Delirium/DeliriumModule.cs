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
        public double Started { get; set; }
        public double NextSpawnAt { get; set; }
        public int VisualVersion { get; set; }
        public HashSet<uint> Monsters { get; set; } = [];
        public int[] Rewards { get; set; } = [];
        public Dictionary<string, int> Modifiers { get; set; } = new(StringComparer.Ordinal);
        public string[] ModifierNames { get; set; } = [];
        public ItemMod[] Affixes { get; set; } = [];
        public Run? Next { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public bool Busy;
        /// <summary>The wave's monster objects by id, while this module generation runs: a kill whose corpse the
        /// killing blow consumed (an explosion, Devour) has already left conn.Monsters when the kill handlers run, so
        /// it is matched here, and Step counts one that died out of the list.</summary>
        [System.Text.Json.Serialization.JsonIgnore] public Dictionary<uint, SpawnedMonster> Bodies { get; } = [];
        [System.Text.Json.Serialization.JsonIgnore] public bool SpawningDone => ProgressGoal > 0 ? SpawnPoints >= ProgressGoal : Spawned >= Budget;
        public int SpawnAllowance(double now, int packSize, int activeLimit) => !Active || now < NextSpawnAt ? 0
            : Math.Max(0, Math.Min(Math.Min(packSize, ProgressGoal > 0 ? ProgressGoal - SpawnPoints : Budget - Spawned), activeLimit - Monsters.Count));
        public bool RecordKill(uint id)
        {
            Bodies.Remove(id);
            if (!Monsters.Remove(id)) return false;
            Kills++;
            if (MonsterPoints.Remove(id, out int points)) KillPoints += points;
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

    public void Load(IModuleContext module)
    {
        context = module;
        module.Lifetime.Own(new Cleanup(() => { runs.Clear(); views.Clear(); }));
        GameEvents.MapOpening.Add(module.Manifest.Id, request => SimulacrumRules.Resolve(request, Random.Shared));
        GameEvents.AreaHeader.Add(module.Manifest.Id, player => SimulacrumRules.IsArena(player.Layout.Id)
            ? new AreaHeader(null, null) { Level = (byte)SimulacrumRules.Level(State(player).Wave == 0 ? 1 : State(player).Wave) } : null);
        GameEvents.PlayerEntered.Add(module.Manifest.Id, Enter);
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
        GameEvents.AreaTick.Add(module.Manifest.Id, start => !SimulacrumRules.IsArena(start.Layout.Id) ? null : async (player, _) =>
        {
            if (!Current(player)) return;
            if (ViewOf(player) is not { Native: true }) await Enter(player);
            if (player.Paused || player.PlayerLife == 0
                || !ReferenceEquals(Clients(player).Where(c => c.PlayerLife > 0).MinBy(c => c.Id), player)) return;
            await Step(player);
        });
        GameEvents.ObjectUse.Add(module.Manifest.Id, async (player, use, requestedAt) =>
        {
            if (!SimulacrumRules.IsArena(player.Layout.Id) || ViewOf(player) is not { } view || use.TargetId != view.Entity) return false;
            if (await WorldOf(player).WalkUpTo(view.At.X, view.At.Y, 12, requestedAt)) await Start(player);
            return true;
        });
        GameEvents.NpcTalk.Add(module.Manifest.Id, async (player, id, _) =>
        {
            if (!SimulacrumRules.IsArena(player.Layout.Id) || ViewOf(player) is not { } v || v.Entity != id) return false;
            await Start(player); return true;
        });
        GameEvents.Drops.Add(module.Manifest.Id, (Action<DropRoll>)(drop => { if (SimulacrumRules.IsArena((string)drop.Player.Layout.Id)) { drop.Cancel = true; drop.Gold = 0; } }));
        GameEvents.Experience.Add(module.Manifest.Id, (Action<ExperienceGain>)(gain =>
        {
            if (SimulacrumRules.IsArena((string)gain.Player.Layout.Id))
                gain.Experience = (uint)Math.Min(uint.MaxValue, (ulong)((ulong)gain.Experience * (uint)(100 + Math.Min(300, (int)(Math.Max(0, (int)(State((Player)gain.Player).Wave - 1)) * 100 / 3))) / 100));
        }));
        GameEvents.Kill.Add(module.Manifest.Id, kill =>
        {
            if (!SimulacrumRules.IsArena(kill.Player.Layout.Id)) return;
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
        });
    }

    Run State(Player player) => runs.GetValue(player.Instance, instance =>
        instance.ContentState.TryGetValue(StateKey, out var text) ? JsonSerializer.Deserialize<Run>(text, serialization) ?? new() : new());
    void Save(Player player, Run run) => player.Instance.ContentState[StateKey] = JsonSerializer.Serialize(run, serialization);
    static bool Current(Player player) => player.Connection.PlayerSent && !player.LeavingArea && !player.Closed;
    static IEnumerable<Player> Clients(Player player) => InGame.Keys.Where(c => !c.Controlled && ReferenceEquals(c.Instance, player.Instance) && Current(c));
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
        var stashAt = player.Layout.Near((player.Layout.SpawnX - 15, player.Layout.SpawnY + 8));
        uint stash = player.TakeObjectId(); view.Stash = stash;
        await SendWorldCreate(player, stash, () => InstanceEntities.CreateObject(stash,
            InstanceEntities.ObjectTypeHash(StashObject.MetadataPath), new StashObject(stashAt.X, stashAt.Y, 0).Blob()),
                "Simulacrum: stash between waves", wake: true, awaitBirth: false);
        player.StashObject = stash; player.StashAt = stashAt; global::Game.SetAreaStash(player, stash, stashAt);
        }
        SaveView(player, view);
        var run = State(player);
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
            // A preview retained across this module upgrade keeps its affixes, but adopts the new population model.
            if (prepared.ProgressGoal == 0) prepared.ProgressGoal = WaveProgressGoal(run.Wave + 1);
            if (!Current(player) || player.PlayerLife == 0) return;
            // No automatic advance; this is the only transition into a new wave.
            run.Wave++; run.Active = true; run.Rewarded = false; run.Spawned = run.Kills = 0;
            run.Budget = prepared.Budget; run.Rewards = prepared.Rewards; run.Modifiers = prepared.Modifiers;
            run.ProgressGoal = prepared.ProgressGoal; run.SpawnPoints = run.KillPoints = 0; run.MonsterPoints.Clear();
            run.Affixes = prepared.Affixes; run.Next = null;
            run.ModifierNames = prepared.ModifierNames; run.OmniThisWave = prepared.OmniThisWave; run.KosisThisWave = prepared.KosisThisWave;
            run.Monsters.Clear(); run.Bodies.Clear(); run.Started = AreaTime(player); run.NextSpawnAt = run.Started;
            cleared = ClearGround(player.Instance.Ground);
            // Every client hears of it: the removals are the area's records.
            shown.AddRange(cleared);
            Save(player, run);
            if (cleared.Count > 0) Console.WriteLine($"[gateway] Simulacrum wave {run.Wave}: {cleared.Count} items on the ground destroyed");
            foreach (uint id in shown)
                await SendWorldRemoval(player, id, $"0x02bd remove world item 0x{id:x} (Simulacrum wave {run.Wave} begins)");
            await Broadcast(player, $"Simulacrum — Wave {run.Wave}/15 · Monster level {SimulacrumRules.Level(run.Wave)} · {SimulacrumRules.Delirious(run.Wave)}% Delirious\n"
                + string.Join("; ", run.ModifierNames));
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

    async Task Step(Player player)
    {
        var run = State(player);
        if (!context.Lifetime.IsActive || !Current(player) || !run.Active || run.Busy) return;
        run.Busy = true;
        foreach (uint id in run.Monsters.ToArray())
        {
            // Bodies is not saved: after a module reload it is learnt again from the list here, so a later
            // corpse-consuming kill is still matched.
            if (player.Area.Monsters.TryGetValue(id, out var monster)) run.Bodies.TryAdd(id, monster);
            if (monster is not null ? monster.Dead || monster.Life == 0
                : run.Bodies.TryGetValue(id, out var body) && (body.Dead || body.Life == 0)) run.RecordKill(id);
        }
        try
        {
            if (run.VisualVersion == 0) await RevealExisting(player, run);
            if (!run.SpawningDone) await SpawnBatch(player, run);
            if (run.SpawningDone && run.Monsters.Count == 0) await Complete(player, run);
        }
        finally { run.Busy = false; }
    }

    static async Task Broadcast(Player player, string text)
    {
        foreach (var peer in Clients(player))
            await PlayerOf(peer).Tell(text);
    }
}
