using System.Net.Sockets;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

// NPCTalk scripts (Agents/docs/protocol/17-npcs.md, campaign-quests.md E5): what a dialogue row's Script / Script2 does
// beyond its flags (those are NpcCatalog.Talk.ScriptSets / ScriptClears, set by OnQuestTalk). Parsed by NpcCatalog.Talk:
// - DirtyTransition() on the NPC itself, ForEachOfType / ForEachInRange+IfType Transition() on objects, DropQuestItem(),
//   and SetMavenRegionFlag() (world changes, run when the row is chosen);
// - DoNPCChat(n), FinishNPCChat(), SendOpenScreen(screen[, arg]), AreaTransitionTo(area, index, delay) (+ FadeToBlack in
//   Script2), SendMavenDialog(id), SendTextAudio(audioId, npcId, range), PlayAnimation(clip), and DispatchRulesetEvent(ev):
//   what happens when the row's talk ends (or at once for a row without text).
public static partial class Game
{
    // S→C 0x7b kinds of the screens the scripts open (the client's screen table at 0x142fd89f0, campaign/pantheon.md §3).
    public static readonly Dictionary<string, byte> ScriptScreens = new(StringComparer.Ordinal)
    {
        ["PantheonScreen"] = 0x14,
        ["AtlasPassiveTreeScreen"] = InstanceWaypoints.AtlasUi,
        ["CharacterPassiveTreeScreen"] = InstanceNpcs.PassiveTreeUi,
        ["HideoutSelectionWindow"] = InstanceNpcs.HideoutSelectionUi,
        ["DelveMapScreen"] = 0x1c,
    };

    static Dictionary<string, int>? _npcTextAudioRows;
    static Dictionary<string, int>? _mavenDialogAudioRows;

    static int? FindNpcTextAudioRow(string audioId)
    {
        if (_npcTextAudioRows is null)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (ClientTables.Has("NPCTextAudio"))
            {
                int i = 0;
                foreach (var row in ClientTables.Table("NPCTextAudio"))
                {
                    if (row.Text("Id") is { Length: > 0 } id) map.TryAdd(id, i);
                    i++;
                }
            }
            _npcTextAudioRows = map;
        }
        return _npcTextAudioRows.TryGetValue(audioId, out int found) ? found : null;
    }

    static int? FindMavenDialogAudioRow(string dialogId)
    {
        if (_mavenDialogAudioRows is null)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (ClientTables.Has("MavenDialog"))
            {
                foreach (var row in ClientTables.Table("MavenDialog"))
                {
                    if (row.Text("Id") is { Length: > 0 } id && row.IntOrNull("TextAudioT1") is int audio and >= 0)
                        map.TryAdd(id, audio);
                }
            }
            _mavenDialogAudioRows = map;
        }
        return _mavenDialogAudioRows.TryGetValue(dialogId, out int found) ? found : null;
    }

    /// <summary>Whether the row does something when its talk ends (or at once without a talk).</summary>
    public static bool EndsWithScript(NpcCatalog.Talk talk) =>
        talk.NextChat is not null || talk.FinishesChat || talk.AreaTransition is not null || talk.OpenScreenRequest is not null
        || talk.RulesetEvents.Count > 0 || talk.MavenDialogs.Count > 0 || talk.ScriptTextAudios.Count > 0 || talk.PlayAnimations.Count > 0;

    // The row's world changes, when it is chosen: the NPC's own Transitionable steps on (Dialla withers, NPCTalk 534),
    // the objects of the named types transition (Quests.OnScriptTransition), DropQuestItem drops the named quest item
    // at the NPC, and SetMavenRegionFlag dispatches the general "set_maven_region_flag" ruleset event.
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
        foreach (string itemId in talk.DropQuestItems)
            await DropQuestItem(player, itemId, (npc.X, npc.Y), $"{npc.Npc.Name} (NPCTalk {talk.Row})");
        if (talk.SetsMavenRegionFlag)
            await GameEvents.DispatchRulesetEvent(player, npcId, npc, talk, "set_maven_region_flag");
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
        bool closes = talk.FinishesChat || talk.AreaTransition is not null || talk.OpenScreenRequest is not null;
        bool hasPostActions = talk.RulesetEvents.Count > 0 || talk.MavenDialogs.Count > 0
            || talk.ScriptTextAudios.Count > 0 || talk.PlayAnimations.Count > 0;
        if (!closes && !hasPostActions) return false;
        if (closes)
        {
            player.TalkingTo = 0;
            player.InNpcTalk = false;
            player.NpcFinishFlag = null;
            player.NpcFinishLeagueFlag = null;
            player.NpcTalkScript = null;
            await Send(player, InstanceNpcs.Talk(0), $"0x0084 object 0: {npc.Npc.Name}'s dialogue ends (FinishNPCChat, NPCTalk {talk.Row})");
        }
        foreach (string ev in talk.RulesetEvents)
            await GameEvents.DispatchRulesetEvent(player, npcId, npc, talk, ev);
        foreach (string dialogId in talk.MavenDialogs)
        {
            if (!await GameEvents.DispatchRulesetEvent(player, npcId, npc, talk, "maven_dialog:" + dialogId)
                && FindMavenDialogAudioRow(dialogId) is int audioRow)
            {
                int speakerRow = Npcs.Find("Metadata/NPC/Epilogue/Maven")?.Row ?? npc.Npc.Row;
                await Send(player, InstanceNpcs.TextAudio(npcId, (ushort)speakerRow, (ushort)audioRow),
                    $"0x01c7 SendMavenDialog({dialogId}) -> NPCTextAudio {audioRow} (NPCTalk {talk.Row})");
            }
        }
        foreach (var (audioId, npcTypeId, _) in talk.ScriptTextAudios)
        {
            if (FindNpcTextAudioRow(audioId) is int audioRow)
            {
                int speakerRow = Npcs.Find(npcTypeId)?.Row ?? npc.Npc.Row;
                await Send(player, InstanceNpcs.TextAudio(npcId, (ushort)speakerRow, (ushort)audioRow),
                    $"0x01c7 SendTextAudio({audioId}, {npcTypeId}) -> NPCTextAudio {audioRow} (NPCTalk {talk.Row})");
            }
        }
        foreach (string clip in talk.PlayAnimations)
        {
            if (!await GameEvents.DispatchRulesetEvent(player, npcId, npc, talk, "play_animation:" + clip)
                && string.Equals(clip, "portal_out_01", StringComparison.OrdinalIgnoreCase))
                await StepNpcTransition(player, npcId, npc, $"PlayAnimation({clip}) (NPCTalk {talk.Row})");
        }
        if (talk.OpenScreenRequest is var (screen, screenArg))
        {
            if (ScriptScreens.TryGetValue(screen, out byte kind))
                await Send(player, InstanceWaypoints.OpenUi(kind, screenArg),
                    $"0x007b open {screen} (arg {screenArg}, NPCTalk {talk.Row})");
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
        return closes;
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
