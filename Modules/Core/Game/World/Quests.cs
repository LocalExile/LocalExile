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
                // A flag every character is given (Quests.EveryCharacter) but has not earned yet is earned now: saved, though
                // the client may have had it (Kitava's death sets EndGameAvailable, which a hideout and the maps had).
                if (on ? conn.QuestFlags.Add(flag) | conn.UnearnedQuestFlags.Remove(flag) : conn.QuestFlags.Remove(flag)) changed.Add(flag);
            if (changed.Count == 0) return null;
            CharacterSaves.ChangeQuestFlags(CharacterName(conn), changed, on);
            // A flag with refund points (QuestStaticRewards) was credited by the save: the balance follows it.
            if (on && changed.Any(f => QuestStaticRewards.Default.Value.Find(f) is { RefundPoints: > 0 }))
            {
                conn.RefundPoints = CharacterSaves.Load(CharacterName(conn))?.RefundPoints ?? conn.RefundPoints;
                conn.SavedPassiveProgress = new(conn.MainAscendancy, conn.Passives, conn.MasteryEffects, conn.RefundPoints);
            }
            if (ChangesResistancePenalty(changed) || ChangesQuestStats(changed))
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
            if (conn.QuestBlockers.Any(b => effective.Contains(b.Rule.UnblockedBy))) UpdateQuestBlockers(conn);
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
        if (ChangesResistancePenalty(change.Changed) || ChangesQuestStats(change.Changed))
            await SendCharacterStats(remote, stream, ciphers, conn, "quest static reward stats changed");
        // Quest passive and refund points (QuestStaticRewards) are in the tree's numbers.
        if (ChangesStaticRewards(change.Changed))
            await SendPassives(remote, stream, ciphers, conn);
        await UpdateShownTransitions(remote, stream, ciphers, conn, $"quest flags {string.Join(", ", change.Effective)}");
        await UpdateQuestNpcs(remote, stream, ciphers, conn, $"quest flags {string.Join(", ", change.Effective)}");
        if (change.Effective.Contains(GlyphQuest.PlacedGlyphs))
            await ChangeSubmergedPassage(remote, stream, ciphers, conn, ClientHasQuestFlag(conn, GlyphQuest.PlacedGlyphs));
        // The quest engine's objects and chests whose state follows a flag (World/Quest/QuestEngine.cs).
        await UpdateQuestObjectStates(remote, stream, ciphers, conn, change.Effective);
        foreach (uint id in change.Removed)
            await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove room object 0x{id:x} (quest flag set)");
    }

    // The quest flag an area transition needs (Quests.FlagToEnter), or null. Such an exit is created only once the character
    // has the flag: The Twilight Strand's gate into Lioneye's Watch appears when Hillock dies (a1q1, 12-quests.md §6).
    internal static string? TransitionFlag(ClientState conn, AreaTransitionObject transition) =>
        AreaTransitionCatalog.IdOf(transition.DestinationHash16) is { } dest ? QuestsOf(conn).FlagToEnter(conn.Layout.Id, dest) : null;

    // The quest tables that run for this player: those of the modules its league lists (Quests.For; empty without
    // QuestObjects). The area's layout is the same for every league, so what the tables add or change there is decided
    // here, per player (TransitionShown).
    internal static Quests.RuleSet QuestsOf(ClientState conn) => Quests.For(ModulesFor(conn));

    // The flag a dialogue row sets when chosen: its QuestFlag4, unless the player's tables say only an event sets it.
    internal static string? TalkStartFlag(ClientState conn, NpcCatalog.Talk talk) => QuestsOf(conn).TalkStartFlag(talk);

    // Shown: its tile type exists for the character (Quests.ExistsRule: the Control Blocks' exit to Oriath Square before
    // Avarius, the one to The Ruined Square after), and its quest flag is set or a script opened it on this instance.
    // An exit a quest added to the shared layout (a gated disabled entrance, a quest portal, a swapped exit's version:
    // AreaTransitionObject.QuestSource) stands only where that source's tables run; one a quest replaced
    // (QuestReplaced: the exit without a model a portal stands in for, a swapped exit's unchanged version) only where
    // they do not, so a league without them has the layout as generated.
    internal static bool TransitionShown(ClientState conn, uint id, AreaTransitionObject transition)
    {
        var quests = QuestsOf(conn);
        if (transition.QuestSource is { } source && quests.Includes(source) == transition.QuestReplaced) return false;
        return quests.Exists(transition.SourceType, f => ClientHasQuestFlag(conn, f))
            && (conn.ScriptShownTransitions.Contains(id) || TransitionFlag(conn, transition) is not { } flag || ClientHasQuestFlag(conn, flag));
    }

    // The area transitions whose shown state changed (a quest flag, an NPC script): created on the client or removed.
    internal static async Task UpdateShownTransitions(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string why)
    {
        if (!conn.PlayerSent) return;
        // Whether each should stand now (outside the lock: it reads the flags), then, under one lock, the ones whose state
        // the client has differently, recorded as sent: of two updates at once only one creates or removes an exit.
        var want = conn.Layout.AreaTransitions.Select(t => (t.Id, t.Transition, Shown: TransitionShown(conn, t.Id, t.Transition))).ToList();
        List<(uint Id, AreaTransitionObject Transition, bool Shown)> changed;
        lock (conn.ShownTransitions)
        {
            changed = [.. want.Where(t => t.Shown != conn.ShownTransitions.Contains(t.Id))];
            foreach (var (id, _, shown) in changed) { if (shown) conn.ShownTransitions.Add(id); else conn.ShownTransitions.Remove(id); }
        }
        foreach (var (id, transition, shown) in changed)
        {
            if (shown)
            {
                await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(transition.Type), transition.Blob()),
                    $"0x02b9 create area transition 0x{id:x} {transition} ({why})");
                await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
            }
            else await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove area transition 0x{id:x} ({why})");
        }
    }

    // A quest flag as the client has it: the character's own, or one the World panel's access flags carry (an activated
    // waypoint's area, 16-waypoints.md §4: the Submerged Passage's waypoint brings a1q4-PlacedGlyphs). The client opens
    // its ways by these, so the server's checks follow them.
    internal static bool ClientHasQuestFlag(ClientState conn, string flag) =>
        Quests.IsInstanceFlag(flag) ? conn.Instance.HasState(flag) : Quests.ClientHas(flag, conn.QuestFlagsSnapshot(), PlayerQuestFlags(conn));

    // The first ground click in WashedUp sends this action instead of Move, and any other request to move gets it from the
    // server (0x141e94fc0). It plays getup in place; treating a target as a walk would move only the server's position estimate.
    internal static async Task WakeUpOnBeach(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        (int X, int Y)? target, ushort sequence, ushort flags, double requestedAt)
    {
        int action;
        (int X, int Y) here;
        using (ProjectileMutation(conn))
        {
            double now = ProjectileWorldTime(conn);
            if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || !LyingOnBeach(conn)
                || PlayerActionBlocked(conn, requestedAt, now)) return;
            requestedAt = Math.Max(requestedAt, conn.ProjectileRuntime?.Runtime.Now / 120000d ?? requestedAt);
            conn.TutorialGettingUp = true;
            action = Interlocked.Increment(ref conn.ActionCount);
            here = conn.Walk.Position(requestedAt);
            conn.Walk.Order(here.X, here.Y, requestedAt);
            conn.ChannelSkill = 0;
        }
        var (x, y) = target ?? here;
        await Send(remote, stream, ciphers,
            InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, x, y, TutorialObjects.WashedUpSkillId, sequence, flags),
            $"0x01ab player gets up on the beach at ({here.X}, {here.Y})",
            still: () =>
            {
                if (PlayerActionCurrent(conn, action)) return true;
                // Dropped: the client is still down, so its next request tries again.
                lock (conn.Monsters) conn.TutorialGettingUp = false;
                return false;
            });
    }

    internal static bool LyingOnBeach(ClientState conn) => conn.PeerTemplate is { WashedUp: true } && !conn.TutorialGettingUp;

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
}
