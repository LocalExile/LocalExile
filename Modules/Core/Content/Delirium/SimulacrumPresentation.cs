using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Delirium;

public sealed partial class DeliriumModule
{
    // AfflictionMinionBase's create_state_visible_0 disables rendering, independently of life and AI.
    public static StateMachineData? MonsterStates(StateMachineData? states) => states?.Set("visible", 1);

    async Task RevealExisting(ClientState conn, Run run)
    {
        var updates = new List<(uint Id, byte Component, StateMachineData States)>();
        lock (conn.Monsters)
        {
            foreach (uint id in run.Monsters)
            {
                if (!conn.Monsters.TryGetValue(id, out var monster) || monster.Dead || monster.Life == 0
                    || monster.States is not { } states || monster.Variety is not { } variety) continue;
                int visible = states.IndexOf("visible"), component = variety.ComponentsOrBase.ToList().IndexOf("StateMachine");
                if (visible < 0 || component < 0 || states.SharedStates[visible] == 1) continue;
                monster.States = MonsterStates(states)!;
                monster.Aggro = true;
                if (conn.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
                    conn.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { StateMachine = monster.States } };
                updates.Add((id, (byte)component, monster.States));
            }
            run.VisualVersion = 1;
            Save(conn, run);
        }
        foreach (var peer in Clients(conn))
            foreach (var update in updates)
            {
                if (!peer.MonsterViews.ContainsKey(update.Id)) continue;
                var wire = peer.Wire;
                await SendWorldRecord(wire.Remote, wire.Stream, wire.Ciphers, peer, update.Id,
                    InstanceEntities.StateMachineUpdate(update.Id, update.Component, update.States), null);
            }
        if (updates.Count > 0) context.Log($"Simulacrum {conn.InstanceNumber}: revealed {updates.Count} existing monsters");
    }

    // Afflictionator.ot's wire components and shared states (the sixth, non-shared state selects its native prompt).
    public static readonly string[] StarterComponents = ["Positioned", "Animated", "StateMachine", "MinimapIcon", "Stats", "ObjectMagicProperties", "InteractionAction"];
    public static string StarterType(string area) => area switch
    {
        "AfflictionTown6" => "Metadata/Terrain/Leagues/Affliction/TownArena/Objects/AfflictionatorAct6",
        "AfflictionTown4" => "Metadata/Terrain/Leagues/Affliction/Act4_DeliriumTown/Objects/AfflictionatorAct4",
        "AfflictionTown7" => "Metadata/Terrain/Leagues/Affliction/Act7_DeliriumTown/Objects/AfflictionatorAct7",
        "AfflictionTown8" => "Metadata/Terrain/Leagues/Affliction/Act8_DeliriumTown/Objects/AfflictionatorAct8",
        "AfflictionTown10" => "Metadata/Terrain/Leagues/Affliction/Act10_DeliriumTown/Objects/AfflictionatorAct10",
        _ => throw new ArgumentException("Not a Simulacrum arena", nameof(area)),
    };
    public static StateMachineData StarterStates(Run run) => new(new int[]
        { run.Active ? 1 : 0, run.Active ? Math.Max(0, run.Wave - 1) : Math.Min(14, run.Wave), 0,
          run.Wave == 15 && run.Rewarded ? 1 : 0, run.Wave == 15 && run.Rewarded ? 1 : 0 }, 0,
        new[] { "active", "wave", "hello", "goodbye", "play_interlude" }) { Overridden = new HashSet<int> { 0, 1, 2, 3, 4 } };

    Run? Preview(ClientState conn, Run run)
    {
        if (run.Active || run.Wave >= 15) return null;
        if (run.Next is not null) return run.Next;
        var next = PrepareWave(conn, run.Wave + 1, run);
        lock (conn.Monsters) { run.Next ??= next; Save(conn, run); return run.Next; }
    }

    void SaveView(ClientState conn, View view) => conn.Instance.ContentState[$"{StateKey}.view.{conn.PeerId}"] =
        $"{view.Entity},{view.Stash},{view.At.X},{view.At.Y},{(view.Native ? 1 : 0)}";

    async Task CreateStarter(ClientState conn, View view, Run run)
    {
        var w = conn.Wire;
        if (view.Entity != 0)
        {
            lock (conn.Monsters) { conn.Monsters.Remove(view.Entity); conn.MinionOrigins.TryRemove(view.Entity, out _); }
            await SendWorldRemoval(w.Remote, w.Stream, w.Ciphers, conn, view.Entity, "Replace Simulacrum initiator");
        }
        string type = StarterType(conn.Layout.Id);
        var placed = conn.Layout.Level?.TileQuestObjects.FirstOrDefault(o => o.Type.EndsWith(type.Split('/')[^1], StringComparison.OrdinalIgnoreCase));
        var at = placed is null ? conn.Layout.Near((conn.Layout.SpawnX + 18, conn.Layout.SpawnY + 12)) : (placed.X, placed.Y);
        var preview = Preview(conn, run);
        var obj = new QuestObject(at.Item1, at.Item2, StarterComponents, Rotation: placed?.Rotation ?? 0)
        { StateMachine = StarterStates(run), Mods = preview?.Affixes ?? run.Affixes };
        uint id = conn.TakeObjectId();
        await SendWorldCreate(w.Remote, w.Stream, w.Ciphers, conn, id, InstanceEntities.CreateObject(id,
            InstanceEntities.ObjectTypeHash(placed?.Type ?? type), obj.Blob()), $"Simulacrum native starter {type} at {at}, wave {run.Wave}, active {run.Active}");
        await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, conn, id, InstanceProtocol.EnterWakeZone(id), null);
        view.Entity = id; view.At = at; view.Native = true;
        SaveView(conn, view);
    }

    async Task PublishStarter(ClientState conn, Run run, bool recreate)
    {
        foreach (var peer in Clients(conn))
        {
            if (ViewOf(peer) is not { } view) continue;
            if (!view.Native || recreate) { await CreateStarter(peer, view, run); continue; }
            var w = peer.Wire;
            await SendWorldRecord(w.Remote, w.Stream, w.Ciphers, peer, view.Entity,
                InstanceEntities.StateMachineUpdate(view.Entity, 2, StarterStates(run)), $"Simulacrum initiator wave {run.Wave}/15, active={run.Active}");
        }
    }
}
