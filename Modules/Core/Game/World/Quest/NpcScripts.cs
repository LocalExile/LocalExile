using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

// NPCTalk scripts (Agents/docs/protocol/17-npcs.md, campaign-quests.md E5): what a dialogue row's Script / Script2 does
// beyond its flags (those are NpcCatalog.Talk.ScriptSets / ScriptClears, set by OnQuestTalk). Parsed by NpcCatalog.Talk:
// - DirtyTransition() on the NPC itself and ForEachOfType / ForEachInRange+IfType Transition() on objects (world changes,
//   run when the row is chosen);
// - DoNPCChat(n), FinishNPCChat(), SendOpenScreen(screen), AreaTransitionTo(area, index, delay) (+ FadeToBlack in
//   Script2): what happens when the row's talk ends (or at once for a row without text).
public static partial class Game
{
    // S→C 0x7b kinds of the screens the scripts open (the client's screen table at 0x142fcd700 / 0x14216bb74).
    public static readonly Dictionary<string, byte> ScriptScreens = new(StringComparer.Ordinal)
    {
        ["PantheonScreen"] = 0x14,
        ["KalguurRecombinatorWindow"] = InstanceVillage.RecombinatorWindowUi,
        ["KalguurRecruitmentWindow"] = InstanceVillage.RecruitmentWindowUi,
        ["KalguurTownMapWindow"] = InstanceVillage.TownMapWindowUi,
        ["KalguurMineWindow"] = InstanceVillage.MineWindowUi,
        ["KalguurPortWindow"] = InstanceVillage.PortWindowUi,
        ["KalguurFarmWindow"] = InstanceVillage.FarmWindowUi,
        ["KalguurBlackSmithWindow"] = InstanceVillage.BlackSmithWindowUi,
        ["KalguurDisenchantingWindow"] = InstanceVillage.DisenchantingWindowUi,
        ["KalguurMapRunnersWindow"] = InstanceVillage.MapRunnersWindowUi,
        ["KalguurFishingWindow"] = InstanceVillage.FishingWindowUi,
    };

    /// <summary>Whether the row does something when its talk ends (or at once without a talk).</summary>
    public static bool EndsWithScript(NpcCatalog.Talk talk) =>
        talk.NextChat is not null || talk.FinishesChat || talk.AreaTransition is not null || talk.OpensScreen is not null || talk.GoToKingsmarch;

