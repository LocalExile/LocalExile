using System.Text.Json;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;

static partial class Game
{
    // Sets or clears quest flags for the character (saved at once) and sends the player's flag set again; one change at a
    // time per connection (QuestGate), so a background sync never interleaves with another change.
    internal static async Task SetQuestFlags(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IEnumerable<string> flags, bool on = true)
    {
        await conn.QuestGate.WaitAsync();
        try { await SetQuestFlagsHeld(remote, stream, ciphers, conn, flags, on); }
        finally { conn.QuestGate.Release(); }
    }

    internal sealed record QuestFlagCommit(List<string> Changed, List<int> All,
        List<string> Effective, List<uint> Removed);

    // Gameplay and persistence commit before any notification. Caller holds the combat lock when used by simulation.
    internal static QuestFlagCommit? CommitQuestFlags(ClientState conn, IEnumerable<string> flags, bool on = true)
    {
        lock (conn.Monsters)
        {
            // Queued work cannot mutate progress after a committed Hardcore death.
            if (HardcoreProgressBlocked(conn)) return null;
            var wanted = flags.Distinct().ToList();
            var clientBefore = PlayerQuestFlags(conn);
            var ownBefore = conn.QuestFlagsSnapshot();
            var had = wanted.ToDictionary(f => f, f => Quests.ClientHas(f, ownBefore, clientBefore));
            var changed = new List<string>();
            foreach (string flag in wanted)
                if (on ? conn.QuestFlags.Add(flag) : conn.QuestFlags.Remove(flag)) changed.Add(flag);
            if (changed.Count == 0) return null;
            CharacterSaves.ChangeQuestFlags(CharacterName(conn), changed, on);
            if (ChangesResistancePenalty(changed))
            {
                conn.Pools = PoolsFor(conn, conn.Pools.Level);
                conn.ApplyMovementSpeed();
            }
            var all = PlayerQuestFlags(conn);
            var own = conn.QuestFlagsSnapshot();
            bool Has(string flag) => Quests.ClientHas(flag, own, all);
            var effective = Quests.EffectiveChanges(changed, f => had[f], Has);
            List<uint> gone = [.. conn.QuestRoomObjects.Where(o => effective.Contains(o.Value.Flag) && Has(o.Value.Flag)).Select(o => o.Key)];
            foreach (uint id in gone) conn.QuestRoomObjects.Remove(id);
            conn.UpdateBarrier();
            return new(changed, all, effective, gone);
        }
    }

    internal static async Task SetQuestFlagsHeld(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, IEnumerable<string> flags, bool on = true)
    {
        QuestFlagCommit? committed;
        lock (conn.Monsters) committed = CommitQuestFlags(conn, flags, on);
        if (committed is not null) await PublishQuestFlags(remote, stream, ciphers, conn, committed, on);
    }

