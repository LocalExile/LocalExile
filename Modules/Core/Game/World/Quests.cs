using System.Text.Json;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;

public static partial class Game
{
    // Sets or clears league quest flags (Quests.League ids, such as a Trial of Ascendancy's): saved with the account's stash
    // in the character's league, so every character of the league has them, then sent as the player's league flag set.
    public static async Task SetLeagueQuestFlags(Player player, IEnumerable<int> flags, bool on = true)
    {
        List<int> wanted = [.. flags], all;
        if (HardcoreProgressBlocked(player)) return;
        PersonalStashOf(player).ChangeLeagueQuestFlags(wanted, on);
        if (on) player.Character.LeagueQuestFlags.UnionWith(wanted); else player.Character.LeagueQuestFlags.ExceptWith(wanted);
        all = [.. player.Character.LeagueQuestFlags.Order()];
        Console.WriteLine($"[gateway] {player.Connection.Remote} {CharacterName(player)}: league quest flag(s) {string.Join(", ", wanted)} {(on ? "set" : "cleared")}");
        if (!player.Connection.PlayerSent) return;
        await SendWorldRecord(player, player.Id, InstanceEntities.PlayerLeagueQuestFlagsUpdate(player.Id, Quests.League.Bits(all)),
            $"0x02bc player league quest flags: {string.Join(", ", all)}");
    }

    // Sets or clears quest flags for the character (saved at once) and sends the player's flag set again; one change at a
    // time per connection (QuestGate), so a background sync never interleaves with another change.
    public static async Task SetQuestFlags(Player player, IEnumerable<string> flags, bool on = true)
    {
        if (flags is ICollection<string> { Count: 0 }) return;
        if (QuestKillCommitOutput is not null)
        {
            await SetQuestFlagsHeld(player, flags, on);
            return;
        }
        await player.Character.QuestGate.WaitAsync();
        try { await SetQuestFlagsHeld(player, flags, on); }
        finally { player.Character.QuestGate.Release(); }
    }

    public sealed record QuestFlagCommit(List<string> Changed, List<int> All,
        List<string> Effective, List<uint> Removed);

    // Gameplay and persistence commit before any notification. Caller holds the combat lock when used by simulation.
    public static QuestFlagCommit? CommitQuestFlags(Player player, IEnumerable<string> flags, bool on = true)
    {
        if (flags is ICollection<string> { Count: 0 }) return null;
        List<string>? wanted = null;
        foreach (string flag in flags)
        {
            wanted ??= [];
            if (!wanted.Contains(flag)) wanted.Add(flag);
        }
        if (wanted is null) return null;
        // Queued work cannot mutate progress after a committed Hardcore death.
        if (HardcoreProgressBlocked(player)) return null;
        var clientBefore = PlayerQuestFlags(player);
        var ownBefore = player.Character.QuestFlagsSnapshot();
        bool InstanceHas(string flag) => player.Instance?.HasState(flag) == true;
        var had = wanted.ToDictionary(f => f, f => Quests.IsInstanceFlag(f)
            ? InstanceHas(f) : Quests.ClientHas(f, ownBefore, clientBefore));
        var changed = new List<string>();
        List<string>? instanceChanges = null;
        foreach (string flag in wanted)
        {
            if (Quests.IsInstanceFlag(flag))
            {
                if (player.Instance is { } instance && (on ? instance.SetState(flag) : instance.ClearState(flag)))
                    (instanceChanges ??= []).Add(flag);
                continue;
            }
            // A flag every character is given (Quests.EveryCharacter) but has not earned yet is earned now: saved, though
            // the client may have had it (Kitava's death sets EndGameAvailable, which a hideout and the maps had).
            if (on ? player.Character.QuestFlags.Add(flag) | player.Character.UnearnedQuestFlags.Remove(flag) : player.Character.QuestFlags.Remove(flag)) changed.Add(flag);
        }
        if (changed.Count == 0 && instanceChanges is null) return null;
        if (changed.Count > 0) CharacterSaves.ChangeQuestFlags(CharacterName(player), changed, on);
        // A flag with refund points (QuestStaticRewards) was credited by the save: the balance follows it.
        if (on && changed.Any(f => QuestStaticRewards.Default.Find(f) is { RefundPoints: > 0 }))
        {
            player.Character.RefundPoints = CharacterSaves.Load(CharacterName(player))?.RefundPoints ?? player.Character.RefundPoints;
            player.Character.SavedPassiveProgress = new(player.Character.MainAscendancy, player.Character.Passives, player.Character.MasteryEffects, player.Character.RefundPoints);
        }
        if (ChangesResistancePenalty(changed) || ChangesQuestStats(changed))
        {
            player.Pools = PoolsFor(player, player.Pools.Level);
            player.ApplyMovementSpeed();
        }
        var all = PlayerQuestFlags(player);
        var own = player.Character.QuestFlagsSnapshot();
        bool Has(string flag) => Quests.IsInstanceFlag(flag) ? InstanceHas(flag) : Quests.ClientHas(flag, own, all);
        if (instanceChanges is not null) changed.AddRange(instanceChanges);
        var effective = Quests.EffectiveChanges(changed, f => had[f], Has);
        List<uint> gone = [.. player.QuestRoomObjects.Where(o => effective.Contains(o.Value.Flag) && Has(o.Value.Flag)).Select(o => o.Key)];
        foreach (uint id in gone) player.QuestRoomObjects.Remove(id);
        player.UpdateBarrier();
        if (player.QuestBlockers.Any(b => effective.Contains(b.Rule.UnblockedBy))) UpdateQuestBlockers(player);
        return new(changed, all, effective, gone);
    }

