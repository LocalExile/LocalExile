using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

// NPCTalk scripts (Agents/docs/protocol/17-npcs.md, campaign-quests.md E5): what a dialogue row's Script / Script2 does
// beyond its flags (those are NpcCatalog.Talk.ScriptSets / ScriptClears, set by OnQuestTalk). Parsed by NpcCatalog.Talk:
// - DirtyTransition() on the NPC itself and ForEachOfType / ForEachInRange+IfType Transition() on objects (world changes,
//   run when the row is chosen);
// - DoNPCChat(n), FinishNPCChat(), SendOpenScreen(screen), AreaTransitionTo(area, index, delay) (+ FadeToBlack in
//   Script2): what happens when the row's talk ends (or at once for a row without text).
static partial class Game
{
    // S→C 0x7b kinds of the screens the scripts open (the client's screen table at 0x142fc5920, campaign/pantheon.md §3).
    internal static readonly Dictionary<string, byte> ScriptScreens = new(StringComparer.Ordinal) { ["PantheonScreen"] = 0x14 };

    /// <summary>Whether the row does something when its talk ends (or at once without a talk).</summary>
    internal static bool EndsWithScript(NpcCatalog.Talk talk) =>
        talk.NextChat is not null || talk.FinishesChat || talk.AreaTransition is not null || talk.OpensScreen is not null;

    // The row's world changes, when it is chosen: the NPC's own Transitionable steps on (Dialla withers, NPCTalk 534) and the
    // objects of the named types transition (Quests.OnScriptTransition: transitions a quest shows, objects it changes).
    internal static async Task RunTalkWorldScript(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint npcId,
        SpawnedNpc npc, NpcCatalog.Talk talk)
    {
        if (talk.TransitionsSelf) await StepNpcTransition(remote, stream, ciphers, conn, npcId, npc, $"{talk.Option} (NPCTalk {talk.Row})");
        foreach (string type in talk.TransitionsTypes)
            await TransitionObjectsOfType(remote, stream, ciphers, conn, type, $"{npc.Npc.Name}: {talk.Option} (NPCTalk {talk.Row})");
    }

    // An NPC's Transitionable one state further (no further than its last state).
    internal static async Task StepNpcTransition(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint npcId, SpawnedNpc npc, string why)
    {
        int component = npc.Npc.Components?.ToList().IndexOf("Transitionable") ?? -1;
        if (component < 0) return;
        byte last = NpcTransitionStates(npc.Npc);
        if (npc.State >= last) return;
        npc.State++;
        await Send(remote, stream, ciphers, InstanceEntities.TransitionableUpdate(npcId, (byte)component, npc.State),
            $"0x02ba npc 0x{npcId:x} {npc.Npc.Name} transitions to state {npc.State} ({why})");
    }

    // The NPC type's Transitionable num_states (object-types.json; 2 when the type does not say).
    internal static byte NpcTransitionStates(NpcCatalog.Npc npc) => npc.TransitionStates is > 1 and var n ? n : (byte)2;

    // Every object of the type in this instance steps on: NPCs (their Transitionable), and what the quests manage
    // (QuestScriptObjects: quest transitions shown, quest objects' states).
    internal static async Task TransitionObjectsOfType(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string type, string why)
    {
        List<(uint Id, SpawnedNpc Npc)> npcs;
        lock (conn.Npcs) npcs = [.. conn.Npcs.Where(n => n.Value.Npc.Id == type).Select(n => (n.Key, n.Value))];
        foreach (var (id, npc) in npcs) await StepNpcTransition(remote, stream, ciphers, conn, id, npc, why);
        await TransitionQuestObjects(remote, stream, ciphers, conn, type, why);
    }

    // A script's Transition of a quest type: an area transition of that tile type that its quest flag still hides opens on
    // this instance (Dialla's 521 → AscentTransition, 535 → RaptureTransition; the row's own flag opens it for good,
    // Quests.ExitRule), and the quest objects the engine created of that type step on (QuestObjectTransition).
    internal static async Task TransitionQuestObjects(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string type, string why)
    {
        bool opened = false;
        foreach (var (id, transition) in conn.Layout.AreaTransitions.Where(t => t.Transition.SourceType == type))
            opened |= conn.ScriptShownTransitions.Add(id);
        if (opened) await UpdateShownTransitions(remote, stream, ciphers, conn, why);
        await QuestObjectTransition(remote, stream, ciphers, conn, type, why);
    }