    internal static async Task PublishQuestFlags(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, QuestFlagCommit change, bool on = true)
    {
        foreach (string flag in change.Changed) Console.WriteLine($"[gateway] {remote} {CharacterName(conn)}: quest flag {flag} {(on ? "set" : "cleared")}");
        if (!conn.PlayerSent) return;
        await Send(remote, stream, ciphers, InstanceEntities.PlayerQuestFlagsUpdate(PlayerId, change.All), $"0x02ba player quest flags: {change.All.Count}");
        if (ChangesResistancePenalty(change.Changed))
            await SendCharacterStats(remote, stream, ciphers, conn, "Kitava resistance penalty changed");
        bool Has(string flag) => ClientHasQuestFlag(conn, flag);
        foreach (var (id, transition) in conn.Layout.AreaTransitions)
        {
            if (TransitionFlag(conn, transition) is not { } flag || !change.Effective.Contains(flag)) continue;
            if (Has(flag))
            {
                await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(transition.Type), transition.Blob()),
                    $"0x02b9 create area transition 0x{id:x} {transition} (quest flag {flag})");
                await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
            }
            else await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove area transition 0x{id:x} (quest flag {flag} cleared)");
        }
        if (change.Effective.Contains(GlyphQuest.PlacedGlyphs))
            await ChangeSubmergedPassage(remote, stream, ciphers, conn, Has(GlyphQuest.PlacedGlyphs));
        foreach (uint id in change.Removed)
            await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove room object 0x{id:x} (quest flag set)");
    }

    // The quest flag an area transition needs (Quests.FlagToEnter), or null. Such an exit is created only once the character
    // has the flag: The Twilight Strand's gate into Lioneye's Watch appears when Hillock dies (a1q1, 12-quests.md §6).
    internal static string? TransitionFlag(ClientState conn, AreaTransitionObject transition) =>
        AreaTransitionCatalog.IdOf(transition.DestinationHash16) is { } dest ? Quests.FlagToEnter(conn.Layout.Id, dest) : null;

    internal static bool TransitionShown(ClientState conn, AreaTransitionObject transition) =>
        TransitionFlag(conn, transition) is not { } flag || ClientHasQuestFlag(conn, flag);

    // A quest flag as the client has it: the character's own, or one the World panel's access flags carry (an activated
    // waypoint's area, 16-waypoints.md §4: the Submerged Passage's waypoint brings a1q4-PlacedGlyphs). The client opens
    // its ways by these, so the server's checks follow them.
    internal static bool ClientHasQuestFlag(ClientState conn, string flag) => Quests.ClientHas(flag, conn.QuestFlagsSnapshot(), PlayerQuestFlags(conn));

    // Ids of the invisible SunkenPassageBlockers on the server's walk estimate (blockers only: the client spawns them).
    internal const uint PassageBlockerFirstId = 0xff000000;
    // a1q4's objects have ids of their own, between the quest room objects (QuestRoomObjectFirstId) and the waypoint, so
    // they never meet the ids other code takes from NextObjectId: the nests (+ their index), the wall, the waterfall and
    // the opened nests (+ 0x20 + their index).
    internal const uint GlyphObjectFirstId = 0x2e80, GlyphWallObjectId = GlyphObjectFirstId + 0x10, GlyphWaterObjectId = GlyphObjectFirstId + 0x11,
        OpenedNestFirstId = GlyphObjectFirstId + 0x20;

    // Which glyph (0–2) an item is, or null.
    internal static int? GlyphIndex(Item item) => GlyphQuest.GlyphOf(Items.ByHash(item.TypeHash)?.Id);

    internal static void SetPassageBlockers(ClientState conn, bool on)
    {
        if (conn.Glyphs is not { } layout) return;
        for (int i = 0; i < layout.Blockers.Count; i++)
            if (on) conn.SetBlocker(PassageBlockerFirstId + (uint)i, layout.Blockers[i].X, layout.Blockers[i].Y, GlyphQuest.BlockerSize);
            else conn.RemoveBlocker(PassageBlockerFirstId + (uint)i);
    }

    // PlacedGlyphs as the client has it changed: the wall and the waterfall play their transition (0x2ba to
    // Transitionable), and the invisible blockers leave the server's walk estimate (the client drops its own when it gets
    // the flag). Cleared (dev): the other way.
    internal static async Task ChangeSubmergedPassage(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, bool open)
    {
        lock (conn.Monsters)
        {
            if (HardcoreProgressBlocked(conn) || conn.Glyphs is null) return;
            SetPassageBlockers(conn, !open);
        }
        byte state = open ? GlyphQuest.Transitioned : GlyphQuest.Untransitioned;
        foreach (var (id, what) in new[] { (conn.GlyphWallId, "Glyph Wall"), (conn.GlyphWaterId, "waterfall") })
            if (id != 0)
                await Send(remote, stream, ciphers, InstanceEntities.TransitionableUpdate(id, GlyphQuest.TransitionableIndex, state),
                    $"0x02ba {what} 0x{id:x} Transitionable → {state}");
    }

    // The HaveGlyph flags follow the glyphs the character carries (GlyphQuest.SyncFlags), whatever took them away (a drop,
    // a sale, the stash). Runs from the player tick: it reads the flags as a snapshot and changes them inside
    // conn.QuestGate, and it changes no inventory (RemoveLeftoverGlyphs does that on the message path).
    internal static async Task SyncGlyphs(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        await conn.QuestGate.WaitAsync();
        try
        {
            var carried = GlyphQuest.Carried(ItemsOf(conn)).Select(c => GlyphIndex(c.Item.Item)).OfType<int>().ToHashSet();
            var own = conn.QuestFlagsSnapshot();
            bool placed = Quests.ClientHas(GlyphQuest.PlacedGlyphs, own, PlayerQuestFlags(conn));
            var (set, clear) = GlyphQuest.SyncFlags(own, placed, placed ? new HashSet<int>() : carried);
            if (set.Count > 0) await SetQuestFlagsHeld(remote, stream, ciphers, conn, set, true);
            if (clear.Count > 0) await SetQuestFlagsHeld(remote, stream, ciphers, conn, clear, false);
        }
        finally { conn.QuestGate.Release(); }
    }
}