    public static async Task SetQuestFlagsHeld(Player player, IEnumerable<string> flags, bool on = true)
    {
        QuestFlagCommit? committed;
        committed = CommitQuestFlags(player, flags, on);
        if (committed is not null) await PublishQuestFlags(player, committed, on);
    }

    public static async Task PublishQuestFlags(Player player, QuestFlagCommit change, bool on = true)
    {
        foreach (string flag in change.Changed) Console.WriteLine($"[gateway] {player.Connection.Remote} {CharacterName(player)}: quest flag {flag} {(on ? "set" : "cleared")}");
        if (!player.Connection.PlayerSent) return;
        if (QuestKillCommitOutput is { } output)
            output.Add(new([], "current quest flags", Kind: PlayerMinions.OutputKind.QuestFlags));
        else
            await SendWorldRecord(player, player.Id, [], "0x02bc player quest flags (current)", atGate: () => QuestFlagPresentation(player));
        if (ChangesResistancePenalty(change.Changed) || ChangesQuestStats(change.Changed))
            await SendCharacterStats(player, PlayerStatChange.Build);
        // Quest passive and refund points (QuestStaticRewards) are in the tree's numbers.
        if (ChangesStaticRewards(change.Changed))
            await SendPassives(player);
        await UpdateShownTransitions(player, $"quest flags {string.Join(", ", change.Effective)}");
        if (change.Effective.Any(Quests.IsInstanceFlag))
        {
            var instanceFlags = change.Effective.Where(Quests.IsInstanceFlag).ToArray();
            foreach (var other in player.Instance.Presence.PlayersExcept(player.Id))
                QueueDoorRefresh(other, instanceFlags);
        }
        await UpdateQuestNpcs(player, $"quest flags {string.Join(", ", change.Effective)}");
        if (change.Effective.Contains(GlyphQuest.PlacedGlyphs))
            await ChangeSubmergedPassage(player, ClientHasQuestFlag(player, GlyphQuest.PlacedGlyphs));
        // The quest engine's objects and chests whose state follows a flag (World/Quest/QuestEngine.cs).
        await UpdateQuestObjectStates(player, change.Effective);
        foreach (uint id in change.Removed)
            await SendWorldRemoval(player, id, $"0x02bd remove room object 0x{id:x} (quest flag set)", atGate: () =>
            { return player.Closed || player.LeavingArea || player.QuestRoomObjects.ContainsKey(id) ? null : InstanceProtocol.RemoveObject(id); });
        if (on) await CharacterVoiceLines.OnQuestFlagsPublishedHeld(player, change.Effective);
    }

    // Whole flag sets are projections, not snapshots: combat can commit while a normal setter awaits the cipher gate.
    // These pure projections never take QuestGate, so QuestGate -> cipher -> Monsters keeps its existing lock order.
    static byte[]? QuestFlagPresentation(Player player)
    {
        return player.Closed || player.LeavingArea || !player.Connection.PlayerSent ? null
            : InstanceEntities.PlayerQuestFlagsUpdate(player.Id, PlayerQuestFlags(player));
    }

