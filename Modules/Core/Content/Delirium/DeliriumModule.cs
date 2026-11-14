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
    readonly ConditionalWeakTable<ClientState, View> views = new();
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
        [System.Text.Json.Serialization.JsonIgnore] public bool SpawningDone => ProgressGoal > 0 ? SpawnPoints >= ProgressGoal : Spawned >= Budget;
        public int SpawnAllowance(double now, int packSize, int activeLimit) => !Active || now < NextSpawnAt ? 0
            : Math.Max(0, Math.Min(Math.Min(packSize, ProgressGoal > 0 ? ProgressGoal - SpawnPoints : Budget - Spawned), activeLimit - Monsters.Count));
        public bool RecordKill(uint id)
        {
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
        GameEvents.AreaHeader.Add(module.Manifest.Id, conn => SimulacrumRules.IsArena(conn.Layout.Id)
            ? new AreaHeader(null, null) { Level = (byte)SimulacrumRules.Level(State(conn).Wave == 0 ? 1 : State(conn).Wave) } : null);
        GameEvents.AreaGenerated.Add(module.Manifest.Id, Enter);
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (conn, why) =>
        {
            if (SimulacrumRules.IsArena(conn.Layout.Id))
            {
                if (ViewOf(conn) is { } v)
                    lock (conn.Monsters) { conn.Monsters.Remove(v.Entity); conn.MinionOrigins.TryRemove(v.Entity, out _); }
                conn.Instance.ContentState.TryRemove($"{StateKey}.view.{conn.PeerId}", out _);
            }
            views.Remove(conn);
            return Task.CompletedTask;
        });
        GameEvents.Tick.Add(module.Manifest.Id, async (conn, _) =>
        {
            if (!SimulacrumRules.IsArena(conn.Layout.Id) || !Current(conn)) return;
            if (ViewOf(conn) is not { Native: true }) await Enter(conn);
            if (conn.Paused || conn.PlayerLife == 0
                || !ReferenceEquals(Clients(conn).Where(c => c.PlayerLife > 0).MinBy(c => c.PeerId), conn)) return;
            await Step(conn);
        });
        GameEvents.ObjectUse.Add(module.Manifest.Id, async (conn, use, requestedAt) =>
        {
            if (!SimulacrumRules.IsArena(conn.Layout.Id) || ViewOf(conn) is not { } view || use.TargetId != view.Entity) return false;
            if (await WorldOf(conn).WalkUpTo(view.At.X, view.At.Y, 12, requestedAt)) await Start(conn);
            return true;
        });
        GameEvents.NpcTalk.Add(module.Manifest.Id, async (conn, id, _) =>
        {
            if (!SimulacrumRules.IsArena(conn.Layout.Id) || ViewOf(conn) is not { } v || v.Entity != id) return false;
            await Start(conn); return true;
        });
        GameEvents.Drops.Add(module.Manifest.Id, drop => { if (SimulacrumRules.IsArena(drop.Conn.Layout.Id)) { drop.Cancel = true; drop.Gold = 0; } });
        GameEvents.Experience.Add(module.Manifest.Id, gain =>
        {
            if (SimulacrumRules.IsArena(gain.Conn.Layout.Id))
                gain.Experience = (uint)Math.Min(uint.MaxValue, (ulong)gain.Experience * (uint)(100 + Math.Min(300, Math.Max(0, State(gain.Conn).Wave - 1) * 100 / 3)) / 100);
        });
        GameEvents.Kill.Add(module.Manifest.Id, kill =>
        {
            if (!SimulacrumRules.IsArena(kill.Conn.Layout.Id)) return;
            var run = State(kill.Conn);
            uint id = run.Monsters.FirstOrDefault(id => kill.Conn.Monsters.TryGetValue(id, out var m) && ReferenceEquals(m, kill.Monster));
            if (id == 0 || !run.RecordKill(id)) return;
            Save(kill.Conn, run);
            if (run.Active && run.SpawningDone && run.Monsters.Count == 0)
                kill.Later(() => Clients(kill.Conn).MinBy(c => c.PeerId) is { } live ? Step(live) : Task.CompletedTask);
        });
        GameEvents.StateBuilding.Add(module.Manifest.Id, (conn, json) =>
        {
            if (SimulacrumRules.IsArena(conn.Layout.Id))
                lock (conn.Monsters) json["simulacrum"] = JsonSerializer.SerializeToNode(State(conn), serialization);
        });
    }

    Run State(ClientState conn) => runs.GetValue(conn.Instance, instance =>
        instance.ContentState.TryGetValue(StateKey, out var text) ? JsonSerializer.Deserialize<Run>(text, serialization) ?? new() : new());
    void Save(ClientState conn, Run run) => conn.Instance.ContentState[StateKey] = JsonSerializer.Serialize(run, serialization);
    static bool Current(ClientState conn) => conn.PlayerSent && !conn.LeavingArea && !conn.Closed;
    static IEnumerable<ClientState> Clients(ClientState conn) => InGame.Keys.Where(c => !c.Controlled && ReferenceEquals(c.Instance, conn.Instance) && Current(c));
    View? ViewOf(ClientState conn)
    {
        if (views.TryGetValue(conn, out var view)) return view;
        if (!conn.Instance.ContentState.TryGetValue($"{StateKey}.view.{conn.PeerId}", out var text)) return null;
        var parts = text.Split(',');
        if (parts.Length is not (4 or 5) || !uint.TryParse(parts[0], out uint entity) || !uint.TryParse(parts[1], out uint stash)
            || !int.TryParse(parts[2], out int x) || !int.TryParse(parts[3], out int y)) return null;
        return views.GetValue(conn, _ => new View { Entity = entity, Stash = stash, At = (x, y), Native = parts.Length == 5 && parts[4] == "1" });
    }

    async Task Enter(ClientState conn)
    {
        if (!SimulacrumRules.IsArena(conn.Layout.Id)) return;
        var (remote, stream, ciphers) = conn.Wire;
        var view = ViewOf(conn) ?? views.GetOrCreateValue(conn);
        if (view.Native || view.Repairing) return;
        view.Repairing = true;
        try { await CreateStarter(conn, view, State(conn)); }
        finally { view.Repairing = false; }
        if (view.Stash == 0)
        {
        var stashAt = conn.Layout.Near((conn.Layout.SpawnX - 15, conn.Layout.SpawnY + 8));
        uint stash = conn.TakeObjectId(); view.Stash = stash;
        await SendWorldCreate(remote, stream, ciphers, conn, stash, InstanceEntities.CreateObject(stash,
            InstanceEntities.ObjectTypeHash(StashObject.MetadataPath), new StashObject(stashAt.X, stashAt.Y, 0).Blob()), "Simulacrum: stash between waves");
        await SendWorldRecord(remote, stream, ciphers, conn, stash, InstanceProtocol.EnterWakeZone(stash), null);
        conn.StashObject = stash; conn.StashAt = stashAt;
        }
        SaveView(conn, view);
        // A guest uses the run owner's origin too, rather than opening a portal to their own hideout.
        if (conn.ReturnPortal is null && MapDevice.InInstance(conn.Layout.Hash, conn.InstanceNumber)?.Map.ReturnTo is { } origin)
        {
            uint portal = conn.TakeObjectId();
            var p = conn.Layout.Near((conn.Layout.SpawnX, conn.Layout.SpawnY));
            await SendWorldCreate(remote, stream, ciphers, conn, portal, InstanceEntities.CreateObject(portal,
                InstanceEntities.ObjectTypeHash(MapPortalObject.MetadataPath), new MapPortalObject(p.X, p.Y, -1, MapPortalType, Open: 1).Blob()), "Simulacrum: return portal");
            await SendWorldRecord(remote, stream, ciphers, conn, portal, InstanceProtocol.EnterWakeZone(portal), null);
            conn.ReturnPortal = (portal, p.X, p.Y); conn.ReturnPortalTo = origin.Area;
            ListPortal(remote, stream, ciphers, conn, portal, origin.Area, "return from Simulacrum", MapPortalEntryMs);
        }
        var run = State(conn);
        await PlayerOf(conn).Tell(run.Wave == SimulacrumRules.Waves && run.Rewarded ? "Simulacrum complete. All 15 waves defeated."
            : run.Active ? $"Simulacrum: wave {run.Wave}/15 continues."
            : $"Simulacrum: interact with the familiar entity to begin wave {run.Wave + 1}/15. Collect your rewards before starting the next wave.");
    }

    async Task Start(ClientState conn)
    {
        var run = State(conn);
        if (run.Wave == SimulacrumRules.Waves && run.Rewarded)
        {
            var wire = conn.Wire;
            if (Descendancies.Find("Delirious") is { } bloodline)
                await OpenDescendancySelector(wire.Remote, wire.Stream, wire.Ciphers, conn, bloodline.Index);
            return;
        }
        lock (conn.Monsters)
        {
            if (!Current(conn) || conn.PlayerLife == 0 || run.Active || run.Busy || run.Wave >= SimulacrumRules.Waves) return;
            run.Busy = true;
        }
        try
        {
            var prepared = run.Next ?? PrepareWave(conn, run.Wave + 1, run);
            // A preview retained across this module upgrade keeps its affixes, but adopts the new population model.
            if (prepared.ProgressGoal == 0) prepared.ProgressGoal = WaveProgressGoal(run.Wave + 1);
            lock (conn.Monsters)
            {
                if (!Current(conn) || conn.PlayerLife == 0) return;
                // No automatic advance; this is the only transition into a new wave.
                run.Wave++; run.Active = true; run.Rewarded = false; run.Spawned = run.Kills = 0;
                run.Budget = prepared.Budget; run.Rewards = prepared.Rewards; run.Modifiers = prepared.Modifiers;
                run.ProgressGoal = prepared.ProgressGoal; run.SpawnPoints = run.KillPoints = 0; run.MonsterPoints.Clear();
                run.Affixes = prepared.Affixes; run.Next = null;
                run.ModifierNames = prepared.ModifierNames; run.OmniThisWave = prepared.OmniThisWave; run.KosisThisWave = prepared.KosisThisWave;
                run.Monsters.Clear(); run.Started = conn.Clock.Elapsed.TotalSeconds; run.NextSpawnAt = run.Started;
                // The real encounter destroys uncollected loot when another wave starts.
                foreach (var ground in conn.Instance.Ground.All()) conn.Instance.Ground.Take(ground.Id, ground.Item);
                Save(conn, run);
            }
            await Broadcast(conn, $"Simulacrum — Wave {run.Wave}/15 · Monster level {SimulacrumRules.Level(run.Wave)} · {SimulacrumRules.Delirious(run.Wave)}% Delirious\n"
                + string.Join("; ", run.ModifierNames));
            await PublishStarter(conn, run, recreate: false);
        }
        finally { lock (conn.Monsters) run.Busy = false; }
        await Step(conn);
    }

    async Task Step(ClientState conn)
    {
        var run = State(conn);
        lock (conn.Monsters)
        {
            if (!context.Lifetime.IsActive || !Current(conn) || !run.Active || run.Busy) return;
            run.Busy = true;
            foreach (uint id in run.Monsters.ToArray())
                if (conn.Monsters.TryGetValue(id, out var monster) && (monster.Dead || monster.Life == 0)) run.RecordKill(id);
        }
        try
        {
            if (run.VisualVersion == 0) await RevealExisting(conn, run);
            if (!run.SpawningDone) await SpawnBatch(conn, run);
            if (run.SpawningDone && run.Monsters.Count == 0) await Complete(conn, run);
        }
        finally { lock (conn.Monsters) run.Busy = false; }
    }

    static async Task Broadcast(ClientState conn, string text)
    {
        foreach (var peer in Clients(conn))
            await PlayerOf(peer).Tell(text);
    }
}
