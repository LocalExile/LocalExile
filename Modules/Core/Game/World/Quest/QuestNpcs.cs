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
    public static List<QuestNpc> QuestNpcCandidates(string remote, ClientState conn, QuestObjectTypes.AreaPlacements placed)
    {
        string area = conn.Layout.Id;
        var found = new List<QuestNpc>();
        if (Npcs.Value.Error is not null) return found;
        if (TownNpcs.Of(conn.Layout.Hash) is not null)
        {
            foreach (var p in NpcCatalog.In(conn.Layout.Hash, conn.Seed))
                if (QuestsOf(conn).ManagesNpc(area, p.Id) && Npcs.Value.Place(p) is { Supported: true } npc)
                    found.Add(new(npc, p.X, p.Y, p.Rotation, area, p.Id, null, roomState: false));
            return found;
        }
        foreach (string type in QuestsOf(conn).PlacedNpcTypes(area))
        {
            // An NPC no room line places takes another object's place (Quests.NpcRule.AtObject: Dialla on the tower top).
            string where = QuestsOf(conn).Npcs(area).FirstOrDefault(r => r.NpcType == type && r.AtObject is not null)?.AtObject ?? type;
            var at = placed.Of(where);
            if (at.Count == 0)
            {
                if (QuestsOf(conn).Npcs(area).Any(r => r.NpcType == type)) Console.WriteLine($"[gateway] {remote} {type}: no room places {(where == type ? "it" : where)}");
                continue;
            }
            if (Npcs.Value.Find(type) is not { Supported: true } npc || npc.Id != type)
            {
                Console.WriteLine($"[gateway] {remote} {type}: unknown or unsupported NPC");
                continue;
            }
            foreach (var o in at.DistinctBy(o => (o.X, o.Y)))
                found.Add(new(npc, o.X, o.Y, o.Rotation, area, type, null, roomState: true));
        }
        return found;
    }

    // Who appears where a boss dies, on a later visit: beside the boss while its kill flags are set and the NPC's GoneWith
    // is not (SinInnocence until a5q6-QuestReceived).
    public static IEnumerable<QuestNpc> AppearedBefore(ClientState conn)
    {
        string area = conn.Layout.Id;
        foreach (var rule in QuestsOf(conn).Appearing(area).Where(r => r.GoneWith is not null))
        {
            if (!QuestsOf(conn).OnMonsterKilled(area, rule.Variety).Any(f => ClientHasQuestFlag(conn, f))) continue;
            SpawnedMonster? boss;
            lock (conn.Monsters) boss = conn.Monsters.Values.FirstOrDefault(m => m.Variety?.Id == rule.Variety);
            if (boss is null || Npcs.Value.Find(rule.NpcType) is not { Supported: true } npc || npc.Id != rule.NpcType) continue;
            var at = boss.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            var (x, y) = conn.Layout.Near(((int)at.X + 8, (int)at.Y + 8));
            yield return AppearingNpc(area, rule, npc, x, y);
        }
    }

    static readonly Lazy<HashSet<string>> SoulArrivalNpcs = new(() => ObjectTypes.All()
        .Where(t => t.Row.Texts("chain").Contains("Metadata/NPC/Act6/SinBossDeathParent")
            || t.Row.TextOrNull("client_transitionable_create_untransitioned")?
                .Contains("Metadata/Effects/Spells/monsters_effects/act5/Sin/absorb/", StringComparison.Ordinal) == true)
        .Select(t => t.Type).ToHashSet(StringComparer.Ordinal));

    // State zero runs the native entrance/absorb scripts. A later visit uses the catalog's idle state.
    public static QuestNpc AppearingNpc(string area, Quests.AppearsRule rule, NpcCatalog.Npc npc, int x, int y,
        bool arriving = false) =>
        new(arriving && SoulArrivalNpcs.Value.Contains(npc.Id) ? npc with { TransitionState = 0 } : npc,
            x, y, 0, area, rule.NpcType, rule, roomState: false);

    // The quest NPCs as the flags have them: created when they stand there now, removed when they no longer do, moved to
    // their last Transitionable state when a flag transitions them.
    public static async Task UpdateQuestNpcs(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string why)
    {
        List<QuestNpc> candidates;
        lock (conn.QuestNpcs) candidates = [.. conn.QuestNpcs];
        string area = conn.Layout.Id;
        bool Has(string f) => ClientHasQuestFlag(conn, f);
        var quests = QuestsOf(conn);
        foreach (var c in candidates)
        {
            bool want = c.Present(quests, Has) && KitavaActTen.PreFightNpcShown(conn, c.Npc.Id);
            bool transitioned;
            lock (conn.QuestNpcs) transitioned = QuestsOf(conn).NpcTransitioned(area, c.Npc.Id, Has) || QuestsOf(conn).NpcFreed(area, c.Npc.Id, conn.KilledVarieties);
            bool transitionable = c.Npc.Components?.Contains("Transitionable") == true;
            if (want && c.Id == 0)
            {
                var npc = !transitionable ? c.Npc
                    : transitioned ? c.Npc with { TransitionState = NpcTransitionStates(c.Npc) }
                    : c.RoomState ? c.Npc with { TransitionState = 1 } : c.Npc;
                c.Id = await SpawnNpc(remote, stream, ciphers, conn, npc, c.X, c.Y, c.Angle);
                Console.WriteLine($"[gateway] {remote} quest npc {c.Npc.Name} ({c.Npc.Id}) appears at ({c.X}, {c.Y}): {why}");
            }
            else if (!want && c.Id != 0)
            {
                uint id = c.Id;
                c.Id = 0;
                lock (conn.Npcs) conn.Npcs.Remove(id);
                conn.RemoveBlocker(id);
                if (conn.TalkingTo == id) { conn.TalkingTo = 0; conn.InNpcTalk = false; }
                await Game.SendWorldRemoval(remote, stream, ciphers, conn, id, $"0x02bd quest npc 0x{id:x} {c.Npc.Name} goes: {why}");
            }
            else if (want && transitioned && transitionable)
            {
                SpawnedNpc? spawned;
                lock (conn.Npcs) spawned = conn.Npcs.GetValueOrDefault(c.Id);
                byte last = NpcTransitionStates(c.Npc);
                if (spawned is not null && spawned.State < last)
                {
                    spawned.State = last;
                    int component = c.Npc.Components!.ToList().IndexOf("Transitionable");
                    await SendWorldRecord(remote, stream, ciphers, conn, c.Id, InstanceEntities.TransitionableUpdate(c.Id, (byte)component, last),
                        $"0x02bc quest npc 0x{c.Id:x} {c.Npc.Name} transitions to state {last}: {why}");
                }
            }
        }
    }

    // A monster died: the NPCs that appear where it fell (Quests.AppearsRule: Sin beside each god).
    public static async Task QuestNpcsAppear(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        MonsterCatalog.Variety variety, (int X, int Y) at)
    {
        var instance = conn.Instance;
        await Show(conn, remote, stream, ciphers);
        foreach (var peer in instance.Presence.PlayersExcept(conn.PeerId))
            if (peer.Owner is { Closed: false, LeavingArea: false, PlayerSent: true } other
                && ReferenceEquals(other.Instance, instance))
                other.KillShares.Enqueue((address, output, keys) =>
                    !other.Closed && !other.LeavingArea && ReferenceEquals(other.Instance, instance)
                        ? Show(other, address, output, keys) : Task.CompletedTask);

        async Task Show(ClientState viewer, string address, NetworkStream output, ConnectionCiphers keys)
        {
            string area = viewer.Layout.Id;
            foreach (var rule in QuestsOf(viewer).AppearOnKill(area, variety.Id))
            {
                if (Npcs.Value.Find(rule.NpcType) is not { Supported: true } npc || npc.Id != rule.NpcType)
                {
                    Console.WriteLine($"[gateway] {address} {rule.NpcType}: unknown or unsupported NPC");
                    continue;
                }
                lock (viewer.QuestNpcs)
                {
                    if (viewer.QuestNpcs.Any(c => c.Npc.Id == rule.NpcType && (c.Appears is not null || c.Id != 0))) continue;
                    var (x, y) = viewer.Layout.Walkable is { } walks && !walks(at.X, at.Y) ? viewer.Layout.Near(at) : at;
                    viewer.QuestNpcs.Add(AppearingNpc(area, rule, npc, x, y, arriving: true));
                }
            }
            await UpdateQuestNpcs(address, output, keys, viewer, $"{variety.Name} died");
        }
    }
}