    // The row's world changes, when it is chosen: the NPC's own Transitionable steps on (Dialla withers, NPCTalk 534) and the
    // objects of the named types transition (Quests.OnScriptTransition: transitions a quest shows, objects it changes).
    public static async Task RunTalkWorldScript(Player player, uint npcId,
        SpawnedNpc npc, NpcCatalog.Talk talk)
    {
        if (talk.TransitionsSelf || talk.IncrementsCounter)
            await StepNpcTransition(player, npcId, npc, $"{talk.Option} (NPCTalk {talk.Row})");
        if (talk.StartsTimer && npc.Npc.Id == "Metadata/NPC/Act3/Fairgraves" && !ClientHasQuestFlag(player, "AudioEventFairgraves2Dead"))
        {
            // Fairgraves.ot Timer fires IncrementCounter(2) after 23.3 s if the player stays in The Docks without clicking Farewell.
            var instance = player.Instance;
            _ = Task.Run(async () =>
            {
                try
                {
                    await AreaDelay(player, 23.3);
                    if (player.Closed || player.LeavingArea || !ReferenceEquals(player.Instance, instance)
                        || ClientHasQuestFlag(player, "AudioEventFairgraves2Dead")) return;
                    await SetQuestFlags(player, ["AudioEventFairgraves2Dead"]);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
            });
        }
        foreach (string type in talk.TransitionsTypes)
            await TransitionObjectsOfType(player, type, $"{npc.Npc.Name}: {talk.Option} (NPCTalk {talk.Row})");
    }

    // An NPC's Transitionable one state further (no further than its last state).
    public static async Task StepNpcTransition(Player player, uint npcId, SpawnedNpc npc, string why)
    {
        int component = npc.Npc.Components?.ToList().IndexOf("Transitionable") ?? -1;
        if (component < 0) return;
        byte last = NpcTransitionStates(npc.Npc);
        if (npc.State >= last) return;
        npc.State++;
        await SendWorldRecord(player, npcId, InstanceEntities.TransitionableUpdate(npcId, (byte)component, npc.State),
            $"0x02bc npc 0x{npcId:x} {npc.Npc.Name} transitions to state {npc.State} ({why})");
    }

    // The NPC type's Transitionable num_states (object-types.json; 2 when the type does not say).
    public static byte NpcTransitionStates(NpcCatalog.Npc npc) => npc.TransitionStates is > 1 and var n ? n : (byte)2;

    // Every object of the type in this instance steps on: NPCs (their Transitionable), and what the quests manage
    // (QuestScriptObjects: quest transitions shown, quest objects' states).
    public static async Task TransitionObjectsOfType(Player player, string type, string why)
    {
        List<(uint Id, SpawnedNpc Npc)> npcs;
        lock (player.Npcs) npcs = [.. player.Npcs.Where(n => n.Value.Npc.Id == type).Select(n => (n.Key, n.Value))];
        foreach (var (id, npc) in npcs) await StepNpcTransition(player, id, npc, why);
        await TransitionQuestObjects(player, type, why);
    }

    // A script's Transition of a quest type: an area transition of that tile type that its quest flag still hides opens on
    // this instance (Dialla's 521 → AscentTransition, 535 → RaptureTransition; the row's own flag opens it for good,
    // Quests.ExitRule), and the quest objects the engine created of that type step on (QuestObjectTransition).
    public static async Task TransitionQuestObjects(Player player, string type, string why)
    {
        bool opened = false;
        foreach (var (id, transition) in player.Layout.AreaTransitions.Where(t => t.Transition.SourceType == type))
            opened |= player.Connection.ScriptShownTransitions.Add(id);
        if (opened) await UpdateShownTransitions(player, why);
        await QuestObjectTransition(player, type, why);
    }

    public static async Task ApplyQuestConversationStart(Player player, uint npcId, SpawnedNpc npc, NpcCatalog.Talk talk, bool applyRowRules)
    {
        if (TalkStartFlag(player, talk) is { } start) await SetQuestFlags(player, [start]);
        if (!applyRowRules && talk.StartLeagueFlag is int startLeague && GameEvents.UsesNpcLeagueFlags(player, npc.Npc))
        {
            bool has = player.Character.LeagueQuestFlags.Contains(startLeague);
            if (!has) await SetLeagueQuestFlags(player, [startLeague]);
        }
        if (applyRowRules && QuestsOf(player).OnTalkRow(talk.Row) is { Count: > 0 } byRow)
            await SetQuestFlags(player, byRow);
        await OnQuestTalk(player, npcId, npc, talk);
    }

    /// <summary>When a row's talk ends (C→S 0x85 after it, or at once for a row without text): its next page
    /// (DoNPCChat), or the end of the dialogue (FinishNPCChat) with the screen it opens or the area it moves to. True when
    /// the script decided what comes next (the option list does not come back).</summary>
    public static async Task<bool> EndTalkScript(Player player, uint npcId,
        SpawnedNpc npc, NpcCatalog.Talk talk)
    {
        if (await BanditChoices.OpenVote(player, npcId, npc, talk)) return true;
        if (talk.NextChat is int next && Npcs.TalkOf(npc.Npc, next) is { } page)
        {
            player.InNpcTalk = true;
            player.NpcFinishFlag = page.FinishFlag;
            player.NpcFinishLeagueFlag = GameEvents.UsesNpcLeagueFlags(player, npc.Npc) ? page.FinishLeagueFlag : null;
            player.NpcTalkScript = EndsWithScript(page) ? page : null;
            await ApplyQuestConversationStart(player, npcId, npc, page, applyRowRules: false);
            await Send(player, InstanceNpcs.Talk(npcId, (byte)page.Index),
                $"0x0084 npc 0x{npcId:x} {npc.Npc.Name} talks on: NPCTalk row {page.Row} (index {page.Index}, DoNPCChat)");
            return true;
        }
        if (!talk.FinishesChat && talk.AreaTransition is null && talk.OpensScreen is null && !talk.GoToKingsmarch) return false;
        player.TalkingTo = 0;
        player.InNpcTalk = false;
        player.NpcFinishFlag = null;
        player.NpcFinishLeagueFlag = null;
        player.NpcTalkScript = null;
        await Send(player, InstanceNpcs.Talk(0), $"0x0084 object 0: {npc.Npc.Name}'s dialogue ends (FinishNPCChat, NPCTalk {talk.Row})");
        if (await GameEvents.RunNpcTalkScript(player, npcId, npc, talk)) return true;
        if (talk.GoToKingsmarch)
        {
            if (AreaTransitionCatalog.HashOf(WaypointCatalog.Kingsmarch) is { } kingsmarch)
                await ChangeArea(player, new PendingTransfer(kingsmarch, 0, false, AtSpawn: true),
                    $"{npc.Npc.Name}: {talk.Option} (GoToKingsmarch)");
            else Console.WriteLine($"[gateway] {player.Connection.Remote} NPCTalk {talk.Row}: GoToKingsmarch() area {WaypointCatalog.Kingsmarch} is not in client.db's area_transitions");
            return true;
        }
        if (talk.OpensScreen is { } screen)
        {
            if (ScriptScreens.TryGetValue(screen, out byte kind))
                await Send(player, InstanceWaypoints.OpenUi(kind), $"0x007b open {screen} (NPCTalk {talk.Row})");
            else Console.WriteLine($"[gateway] {player.Connection.Remote} NPCTalk {talk.Row}: SendOpenScreen({screen}) is not mapped");
        }
        if (talk.AreaTransition is var (area, index, delay))
        {
            if (AreaTransitionCatalog.HashOf(area) is not { } hash)
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} NPCTalk {talk.Row}: AreaTransitionTo({area}) is not in client.db's area_transitions");
                return true;
            }
            // The fade (Script2 FadeToBlack) is the client's; the move follows after the script's delay.
            if (delay > 0) await AreaDelay(player, Math.Min(delay, 5));
            bool useShoreEntrance = player.Layout.Id == KitavaActTen.Docks && area == KitavaActTen.Epilogue;
            await ChangeArea(player, new PendingTransfer(hash, 0, false, AtSpawn: !useShoreEntrance),
                $"{npc.Npc.Name}: {talk.Option} (AreaTransitionTo {area}, {index})");
        }
        return true;
    }

    // The quest objects the engine created of this type (Quests.ObjectRule) go to their last Transitionable state on this
    // instance (the Rapture Device, NPCTalk 534); their rule's own flag keeps that state on later visits.
    public static async Task QuestObjectTransition(Player player, string type, string why)
    {
        List<(uint Id, QuestObjectTypes.Info Info)> objects;
        lock (player.QuestObjects) objects = [.. player.QuestObjects.Where(o => o.Value.Rule.ObjectType == type).Select(o => (o.Key, o.Value.Info))];
        foreach (var (id, info) in objects)
            if (GameServer.Core.Protocol.Instance.QuestObject.TransitionableIndex(info.Components) is int index and >= 0)
                await SendWorldRecord(player, id, InstanceEntities.TransitionableUpdate(id, (byte)index, (byte)info.States),
                    $"0x02bc quest object 0x{id:x} {type.Split('/')[^1]} Transitionable → {info.States} ({why})");
    }
}
