using System.Text.Json;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;

public static partial class Game
{
    // Sets or clears league quest flags (Quests.League ids, such as a Trial of Ascendancy's): saved with the account's stash
    // in the character's league, so every character of the league has them, then sent as the player's league flag set.
    public static async Task SetLeagueQuestFlags(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IEnumerable<int> flags, bool on = true)
    {
        List<int> wanted = [.. flags], all;
        lock (conn.Monsters)
            if (HardcoreProgressBlocked(conn)) return;
        PersonalStashOf(conn).ChangeLeagueQuestFlags(wanted, on);
        lock (conn.Monsters)
        {
            if (on) conn.LeagueQuestFlags.UnionWith(wanted); else conn.LeagueQuestFlags.ExceptWith(wanted);
            all = [.. conn.LeagueQuestFlags.Order()];
        }
        Console.WriteLine($"[gateway] {remote} {CharacterName(conn)}: league quest flag(s) {string.Join(", ", wanted)} {(on ? "set" : "cleared")}");
        if (!conn.PlayerSent) return;
        await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.PlayerLeagueQuestFlagsUpdate(PlayerId, Quests.League.Bits(all)),
            $"0x02bc player league quest flags: {string.Join(", ", all)}");
    }

    // Sets or clears quest flags for the character (saved at once) and sends the player's flag set again; one change at a
    // time per connection (QuestGate), so a background sync never interleaves with another change.
    public static async Task SetQuestFlags(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IEnumerable<string> flags, bool on = true)
    {
        if (QuestKillCommitOutput is not null)
        {
            await SetQuestFlagsHeld(remote, stream, ciphers, conn, flags, on);
            return;
        }
        await conn.QuestGate.WaitAsync();
        try { await SetQuestFlagsHeld(remote, stream, ciphers, conn, flags, on); }
        finally { conn.QuestGate.Release(); }
    }

    public sealed record QuestFlagCommit(List<string> Changed, List<int> All,
        List<string> Effective, List<uint> Removed);

    // Gameplay and persistence commit before any notification. Caller holds the combat lock when used by simulation.
    public static QuestFlagCommit? CommitQuestFlags(ClientState conn, IEnumerable<string> flags, bool on = true)
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

    public static async Task SetQuestFlagsHeld(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, IEnumerable<string> flags, bool on = true)
    {
        QuestFlagCommit? committed;
        lock (conn.Monsters) committed = CommitQuestFlags(conn, flags, on);
        if (committed is not null) await PublishQuestFlags(remote, stream, ciphers, conn, committed, on);
    }

    public static async Task PublishQuestFlags(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, QuestFlagCommit change, bool on = true)
    {
        foreach (string flag in change.Changed) Console.WriteLine($"[gateway] {remote} {CharacterName(conn)}: quest flag {flag} {(on ? "set" : "cleared")}");
        if (!conn.PlayerSent) return;
        if (QuestKillCommitOutput is { } output)
            output.Add(new([], "current quest flags", Kind: PlayerMinions.OutputKind.QuestFlags));
        else
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, [], "0x02bc player quest flags (current)", atGate: () => QuestFlagPresentation(conn));
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
            await Send(remote, stream, ciphers, [], $"0x02bd remove room object 0x{id:x} (quest flag set)", atGate: () =>
            { lock (conn.Monsters) return conn.Closed || conn.LeavingArea || conn.QuestRoomObjects.ContainsKey(id) ? null : InstanceProtocol.RemoveObject(id); });
    }

    // Whole flag sets are projections, not snapshots: combat can commit while a normal setter awaits the cipher gate.
    // These pure projections never take QuestGate, so QuestGate -> cipher -> Monsters keeps its existing lock order.
    static byte[]? QuestFlagPresentation(ClientState conn)
    {
        lock (conn.Monsters)
            return conn.Closed || conn.LeavingArea || !conn.PlayerSent ? null
                : InstanceEntities.PlayerQuestFlagsUpdate(PlayerId, PlayerQuestFlags(conn));
    }

    static OutputBatch? QuestTransitionRecords(ClientState conn, WorldAdmission admission, uint id, AreaTransitionObject transition)
    {
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea || !conn.Layout.AreaTransitions.Any(e => e.Id == id && ReferenceEquals(e.Transition, transition))) return null;
            var records = new WorldRecordBuilder(conn, admission);
            if (TransitionShown(conn, id, transition))
            {
                records.Create(id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(transition.Type), transition.Blob()));
                records.Presentation(id, InstanceProtocol.EnterWakeZone(id));
            }
            else records.Remove(id);
            return records.Build();
        }
    }

    // The quest flag an area transition needs (Quests.FlagToEnter), or null. Such an exit is created only once the character
    // has the flag: The Twilight Strand's gate into Lioneye's Watch appears when Hillock dies (a1q1, 12-quests.md §6).
    public static string? TransitionFlag(ClientState conn, AreaTransitionObject transition) =>
        // The plaza's way into the Labyrinth leads to the day's first room: shown once its door is open (ChooseLabyrinth).
        conn.Layout.Id == LabyrinthDay.Plaza && transition.SourceType == LabyrinthDay.Entrance ? LabyrinthDay.PlazaDoorOpen
        // Izaro's arena's ways out (the layout's disabled ones): shown once his trial in this instance is won; the way in
        // once the corridor's gate is open.
        : transition.LayoutDisabled && LabyrinthDay.IzaroRoom(conn.Layout.Id, conn.Seed) is not null ? IzaroFights.Beaten
        : transition.SourceType == IzaroFights.ArenaTransition && LabyrinthDay.IzaroRoom(conn.Layout.Id, conn.Seed) is not null ? IzaroFights.GateOpen
        : AreaTransitionCatalog.IdOf(transition.DestinationHash16) is { } dest ? QuestsOf(conn).FlagToEnter(conn.Layout.Id, dest) : null;

    // Whether a Labyrinth tier's trials are done (Labyrinths RequiredTrials: Normal's six, …; the character's flag or the
    // league's, LabyrinthTrials).
    public static bool LabyrinthTrialsDone(ClientState conn, int tier, out int done, out int count)
    {
        var trials = LabyrinthTrials.Trials.Where(t => LabyrinthTrials.Tier(t) == tier).ToList();
        done = trials.Count(t => conn.HasQuestFlag(t.Flag) || conn.LeagueQuestFlags.Contains(t.LeagueFlag));
        count = trials.Count;
        return count > 0 && done == count;
    }

    // C→S 0x212: the Labyrinth the plaza's select window chose (1 Normal … 4 Eternal). With its trials done the door in front
    // of the way in opens in this instance and the way in shows (to today's first room; only the Normal Labyrinth is built).
    public static async Task ChooseLabyrinth(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, byte tier)
    {
        if (conn.Layout.Id != LabyrinthDay.Plaza) return;
        bool done = LabyrinthTrialsDone(conn, tier, out int have, out int count);
        Console.WriteLine($"[gateway] {remote} <= 0x0212 Labyrinth {tier} chosen: {have} of {count} of its trials done");
        if (tier != 1)
        {
            Console.WriteLine($"[gateway] {remote} Labyrinth {tier}: only the Normal Labyrinth is built, the door stays shut");
            return;
        }
        if (!done || !conn.Instance.SetState(LabyrinthDay.PlazaDoorOpen)) return;
        await UpdateQuestObjectStates(remote, stream, ciphers, conn, [LabyrinthDay.PlazaDoorOpen]);
        await UpdateShownTransitions(remote, stream, ciphers, conn, "the Labyrinth's door opens");
    }

    // The quest tables that run for this player: those of the modules its league lists (Quests.For; empty without
    // QuestObjects). The area's layout is the same for every league, so what the tables add or change there is decided
    // here, per player (TransitionShown).
    public static Quests.RuleSet QuestsOf(ClientState conn) => Quests.For(ModulesFor(conn));

    // The flag a dialogue row sets when chosen: its QuestFlag4, unless the player's tables say only an event sets it.
    public static string? TalkStartFlag(ClientState conn, NpcCatalog.Talk talk) => QuestsOf(conn).TalkStartFlag(talk);

    // Shown: its tile type exists for the character (Quests.ExistsRule: the Control Blocks' exit to Oriath Square before
    // Avarius, the one to The Ruined Square after), and its quest flag is set or a script opened it on this instance.
    // An exit a quest added to the shared layout (a gated disabled entrance, a quest portal, a swapped exit's version:
    // AreaTransitionObject.QuestSource) stands only where that source's tables run; one a quest replaced
    // (QuestReplaced: the exit without a model a portal stands in for, a swapped exit's unchanged version) only where
    // they do not, so a league without them has the layout as generated.
    public static bool TransitionShown(ClientState conn, uint id, AreaTransitionObject transition)
    {
        if (Sirus.GateShown(conn, transition) is bool sirusShown) return sirusShown;
        if (DepravedTrinity.GateShown(conn, id, transition) is bool shown) return shown;
        if (KitavaActFive.GateShown(conn, transition) is bool kitavaShown) return kitavaShown;
        if (KitavaActTen.GateShown(conn, transition) is bool finalKitavaShown) return finalKitavaShown;
        // The Reverie Device's six consumable portals replace the old permanent quest exit.
        if (QuestsOf(conn).Includes("QuestObjects") && conn.Layout.Id == MapDevice.CampaignDeviceArea
            && transition.DestinationHash16 == AreaTransitionCatalog.HashOf(MapDevice.CampaignMapArea)) return false;
        var quests = QuestsOf(conn);
        if (transition.QuestSource is { } source && quests.Includes(source) == transition.QuestReplaced) return false;
        return quests.Exists(transition.SourceType, f => ClientHasQuestFlag(conn, f))
            && (conn.ScriptShownTransitions.Contains(id) || TransitionFlag(conn, transition) is not { } flag || ClientHasQuestFlag(conn, flag));
    }

    // The area transitions whose shown state changed (a quest flag, an NPC script): created on the client or removed.
    public static async Task UpdateShownTransitions(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string why)
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
            if (QuestKillCommitOutput is { } output)
            {
                output.Add(new([], "current quest transition", TargetId: id,
                    Kind: PlayerMinions.OutputKind.QuestTransition, Transition: transition));
                continue;
            }
            if (shown)
            {
                await SendWorldCreate(remote, stream, ciphers, conn, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(transition.Type), transition.Blob()),
                    $"0x02bb create area transition 0x{id:x} {transition} ({why})");
                await SendWorldRecord(remote, stream, ciphers, conn, id, InstanceProtocol.EnterWakeZone(id), null);
            }
            else await Game.SendWorldRemoval(remote, stream, ciphers, conn, id, $"0x02bd remove area transition 0x{id:x} ({why})");
        }
    }

    // A quest flag as the client has it: the character's own, or one the World panel's access flags carry (an activated
    // waypoint's area, 16-waypoints.md §4: the Submerged Passage's waypoint brings a1q4-PlacedGlyphs). The client opens
    // its ways by these, so the server's checks follow them.
    public static bool ClientHasQuestFlag(ClientState conn, string flag)
    {
        if (Quests.IsInstanceFlag(flag)) return conn.Instance.HasState(flag);
        if (conn.HasQuestFlag(flag)) return true;
        if (Quests.Rows([flag]) is not [int row]) return false;
        bool inHideout = HideoutCatalog.ByArea(conn.Layout.Hash) is not null || conn.Layout.Hash == StashCatalog.DelveHub;
        lock (conn.Monsters)
            if (conn.UnearnedQuestFlags.Contains(flag) && Quests.EndgameOnly.Contains(flag)
                && (inHideout || MapDevices.Value.IsMapArea(conn.Layout.Hash))) return true;
        // Combat reads individual flags for every Pantheon/stat calculation. Keep the World panel's access and visit
        // semantics without rebuilding, sorting and allocating its complete flag list for each membership test.
        lock (conn.Waypoints)
        {
            var world = WorldQuestFlagLookups.GetOrCreateValue(conn);
            if (!world.Waypoints.SetEquals(conn.Waypoints) || !world.Visited.SetEquals(conn.Visited))
            {
                world.Waypoints = [.. conn.Waypoints];
                world.Visited = [.. conn.Visited];
                world.Rows.Clear();
                foreach (var area in WaypointCatalog.Areas.Entries)
                    if (conn.Waypoints.Contains(area.Id)) world.Rows.UnionWith(area.AccessFlags ?? []);
                foreach (string area in conn.Visited) world.Rows.UnionWith(WaypointCatalog.Visits.For(area));
                foreach (string area in conn.Waypoints) world.Rows.UnionWith(WaypointCatalog.Visits.For(area));
            }
            return world.Rows.Contains(row);
        }
    }

    // Only the World panel's derived flags are cached. Own/session/instance flags above remain live; adding or
    // removing a visit or waypoint rebuilds these rows, including replacements that leave the set's count unchanged.
    sealed class WorldQuestFlagLookup
    {
        public HashSet<string> Waypoints = [], Visited = [];
        public readonly HashSet<int> Rows = [];
    }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ClientState, WorldQuestFlagLookup> WorldQuestFlagLookups = new();

    // The first ground click in WashedUp sends this action instead of Move, and any other request to move gets it from the
    // server (0x141e9b860). It plays getup in place; treating a target as a walk would move only the server's position estimate.
    public static async Task WakeUpOnBeach(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
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
        await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, x, y, TutorialObjects.WashedUpSkillId, sequence, flags),
            $"0x01ab player gets up on the beach at ({here.X}, {here.Y})",
            still: () =>
            {
                if (PlayerActionCurrent(conn, action)) return true;
                // Dropped: the client is still down, so its next request tries again.
                lock (conn.Monsters) conn.TutorialGettingUp = false;
                return false;
            });
    }

    public static bool LyingOnBeach(ClientState conn) => conn.PeerTemplate is { WashedUp: true } && !conn.TutorialGettingUp;

    // Ids of the invisible SunkenPassageBlockers on the server's walk estimate (blockers only: the client spawns them).
    public const uint PassageBlockerFirstId = 0xff000000;
    // a1q4's objects have ids of their own, between the quest room objects (QuestRoomObjectFirstId) and the waypoint, so
    // they never meet the ids other code takes from NextObjectId: the nests (+ their index), the wall, the waterfall and
    // the opened nests (+ 0x20 + their index).
    public const uint GlyphObjectFirstId = 0x2e80, GlyphWallObjectId = GlyphObjectFirstId + 0x10, GlyphWaterObjectId = GlyphObjectFirstId + 0x11,
        OpenedNestFirstId = GlyphObjectFirstId + 0x20;

    // Which glyph (0–2) an item is, or null.
    public static int? GlyphIndex(Item item) => GlyphQuest.GlyphOf(Items.ByHash(item.TypeHash)?.Id);

    public static void SetPassageBlockers(ClientState conn, bool on)
    {
        if (conn.Glyphs is not { } layout) return;
        for (int i = 0; i < layout.Blockers.Count; i++)
            if (on) conn.SetBlocker(PassageBlockerFirstId + (uint)i, layout.Blockers[i].X, layout.Blockers[i].Y, GlyphQuest.BlockerSize);
            else conn.RemoveBlocker(PassageBlockerFirstId + (uint)i);
    }

    // PlacedGlyphs as the client has it changed: the wall and the waterfall play their transition (0x2bc to
    // Transitionable), and the invisible blockers leave the server's walk estimate (the client drops its own when it gets
    // the flag). Cleared (dev): the other way.
    public static async Task ChangeSubmergedPassage(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, bool open)
    {
        lock (conn.Monsters)
        {
            if (HardcoreProgressBlocked(conn) || conn.Glyphs is null) return;
            SetPassageBlockers(conn, !open);
        }
        byte state = open ? GlyphQuest.Transitioned : GlyphQuest.Untransitioned;
        foreach (var (id, what) in new[] { (conn.GlyphWallId, "Glyph Wall"), (conn.GlyphWaterId, "waterfall") })
            if (id != 0)
                await SendWorldRecord(remote, stream, ciphers, conn, id, InstanceEntities.TransitionableUpdate(id, GlyphQuest.TransitionableIndex, state),
                    $"0x02bc {what} 0x{id:x} Transitionable → {state}");
    }
}
