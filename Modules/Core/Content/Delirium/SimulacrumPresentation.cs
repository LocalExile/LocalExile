using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Delirium;

public sealed partial class DeliriumModule
{
    // AfflictionMinionBase's create_state_visible_0 disables rendering, independently of life and AI.
    public static StateMachineData? MonsterStates(StateMachineData? states) => states?.Set("visible", 1);

    async Task RevealExisting(Player player, Run run)
    {
        var updates = new List<(uint Id, byte Component, StateMachineData States)>();
        foreach (uint id in run.Monsters)
        {
            if (!player.Area.Monsters.TryGetValue(id, out var monster) || monster.Dead || monster.Life == 0
                || monster.States is not { } states || monster.Variety is not { } variety) continue;
            int visible = states.IndexOf("visible"), component = variety.ComponentsOrBase.ToList().IndexOf("StateMachine");
            if (visible < 0 || component < 0 || states.SharedStates[visible] == 1) continue;
            monster.States = MonsterStates(states)!;
            monster.Aggro = true;
            if (player.Instance.World.MonsterOrigins.TryGetValue(id, out var origin))
                player.Instance.World.MonsterOrigins[id] = origin with { Object = origin.Object with { StateMachine = monster.States } };
            updates.Add((id, (byte)component, monster.States));
        }
        run.VisualVersion = 1;
        Save(player, run);
        foreach (var peer in Clients(player))
            foreach (var update in updates)
            {
                if (!MonsterShown(peer, update.Id)) continue;
                await SendWorldRecord(peer, update.Id,
                    InstanceEntities.StateMachineUpdate(update.Id, update.Component, update.States), null);
            }
        if (updates.Count > 0) context.Log($"Simulacrum {player.InstanceNumber}: revealed {updates.Count} existing monsters");
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

    Run? Preview(Player player, Run run)
    {
        if (run.Active || run.Wave >= 15) return null;
        if (run.Next is not null) return run.Next;
        var next = PrepareWave(player, run.Wave + 1, run);
        run.Next ??= next; Save(player, run); return run.Next;
    }

    void SaveView(Player player, View view) => player.Instance.ContentState[$"{StateKey}.view.{player.Id}"] =
        $"{view.Entity},{view.Stash},{view.At.X},{view.At.Y},{(view.Native ? 1 : 0)}";

    async Task CreateStarter(Player player, View view, Run run)
    {
        if (view.Entity != 0)
        {
            player.Area.Monsters.Remove(view.Entity); player.MinionOrigins.TryRemove(view.Entity, out _);
            await SendWorldRemoval(player, view.Entity, "Replace Simulacrum initiator");
        }
        string type = StarterType(player.Layout.Id);
        var placed = player.Layout.Level?.TileQuestObjects.FirstOrDefault(o => o.Type.EndsWith(type.Split('/')[^1], StringComparison.OrdinalIgnoreCase));
        var at = placed is null ? player.Layout.Near((player.Layout.SpawnX + 18, player.Layout.SpawnY + 12)) : (placed.X, placed.Y);
        var preview = Preview(player, run);
        var obj = new QuestObject(at.Item1, at.Item2, StarterComponents, Rotation: placed?.Rotation ?? 0)
        { StateMachine = StarterStates(run), Mods = preview?.Affixes ?? run.Affixes };
        uint id = player.TakeObjectId();
        await SendWorldCreate(player, id, () => InstanceEntities.CreateObject(id,
            InstanceEntities.ObjectTypeHash(placed?.Type ?? type), obj.Blob()),
                $"Simulacrum native starter {type} at {at}, wave {run.Wave}, active {run.Active}", wake: true, awaitBirth: false);
        view.Entity = id; view.At = at; view.Native = true;
        SaveView(player, view);
    }

    async Task PublishStarter(Player player, Run run, bool recreate)
    {
        foreach (var peer in Clients(player))
        {
            if (ViewOf(peer) is not { } view) continue;
            if (!view.Native || recreate) { await CreateStarter(peer, view, run); continue; }
            await SendWorldRecord(peer, view.Entity,
                InstanceEntities.StateMachineUpdate(view.Entity, 2, StarterStates(run)), $"Simulacrum initiator wave {run.Wave}/15, active={run.Active}");
        }
    }
}