    static OutputBatch? QuestTransitionRecords(Player player, WorldAdmission admission, uint id, AreaTransitionObject transition)
    {
        if (player.Closed || player.LeavingArea || !player.Layout.AreaTransitions.Any(e => e.Id == id && ReferenceEquals(e.Transition, transition))) return null;
        var records = new WorldRecordBuilder(player, admission);
        if (TransitionShown(player, id, transition))
        {
            records.Create(id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(transition.Type), transition.Blob()));
            records.Presentation(id, InstanceProtocol.EnterWakeZone(id));
        }
        else records.Remove(id);
        return records.Build();
    }

    // The quest flag an area transition needs (Quests.FlagToEnter), or null. Such an exit is created only once the character
    // has the flag: The Twilight Strand's gate into Lioneye's Watch appears when Hillock dies (a1q1, 12-quests.md §6).
    public static string? TransitionFlag(Player player, AreaTransitionObject transition) =>
        // The plaza's way into the Labyrinth leads to the day's first room: shown once its door is open (ChooseLabyrinth).
        player.Layout.Id == LabyrinthDay.Plaza && transition.SourceType == LabyrinthDay.Entrance ? LabyrinthDay.PlazaDoorOpen
        // Izaro's arena's ways out (the layout's disabled ones): shown once his trial in this instance is won; the way in
        // once the corridor's gate is open.
        : transition.LayoutDisabled && LabyrinthDay.IzaroRoom(player.Layout.Id, player.Seed) is not null ? IzaroFights.Beaten
        : transition.SourceType == IzaroFights.ArenaTransition && LabyrinthDay.IzaroRoom(player.Layout.Id, player.Seed) is not null ? IzaroFights.GateOpen
        : transition.LayoutDisabled && transition.TeleportTo.HasValue && QuestsOf(player).DisabledTeleportFlag(player.Layout.Id) is { } locked ? locked
        : AreaTransitionCatalog.IdOf(transition.DestinationHash16) is { } dest ? QuestsOf(player).FlagToEnter(player.Layout.Id, dest) : null;

    // Whether a Labyrinth tier's trials are done (Labyrinths RequiredTrials: Normal's six, …; the character's flag or the
    // league's, LabyrinthTrials).
    public static bool LabyrinthTrialsDone(Player player, int tier, out int done, out int count)
    {
        var trials = LabyrinthTrials.Trials.Where(t => LabyrinthTrials.Tier(t) == tier).ToList();
        done = trials.Count(t => player.Character.HasQuestFlag(t.Flag) || player.Character.LeagueQuestFlags.Contains(t.LeagueFlag));
        count = trials.Count;
        return count > 0 && done == count;
    }

    public static string? LabyrinthAdmissionFailure(Player player, int tier)
    {
        if (tier is not (1 or 2)) return "this difficulty is not supported";
        if (tier == 2 && player.Pools.Level < 40) return "requires level 40";
        if (tier == 2 && !player.Character.HasQuestFlag("LabyrinthComplete1")
            && !player.Character.MainAscendancy.EarnedAwards.Contains(global::AscendancyRules.AwardKeys[0]))
            return "requires completing the Normal Labyrinth";
        return LabyrinthTrialsDone(player, tier, out int done, out int count)
            ? null : $"requires all {count} trials ({done} completed)";
    }

    public static string? OpenLabyrinthEntrance(Player player, int tier, uint day)
    {
        if (player.Layout.Id != LabyrinthDay.Plaza || player.LabyrinthEntry is not null || player.Closed || player.LeavingArea)
            return "the plaza entrance cannot be changed";
        if (LabyrinthAdmissionFailure(player, tier) is { } failure) return failure;

        var (id, portal) = player.Layout.AreaTransitions.FirstOrDefault(t => t.Transition.SourceType == LabyrinthDay.Entrance);
        if (portal is null || LabyrinthDay.EntryFor(day, tier) is not { } entry) return "the first room is unavailable";
        player.ReplaceAreaTransition(id, portal with { DestinationHash16 = entry.Destination });
        player.LabyrinthEntry = entry;
        return null;
    }

    // C→S 0x212: the plaza's choice opens a door and shows the selected day's first room for this player.
    public static async Task ChooseLabyrinth(Player player, byte tier)
    {
        if (player.Layout.Id != LabyrinthDay.Plaza) return;
        string? failure;
        try { failure = OpenLabyrinthEntrance(player, tier, LabyrinthDay.Seed); }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
        { failure = ex.Message; }
        if (failure is not null)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0212 Labyrinth {tier} refused: {failure}");
            return;
        }
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0212 Labyrinth {tier} chosen");
        await UpdateQuestObjectStates(player, [LabyrinthDay.PlazaDoorOpen]);
        await UpdateShownTransitions(player, "the Labyrinth's door opens");
    }

    // The quest tables that run for this player: those of the modules its league lists (Quests.For; empty without
    // QuestObjects). The area's layout is the same for every league, so what the tables add or change there is decided
    // here, per player (TransitionShown).
    public static Quests.RuleSet QuestsOf(Player player) => Quests.For(ModulesFor(player));

    // The flag a dialogue row sets when chosen: its QuestFlag4, unless the player's tables say only an event sets it.
    public static string? TalkStartFlag(Player player, NpcCatalog.Talk talk) => QuestsOf(player).TalkStartFlag(talk);

    // Shown: its tile type exists for the character (Quests.ExistsRule: the Control Blocks' exit to Oriath Square before
    // Avarius, the one to The Ruined Square after), and its quest flag is set or a script opened it on this instance.
    // An exit a quest added to the shared layout (a gated disabled entrance, a quest portal, a swapped exit's version:
    // AreaTransitionObject.QuestSource) stands only where that source's tables run; one a quest replaced
    // (QuestReplaced: the exit without a model a portal stands in for, a swapped exit's unchanged version) only where
    // they do not, so a league without them has the layout as generated.
    public static bool TransitionShown(Player player, uint id, AreaTransitionObject transition)
    {
        if (player.Layout.Id == LabyrinthDay.Plaza && transition.SourceType == LabyrinthDay.Entrance)
            return player.LabyrinthEntry?.Destination == transition.DestinationHash16;
        // A gate into an ExtraTerrainFeatures sub-area stands only where a module of the league fills it.
        if (transition.Feature is { } feature && !GameEvents.FillsFeature(player, feature)) return false;
        if (Sirus.GateShown(player, transition) is bool sirusShown) return sirusShown;
        if (DepravedTrinity.GateShown(player, id, transition) is bool shown) return shown;
        if (KitavaActFive.GateShown(player, transition) is bool kitavaShown) return kitavaShown;
        if (KitavaActTen.GateShown(player, transition) is bool finalKitavaShown) return finalKitavaShown;
        // The Reverie Device's six consumable portals replace the old permanent quest exit.
        if (QuestsOf(player).Includes("QuestObjects") && player.Layout.Id == MapDevice.CampaignDeviceArea
            && transition.DestinationHash16 == AreaTransitionCatalog.HashOf(MapDevice.CampaignMapArea)) return false;
        var quests = QuestsOf(player);
        if (transition.QuestSource is { } source && quests.Includes(source) == transition.QuestReplaced) return false;
        return quests.Exists(transition.SourceType, f => ClientHasQuestFlag(player, f))
            && (player.Connection.ScriptShownTransitions.Contains(id) || TransitionFlag(player, transition) is not { } flag || ClientHasQuestFlag(player, flag));
    }

    // The area transitions whose shown state changed (a quest flag, an NPC script): created on the client or removed.
    public static async Task UpdateShownTransitions(Player player, string why)
    {
        if (!player.Connection.PlayerSent) return;
        // Whether each should stand now (outside the lock: it reads the flags), then, under one lock, the ones whose state
        // the client has differently, recorded as sent: of two updates at once only one creates or removes an exit.
        var want = player.Layout.AreaTransitions.Select(t => (t.Id, t.Transition, Shown: TransitionShown(player, t.Id, t.Transition))).ToList();
        List<(uint Id, AreaTransitionObject Transition, bool Shown)> changed;
        lock (player.Connection.ShownTransitions)
        {
            changed = [.. want.Where(t => t.Shown != player.Connection.ShownTransitions.Contains(t.Id))];
            foreach (var (id, _, shown) in changed) { if (shown) player.Connection.ShownTransitions.Add(id); else player.Connection.ShownTransitions.Remove(id); }
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
                await SendWorldCreate(player, id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(transition.Type), transition.Blob()),
                    $"0x02bb create area transition 0x{id:x} {transition} ({why})", wake: true, awaitBirth: false);
            }
            else await Game.SendWorldRemoval(player, id, $"0x02bd remove area transition 0x{id:x} ({why})");
        }
    }

    // A quest flag as the client has it: the character's own, or one the World panel's access flags carry (an activated
    // waypoint's area, 16-waypoints.md §4: the Submerged Passage's waypoint brings a1q4-PlacedGlyphs). The client opens
    // its ways by these, so the server's checks follow them.
    public static bool ClientHasQuestFlag(Player player, string flag)
    {
        if (Quests.IsInstanceFlag(flag))
            return flag == LabyrinthDay.PlazaDoorOpen ? player.LabyrinthEntry is not null : player.Instance.HasState(flag);
        if (player.Character.HasQuestFlag(flag)) return true;
        if (Quests.Rows([flag]) is not [int row]) return false;
        bool inHideout = HideoutCatalog.ByArea(player.Layout.Hash) is not null || player.Layout.Hash == StashCatalog.DelveHub;
        if (player.Character.UnearnedQuestFlags.Contains(flag) && Quests.EndgameOnly.Contains(flag)
            && (inHideout || MapDevices.IsMapArea(player.Layout.Hash))) return true;
        // Combat reads individual flags for every Pantheon/stat calculation. Keep the World panel's access and visit
        // semantics without rebuilding, sorting and allocating its complete flag list for each membership test.
        lock (player.Character.Waypoints)
        {
            var world = WorldQuestFlagLookups.GetOrCreateValue(player);
            if (!world.Waypoints.SetEquals(player.Character.Waypoints) || !world.Visited.SetEquals(player.Character.Visited))
            {
                world.Waypoints = [.. player.Character.Waypoints];
                world.Visited = [.. player.Character.Visited];
                world.Rows.Clear();
                foreach (var area in WaypointCatalog.Areas.Entries)
                    if (player.Character.Waypoints.Contains(area.Id)) world.Rows.UnionWith(area.AccessFlags ?? []);
                foreach (string area in player.Character.Visited) world.Rows.UnionWith(WaypointCatalog.Visits.For(area));
                foreach (string area in player.Character.Waypoints) world.Rows.UnionWith(WaypointCatalog.Visits.For(area));
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
    static readonly Store<Player, WorldQuestFlagLookup> WorldQuestFlagLookups = new();

    // The first ground click in WashedUp sends this action instead of Move, and any other request to move gets it from the
    // server (0x141e9b860). It plays getup in place; treating a target as a walk would move only the server's position estimate.
    public static async Task WakeUpOnBeach(Player player,
        (int X, int Y)? target, ushort sequence, ushort flags, double requestedAt)
    {
        int action;
        (int X, int Y) here;
        double getupSeconds = 0;
        using (ProjectileMutation(player))
        {
            double now = ProjectileWorldTime(player);
            if (player.Closed || player.LeavingArea || player.PlayerLife == 0 || !LyingOnBeach(player)
                || PlayerActionBlocked(player, requestedAt, now)) return;
            requestedAt = Math.Max(requestedAt, player.ProjectileRuntime?.Runtime.Now / 120000d ?? requestedAt);
            player.TutorialGettingUp = true;
            action = Interlocked.Increment(ref player.ActionCount);
            // The client plays its class's getup clip in place (0x141c6fd70, speed 1) and takes nothing else until it
            // ends (0x141c6fe10): hold provisionally here and anchor to the send frame's countdown in atGate so a Move
            // sent during getup never arrives before the client finishes getting up (which would discard its path).
            string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject;
            if (Animations.ClipLengthMs(ao, "getup") is int getup)
            {
                getupSeconds = getup / 1000.0;
                player.LockstepBusyUntil = Math.Max(player.LockstepBusyUntil, AreaTime(player) + getupSeconds + player.LockstepActionTail);
            }
            here = player.Walk.Position(requestedAt);
            player.Walk.Order(here.X, here.Y, requestedAt);
            player.ChannelSkill = 0;
        }
        var (x, y) = target ?? here;
        await SendWorldRecord(player, player.Id, [],
            $"0x01ab player gets up on the beach at ({here.X}, {here.Y})",
            still: () =>
            {
                if (PlayerActionCurrent(player, action)) return true;
                // Dropped: the client is still down, so its next request tries again.
                player.TutorialGettingUp = false;
                return false;
            },
            atGate: () =>
            {
                if (getupSeconds > 0 && player.Connection.LockstepClient && player.Connection.LockstepFrame is { } frame
                    && LockstepCountdownFrames((float)getupSeconds) is { } frames)
                    player.LockstepBusyUntil = Math.Max(player.LockstepBusyUntil, frame.Start + (frames + 1) * (frame.End - frame.Start));
                return InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, x, y, TutorialObjects.WashedUpSkillId, sequence, flags);
            });
    }

    public static bool LyingOnBeach(Player player) => player.PeerTemplate is { WashedUp: true } && !player.TutorialGettingUp;

    // C→S 0x020a: a tutorial step or Help Panel article completed or dismissed (u8 Tutorial.dat row).
    // Row 17 (AllTutorials, from "Skip All Tutorials" or Options → "Disable Tutorials") sets every tutorial completion
    // flag (rows 0..18, including CompletedSkillGemTutorial checked by TutorialManager at 0x140d46d50) plus
    // TutorialPanelHideArticlePopups (row 77, checked by the Help Panel at 0x140a901ec/0x140a910e3/0x140a92dca); every
    // other row sets its own QuestFlagComplete.
    public static async Task CompleteTutorial(Player player, byte row)
    {
        if (Quests.TutorialFlag(row) is not { } flag)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x020a tutorial row {row}: no completion flag, ignored");
            return;
        }
        IReadOnlyList<string> flags = flag == Quests.CompletedAllTutorials ? Quests.AllTutorialFlags : [flag];
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x020a tutorial row {row} -> {(flags.Count == 1 ? flag : $"{flag} ({flags.Count} flags)")}");
        await SetQuestFlags(player, flags);
    }

    // Ids of the invisible SunkenPassageBlockers on the server's walk estimate (blockers only: the client spawns them).
    public const uint PassageBlockerFirstId = 0xff000000;
    // a1q4's objects have ids of their own, between the quest room objects (QuestRoomObjectFirstId) and the waypoint, so
    // they never meet the ids other code takes from NextObjectId: the nests (+ their index), the wall, the waterfall and
    // the opened nests (+ 0x20 + their index).
    public const uint GlyphObjectFirstId = 0x2e80, GlyphWallObjectId = GlyphObjectFirstId + 0x10, GlyphWaterObjectId = GlyphObjectFirstId + 0x11,
        OpenedNestFirstId = GlyphObjectFirstId + 0x20;

    // Which glyph (0–2) an item is, or null.
    public static int? GlyphIndex(Item item) => GlyphQuest.GlyphOf(Items.ByHash(item.TypeHash)?.Id);

    public static void SetPassageBlockers(Player player, bool on)
    {
        if (player.Glyphs is not { } layout) return;
        for (int i = 0; i < layout.Blockers.Count; i++)
            if (on) player.SetBlocker(PassageBlockerFirstId + (uint)i, layout.Blockers[i].X, layout.Blockers[i].Y, GlyphQuest.BlockerSize);
            else player.RemoveBlocker(PassageBlockerFirstId + (uint)i);
    }

    // PlacedGlyphs as the client has it changed: the wall and the waterfall play their transition (0x2bc to
    // Transitionable), and the invisible blockers leave the server's walk estimate (the client drops its own when it gets
    // the flag). Cleared (dev): the other way.
    public static async Task ChangeSubmergedPassage(Player player, bool open)
    {
        if (HardcoreProgressBlocked(player) || player.Glyphs is null) return;
        SetPassageBlockers(player, !open);
        byte state = open ? GlyphQuest.Transitioned : GlyphQuest.Untransitioned;
        foreach (var (id, what) in new[] { (player.GlyphWallId, "Glyph Wall"), (player.GlyphWaterId, "waterfall") })
            if (id != 0)
                await SendWorldRecord(player, id, InstanceEntities.TransitionableUpdate(id, GlyphQuest.TransitionableIndex, state),
                    $"0x02bc {what} 0x{id:x} Transitionable → {state}");
    }
}