    /// <summary>When a row's talk ends (C→S 0x85 after it, or at once for a row without text): its next page
    /// (DoNPCChat), or the end of the dialogue (FinishNPCChat) with the screen it opens or the area it moves to. True when
    /// the script decided what comes next (the option list does not come back).</summary>
    internal static async Task<bool> EndTalkScript(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint npcId,
        SpawnedNpc npc, NpcCatalog.Talk talk)
    {
        if (talk.NextChat is int next && Npcs.Value.TalkOf(npc.Npc, next) is { } page)
        {
            conn.InNpcTalk = true;
            conn.NpcFinishFlag = page.FinishFlag;
            conn.NpcTalkScript = EndsWithScript(page) ? page : null;
            if (TalkStartFlag(conn, page) is { } start) await SetQuestFlags(remote, stream, ciphers, conn, [start]);
            await OnQuestTalk(remote, stream, ciphers, conn, npcId, npc, page);
            await Send(remote, stream, ciphers, InstanceNpcs.Talk(npcId, (byte)page.Index),
                $"0x0084 npc 0x{npcId:x} {npc.Npc.Name} talks on: NPCTalk row {page.Row} (index {page.Index}, DoNPCChat)");
            return true;
        }
        if (!talk.FinishesChat && talk.AreaTransition is null && talk.OpensScreen is null) return false;
        conn.TalkingTo = 0;
        conn.InNpcTalk = false;
        conn.NpcFinishFlag = null;
        conn.NpcTalkScript = null;
        await Send(remote, stream, ciphers, InstanceNpcs.Talk(0), $"0x0084 object 0: {npc.Npc.Name}'s dialogue ends (FinishNPCChat, NPCTalk {talk.Row})");
        if (talk.OpensScreen is { } screen)
        {
            if (ScriptScreens.TryGetValue(screen, out byte kind))
                await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(kind), $"0x007b open {screen} (NPCTalk {talk.Row})");
            else Console.WriteLine($"[gateway] {remote} NPCTalk {talk.Row}: SendOpenScreen({screen}) is not mapped");
        }
        if (talk.AreaTransition is var (area, index, delay))
        {
            if (AreaTransitionCatalog.HashOf(area) is not { } hash)
            {
                Console.WriteLine($"[gateway] {remote} NPCTalk {talk.Row}: AreaTransitionTo({area}) is not in data/area-transitions.json");
                return true;
            }
            // The fade (Script2 FadeToBlack) is the client's; the move follows after the script's delay.
            if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(Math.Min(delay, 5)));
            await ChangeArea(remote, stream, ciphers, conn, new PendingTransfer(hash, 0, false, AtSpawn: true),
                $"{npc.Npc.Name}: {talk.Option} (AreaTransitionTo {area}, {index})");
        }
        return true;
    }

    // The quest objects the engine created of this type (Quests.ObjectRule) go to their last Transitionable state on this
    // instance (the Rapture Device, NPCTalk 534); their rule's own flag keeps that state on later visits.
    internal static async Task QuestObjectTransition(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string type, string why)
    {
        List<(uint Id, QuestObjectTypes.Info Info)> objects;
        lock (conn.QuestObjects) objects = [.. conn.QuestObjects.Where(o => o.Value.Rule.ObjectType == type).Select(o => (o.Key, o.Value.Info))];
        foreach (var (id, info) in objects)
            if (GameServer.Core.Protocol.Instance.QuestObject.TransitionableIndex(info.Components) is int index and >= 0)
                await Send(remote, stream, ciphers, InstanceEntities.TransitionableUpdate(id, (byte)index, (byte)info.States),
                    $"0x02ba quest object 0x{id:x} {type.Split('/')[^1]} Transitionable → {info.States} ({why})");
    }
}
