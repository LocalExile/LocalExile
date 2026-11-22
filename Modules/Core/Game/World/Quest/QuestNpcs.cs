using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

// The NPCs a quest decides (campaign-quests.md E4, E6, E7): room NPCs outside towns (Quests.NpcRule), the stage of a town
// NPC its room lists (DiallaTown / DiallaTownWithered), objects that exist only from or until a flag (Quests.ExistsRule:
// Lilly on the rooftop) and NPCs that appear where a boss dies (Quests.AppearsRule: the god Sins). Each is a candidate of
// the instance (conn.QuestNpcs) with the place it stands; UpdateQuestNpcs creates, removes and transitions them as the
// character's flags change.
public static partial class Game
{
    // The instance's quest NPCs, from the town's placements or where the area's rooms place them (entering the area).
    public static List<QuestNpc> QuestNpcCandidates(Player player, QuestObjectTypes.AreaPlacements placed)
    {
        string area = player.Layout.Id;
        var found = new List<QuestNpc>();
        if (Npcs.Error is not null) return found;
        if (TownNpcs.Of(player.Layout.Hash) is not null)
        {
            foreach (var p in NpcCatalog.In(player.Layout.Hash, player.Seed))
                if (QuestsOf(player).ManagesNpc(area, p.Id) && Npcs.Place(p) is { Supported: true } npc)
                    found.Add(new(npc, p.X, p.Y, p.Rotation, area, p.Id, null, roomState: false));
            return found;
        }
        foreach (string type in QuestsOf(player).PlacedNpcTypes(area))
        {
            // An NPC no room line places takes another object's place (Quests.NpcRule.AtObject: Dialla on the tower top).
            string where = QuestsOf(player).Npcs(area).FirstOrDefault(r => r.NpcType == type && r.AtObject is not null)?.AtObject ?? type;
            var at = placed.Of(where);
            if (at.Count == 0)
            {
                if (QuestsOf(player).Npcs(area).Any(r => r.NpcType == type)) Console.WriteLine($"[gateway] {player.Connection.Remote} {type}: no room places {(where == type ? "it" : where)}");
                continue;
            }
            if (Npcs.Find(type) is not { Supported: true } npc || npc.Id != type)
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} {type}: unknown or unsupported NPC");
                continue;
            }
            foreach (var o in at.DistinctBy(o => (o.X, o.Y)))
                found.Add(new(npc, o.X, o.Y, o.Rotation, area, type, null, roomState: true));
        }
        return found;
    }

    static string NpcAppearanceKey(string variety) => "quest-npc-kill:" + variety;

    // A character's old kill flags do not mean a new instance's boss has died. Keep the death site in the instance;
    // corpses are omitted from its saved monster list. When a rule names a fallback room object (AtObject) for a
    // story-critical hand-off (SinInnocence giving a5q6), a fresh instance still places the NPC there until GoneWith
    // is set. A later visit uses the NPC's completed entrance state.
    public static IEnumerable<QuestNpc> AppearedBefore(Player player, QuestObjectTypes.AreaPlacements? placed = null)
    {
        string area = player.Layout.Id;
        foreach (var rule in QuestsOf(player).Appearing(area).Where(r => r.GoneWith is not null))
        {
            if (!QuestsOf(player).OnMonsterKilled(area, rule.Variety).Any(f => ClientHasQuestFlag(player, f))) continue;
            var pos = player.Instance.RememberedPosition(NpcAppearanceKey(rule.Variety));
            if (pos is null && rule.AtObject is { } fallback && placed is not null && !ClientHasQuestFlag(player, rule.GoneWith!)
                && placed.Of(fallback).FirstOrDefault() is { } spot)
                pos = (spot.X, spot.Y);
            if (pos is not { } at
                || Npcs.Find(rule.NpcType) is not { Supported: true } npc || npc.Id != rule.NpcType) continue;
            var (x, y) = player.Layout.Walkable is { } walks && !walks(at.X, at.Y) ? player.Layout.Near(at) : at;
            if (npc.Components?.Contains("Transitionable") == true)
                npc = npc with { TransitionState = NpcTransitionStates(npc) };
            yield return AppearingNpc(area, rule, npc, x, y);
        }
    }

    public static QuestNpc AppearingNpc(string area, Quests.AppearsRule rule, NpcCatalog.Npc npc, int x, int y) =>
        new(npc, x, y, 0, area, rule.NpcType, rule, roomState: false);

    // The quest NPCs as the flags have them: created when they stand there now, removed when they no longer do, moved to
    // their configured Transitionable state (the last state by default) when a flag transitions them.
    public static async Task UpdateQuestNpcs(Player player, string why)
    {
        List<QuestNpc> candidates;
        lock (player.QuestNpcs) candidates = [.. player.QuestNpcs];
        string area = player.Layout.Id;
        bool Has(string f) => ClientHasQuestFlag(player, f);
        var quests = QuestsOf(player);
        foreach (var c in candidates)
        {
            bool want = c.Present(quests, Has) && KitavaActTen.PreFightNpcShown(player, c.Npc.Id);
            bool transitioned;
            byte transitionState;
            lock (player.QuestNpcs)
            {
                transitioned = quests.NpcTransitioned(area, c.Npc.Id, Has) || quests.NpcFreed(area, c.Npc.Id, player.Character.KilledVarieties);
                transitionState = quests.Npcs(area).FirstOrDefault(r => r.NpcType == c.Npc.Id && r.TransitionedTo is not null
                    && (r.TransitionedBy is { } flag && Has(flag) || r.FreedBy is { } boss && player.Character.KilledVarieties.Contains(boss)))
                    ?.TransitionedTo ?? NpcTransitionStates(c.Npc);
            }
            bool transitionable = c.Npc.Components?.Contains("Transitionable") == true;
            if (want && c.Id == 0)
            {
                var npc = !transitionable ? c.Npc
                    : transitioned ? c.Npc with { TransitionState = transitionState }
                    : c.RoomState ? c.Npc with { TransitionState = 1 } : c.Npc;
                c.Id = await SpawnNpc(player, npc, c.X, c.Y, c.Angle);
                if (c.Appears?.AdvanceOnAppearance == true)
                {
                    SpawnedNpc? spawned;
                    lock (player.Npcs) spawned = player.Npcs.GetValueOrDefault(c.Id);
                    if (spawned is not null)
                        await StepNpcTransition(player, c.Id, spawned, "quest NPC entrance");
                }
                Console.WriteLine($"[gateway] {player.Connection.Remote} quest npc {c.Npc.Name} ({c.Npc.Id}) appears at ({c.X}, {c.Y}): {why}");
            }
            else if (!want && c.Id != 0)
            {
                uint id = c.Id;
                c.Id = 0;
                lock (player.Npcs) player.Npcs.Remove(id);
                player.RemoveBlocker(id);
                if (player.TalkingTo == id) { player.TalkingTo = 0; player.InNpcTalk = false; }
                await Game.SendWorldRemoval(player, id, $"0x02bd quest npc 0x{id:x} {c.Npc.Name} goes: {why}");
            }
            else if (want && transitioned && transitionable)
            {
                SpawnedNpc? spawned;
                lock (player.Npcs) spawned = player.Npcs.GetValueOrDefault(c.Id);
                byte last = transitionState;
                if (spawned is not null && spawned.State < last)
                {
                    spawned.State = last;
                    int component = c.Npc.Components!.ToList().IndexOf("Transitionable");
                    await SendWorldRecord(player, c.Id, InstanceEntities.TransitionableUpdate(c.Id, (byte)component, last),
                        $"0x02bc quest npc 0x{c.Id:x} {c.Npc.Name} transitions to state {last}: {why}");
                }
            }
        }
    }

    // A monster died: the NPCs that appear where it fell (Quests.AppearsRule: Sin beside each god).
    public static async Task QuestNpcsAppear(Player player,
        MonsterCatalog.Variety variety, (int X, int Y) at)
    {
        var instance = player.Instance;
        instance.RememberPosition(NpcAppearanceKey(variety.Id), at);
        await Show(player);
        foreach (var peer in instance.Presence.PlayersExcept(player.Id))
            if (peer is { Closed: false, LeavingArea: false, Connection.PlayerSent: true } other
                && ReferenceEquals(other.Instance, instance))
                other.KillShares.Enqueue(_ =>
                    !other.Closed && !other.LeavingArea && ReferenceEquals(other.Instance, instance)
                        ? Show(other) : Task.CompletedTask);

        async Task Show(Player viewer)
        {
            string area = viewer.Layout.Id;
            foreach (var rule in QuestsOf(viewer).AppearOnKill(area, variety.Id))
            {
                if (Npcs.Find(rule.NpcType) is not { Supported: true } npc || npc.Id != rule.NpcType)
                {
                    Console.WriteLine($"[gateway] {viewer.Connection.Remote} {rule.NpcType}: unknown or unsupported NPC");
                    continue;
                }
                lock (viewer.QuestNpcs)
                {
                    if (viewer.QuestNpcs.Any(c => c.Npc.Id == rule.NpcType && (c.Appears is not null || c.Id != 0))) continue;
                    var (x, y) = viewer.Layout.Walkable is { } walks && !walks(at.X, at.Y) ? viewer.Layout.Near(at) : at;
                    viewer.QuestNpcs.Add(AppearingNpc(area, rule, npc, x, y));
                }
            }
            await UpdateQuestNpcs(viewer, $"{variety.Name} died");
        }
    }
}
