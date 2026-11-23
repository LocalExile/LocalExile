using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using PlayerWalk = GameServer.Core.World.PlayerWalk;

public static partial class Game
{
    // Reserve an object walk and its original token atomically with the same recovery/read-time check as skills.
    // An interaction dispatched before a hit may not capture a new token after that hit cancels the old action.
    public static bool TryBeginObjectWalk(Player player, double requestedAt, int x, int y, double reach,
        out (int Action, double Now, (int X, int Y) Here, (int X, int Y) Stop, double Arrive) started,
        bool centreRange = false, int tolerance = -1)
    {
        started = default;
        using (ProjectileMutation(player))
        {
            double now = ProjectileWorldTime(player);
            if (player.Closed || player.PlayerLife == 0 || PlayerActionBlocked(player, requestedAt, now)) return false;
            CancelMeleeRepeats(player, now);
            int action = Interlocked.Increment(ref player.ActionCount);
            player.ChannelSkill = 0;
            var (here, stop, arrive) = centreRange ? player.Walk.Approach(x, y, reach, now, tolerance) : player.Walk.WalkUpTo(x, y, (int)reach, now);
            started = (action, now, here, stop, arrive);
            return true;
        }
    }

    // An area transition click. Plain: change area (0x2b4 now, the move itself once the client acknowledges with 0x2cd).
    // With Ctrl: list the destination's instances (0xae) for the client's instance chooser.
    public static async Task OnUseObject(Player player,
        InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (PlayerActionBlocked(player, requestedAt, AreaTime(player))) return;
        if (SkillsFor(player).IsGemSkill(use.SkillId))
        {
            var requested = GemSkillEffects(player).FirstOrDefault(g => g.Id == use.SkillId);
            if (requested.Name is not null && GemSkillInstanceDisabled(player, requested.Effect, use.SkillId) is not null) return;
        }
        if (LyingOnBeach(player))
        {
            await WakeUpOnBeach(player, null, TutorialObjects.GetUpSequence, 0, requestedAt);
            return;
        }
        if (InTown(player) && (SkillsFor(player).IsGemSkill(use.SkillId) || use.SkillId == SkillCatalog.FirstSkillId)) return;
        if (SkillsFor(player).IsGemSkill(use.SkillId) || use.SkillId == SkillCatalog.FirstSkillId)
            await EndGracePeriod(player, action: true);
        // An encounter actor can acquire an NPC component's dialogue without changing its monster identity.
        if (use.SkillId == 0x266)
        {
            SpawnedNpc? speaker;
            lock (player.Npcs) speaker = player.Npcs.GetValueOrDefault(use.TargetId);
            if (speaker is null || speaker.Npc.Id == BanditChoices.In(player)?.Variety)
                speaker = BanditChoices.Speaker(player, use.TargetId);
            if (speaker is not null)
            {
                await TalkToNpc(player, use, speaker, requestedAt);
                return;
            }
        }
        SpawnedMonster? target;
        target = player.Area.Monsters.GetValueOrDefault(use.TargetId);
        if (target is not null)
        {
            if (use.SkillId != SkillCatalog.FirstSkillId) await CastOn(player, use, target, requestedAt);
            else if (target.Hostile) await Attack(player, use, target, requestedAt);   // not the player's own minion
            return;
        }
        // A self-cast skill (Righteous Fire) targets the player itself: echo it in place, then cast.
        if (use.TargetId == player.Id && SkillsFor(player).IsGemSkill(use.SkillId))
        {
            var gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == use.SkillId);
            double at = AreaTime(player);
            (int X, int Y) self = default;
            if (!TryBeginSkillCooldownUse(player, use.SkillId, gem.Effect,
                gem.Name is null ? null : CooldownForSkill(player, use.SkillId), false,
                () =>
                {
                    Interlocked.Increment(ref player.ActionCount);
                    self = player.Walk.Position(at);
                    player.Walk.Order(self.X, self.Y, at);
                }, out var cooldownUse, requestedAt: requestedAt)) return;
            bool acknowledged = false;
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionStarted(player.Id, self.X, self.Y, self.X, self.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: player.Id),
                $"0x01ab player skill 0x{use.SkillId:x4} cast on itself at ({self.X}, {self.Y})",
                still: () => acknowledged = SkillAcknowledgementAllowed(player, gem.Effect, use.SkillId,
                    () => cooldownUse?.CommitNativeUse(player, use.SkillId) ?? true));
            if (!acknowledged) return;
            await SendNativeCooldownRefresh(player);
            await CastGemSkill(player, use.SkillId, self, self, player.Id, sequence: use.Sequence,
                actionToken: cooldownUse?.ActionToken, cooldownUse: cooldownUse);
            return;
        }
        if (player.Instance.Ground.Get(use.TargetId) is { } ground)
        {
            if (await AnimateGroundItem(player, ground, use, requestedAt)) return;
            if (await GameEvents.AnyGroundItemUse(player, use.TargetId, ground, use, requestedAt)) return;
            await PickUpGroundItem(player, use.TargetId, ground, use, requestedAt);
            return;
        }
        if (use.SkillId == 0x266 && player.Instance.Ground.WasTaken(use.TargetId))
        {
            await Send(player, [], $"remove stale ground label 0x{use.TargetId:x}", atGate: () =>
                player.Instance.Ground.WasTaken(use.TargetId) ? InstanceProtocol.RemoveObject(use.TargetId) : null);
            return;
        }
        // Objects of a module or mod.
        if (await GameEvents.AnyObjectUse(player, use, requestedAt)) return;
        if (use.TargetId == WaypointCatalog.ObjectId && player.Layout.Waypoint is { } waypoint)
        {
            await UseWaypoint(player, use, waypoint, requestedAt);
            return;
        }
        SpawnedNpc? npc;
        lock (player.Npcs) npc = player.Npcs.GetValueOrDefault(use.TargetId);
        if (npc is not null)
        {
            await TalkToNpc(player, use, npc, requestedAt);
            return;
        }
        if (player.StashObject != 0 && use.TargetId == player.StashObject)
        {
            await OpenStash(player, use, requestedAt);
            return;
        }
        if (player.GuildStashObject != 0 && use.TargetId == player.GuildStashObject)
        {
            await OpenGuildStash(player, use, requestedAt);
            return;
        }
        var (transitionId, transition) = player.Layout.AreaTransitions.FirstOrDefault(e => e.Id == use.TargetId);
        if (transition is null)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0024 use object {use} (not an area transition)");
            return;
        }
        transition = KitavaActTen.PortalFor(player, transition);
        ushort dest = transition.DestinationHash16;
        // A quest can close an exit: The Twilight Strand's way into Lioneye's Watch needs Hillock dead (a1q1).
        if (!TransitionShown(player, transitionId, transition))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0024 area transition 0x{use.TargetId:x} to {AreaTransitionCatalog.IdOf(dest)}: closed " +
                $"(quest flag {TransitionFlag(player, transition)})");
            return;
        }
        // The client walks to the area transition first; follow it and act once the player is there (a move meanwhile cancels).
        if (!TryBeginObjectWalk(player, requestedAt, transition.GridX, transition.GridY, AreaTransitionObjectSize, out var objectWalk)) return;
        var (action, now, here, stop, arrive) = objectWalk;
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0024 area transition 0x{use.TargetId:x} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now) await AreaDelay(player, arrive - now);
        if (!PlayerActionCurrent(player, action)) return;
        if (!PlayerWalk.InWalkUpRange(stop, (transition.GridX, transition.GridY), AreaTransitionObjectSize))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} area transition 0x{use.TargetId:x} out of reach from ({stop.X}, {stop.Y})");
            return;
        }
        // A gate within the area (a map's boss arena): next to its pair, in the same instance.
        if (transition.TeleportTo is { } pair)
        {
            var (tx, ty) = GateArrival(player.Layout, transition);
            using (ProjectileMutation(player))
            {
                if (!PlayerActionCurrent(player, action)) return;
                player.Walk.Teleport(tx, ty, ProjectileWorldTime(player));
            }
            await SendTransitionFade(player, (tx, ty), $"the gate 0x{use.TargetId:x}");
            if (await SendWorldRecord(player, player.Id, () => InstanceProtocol.Teleport(player.Id, tx, ty),
                $"0x01b7 player through the gate 0x{use.TargetId:x} to ({tx}, {ty}), destination gate at ({pair.X}, {pair.Y})",
                still: () => PlayerActionCurrent(player, action)))
                await GameEvents.RunLocalGateUsed(player);
            return;
        }
        bool labyrinthEntry = player.Layout.Id == LabyrinthDay.Plaza && transition.SourceType == LabyrinthDay.Entrance;
        if (use.Ctrl && !labyrinthEntry)
        {
            await ShowInstances(player, dest, atWaypoint: false,
                arrivalEntrance: AreaTransitionCatalog.PairedEntrance(player.Layout.Hash, dest, transition.Entrance));
            return;
        }
        // A Labyrinth run's ways (the plaza's way in, its rooms' exits): each room its own instance (LabyrinthDay.Transfer).
        var transfer = LabyrinthDay.Transfer(CharacterName(player), player.Layout.Id, player.Seed, transition.SourceType, dest,
            AreaTransitionCatalog.IdOf(dest), transition.Entrance, player.LabyrinthEntry);
        if (labyrinthEntry && transfer is null) return;
        transfer ??= new PendingTransfer(dest, 0, false)
        { ArrivalEntrance = AreaTransitionCatalog.PairedEntrance(player.Layout.Hash, dest, transition.Entrance) };
        await ChangeArea(player, transfer, use.ToString());
    }

    // karui_fortress_01/02: miniboss.arm has two bridges in one graph node. Its four telepad
    // points are in file order: arena, exterior, arena, exterior. Each pair belongs to one bridge.
    internal static void PrepareKaruiBridgeGates(Player player)
    {
        var layout = player.Layout;
        if (layout.Id != "2_6_5" || layout.Level is not { } level) return;
        var room = level.Records.SelectMany(r => r.Rooms).FirstOrDefault(r =>
            r.Room.Path.Equals("Metadata/Terrain/Act6/Area5/Rooms/miniboss.arm", StringComparison.OrdinalIgnoreCase));
        if (room is null) return;
        var pads = room.Room.Points.Where(p => p.Name == "telepad").Select(p =>
        {
            var (x, y) = GameServer.Data.Terrain.Generation.Orientation.Map(room.Orientation, (int)p.X, (int)p.Y,
                room.Room.Width * 23, room.Room.Height * 23);
            return (X: room.X * 23 + x, Y: room.Y * 23 + y);
        }).ToArray();
        var gates = layout.AreaTransitions.Where(g => g.Transition.TeleportTo.HasValue
            && g.Transition.Model?.Contains("/swamp_longbridge_v01_01_area_transition", StringComparison.OrdinalIgnoreCase) == true).ToArray();
        if (pads.Length != 4 || gates.Length != 4) return;
        var at = pads.Select(p => gates.MinBy(g => Dist(p, (g.Transition.GridX, g.Transition.GridY)))).ToArray();
        if (at.Select(g => g.Id).Distinct().Count() != pads.Length) return;
        uint? keep = gates.Select(g => g.Transition.Label).FirstOrDefault(label => label.HasValue);
        for (int i = 0; i < pads.Length; i++)
        {
            bool entering = i % 2 == 1;
            var (id, gate) = at[i];
            var destination = pads[entering ? i - 1 : i + 1];
            player.ReplaceAreaTransition(id, gate with { TeleportTo = destination, Label = entering ? keep : null });
        }
    }

    // Arakaali's return gate and telepad are on the balcony outside her closed web. Until the encounter's scripted
    // entrance is modelled, the arena entrance must land on the boss room's floor, rather than beside that return gate.
    // Use the placed room's footprint so every seed and orientation supplies its own coordinates.
    // S→C 0xcb with s seconds queues a move on the client (FUN_14218ba10): the screen darkens over 0.8 s, the move
    // lands after 2 s of world time, then the screen clears over 1.6 s (FUN_141659f70), so one alone stays black for
    // 1.2 s. A second one sent right after replaces the darkening's length while it has not begun (FUN_1410c3720), so:
    // the first (the move) clears from 2 × 0.18 = 0.36 s to 0.65 s, the second darkens over 0.8 × 0.25 = 0.2 s, and its own
    // end (0.5 s) falls in the clearing, where it starts no other (traced live). The teleport goes once the screen is black
    // (0.2 s): it applies at a frame's end, so one sent at the move's end landed while the screen was clearing.
    // Not measured against GGG's.
    const float TransitionMoveSeconds = 0.18f, TransitionDarkenSeconds = 0.25f;

    /// <summary>The screen's fade as the player goes through a way within the area (a gate, a trial's return portal):
    /// S→C 0xcb with its fade bit, then a wait for the screen to go black, so the teleport sent next lands while it is.</summary>
    public static async Task SendTransitionFade(Player player,
        (int X, int Y) to, string through)
    {
        await Send(player, [.. InstanceProtocol.FadeMove(player.Id, to.X, to.Y, TransitionMoveSeconds),
                .. InstanceProtocol.FadeMove(player.Id, to.X, to.Y, TransitionDarkenSeconds)],
            $"0x00cb fade through {through} to ({to.X}, {to.Y}), {TransitionMoveSeconds} s and {TransitionDarkenSeconds} s");
        double until = AreaTime(player) + 0.8 * TransitionDarkenSeconds;
        while (!player.Closed && AreaTime(player) < until) await AreaDelay(player, 0.016);
    }

    public static (int X, int Y) GateArrival(AreaLayout layout, AreaTransitionObject transition)
    {
        if (transition.TeleportTo is not { } pair) throw new ArgumentException("The transition is not an intra-area gate.", nameof(transition));
        if (layout.Id == "2_7_12_2" && transition.SourceType == "Metadata/QuestObjects/BossArenaEntranceTransition"
            && layout.Level?.Records.SelectMany(r => r.Rooms).LastOrDefault(r =>
                r.Room.Path.EndsWith("/boss.arm", StringComparison.OrdinalIgnoreCase)
                && pair.X >= r.X * 23 && pair.X < (r.X + r.Width) * 23
                && pair.Y >= r.Y * 23 && pair.Y < (r.Y + r.Height) * 23) is { } room)
        {
            var centre = (X: room.X * 23 + room.Width * 23 / 2, Y: room.Y * 23 + room.Height * 23 / 2);
            if (layout.WalkableFor(centre.X, centre.Y, 1)) return centre;
            var near = layout.Near(centre);
            if (near.X >= room.X * 23 && near.X < (room.X + room.Width) * 23
                && near.Y >= room.Y * 23 && near.Y < (room.Y + room.Height) * 23 && layout.WalkableFor(near.X, near.Y, 1)) return near;
        }
        return layout.Near(pair);
    }

    // The destination's instances (0xae) for the client's instance chooser; its pick (0xaf) changes area.
    public static async Task ShowInstances(Player player, ushort dest, bool atWaypoint,
        int? arrivalEntrance = null)
    {
        var list = await InstanceControl.CallAsync(new ControlRequest { Op = "list", Ticket = player.Ticket, Area = dest });
        var instances = list.Instances ?? [];
        var entries = instances
            .Select(i => new InstanceTransfer.InstanceListEntry((ulong)i.CreatedUnix, (uint)i.SecondsLeft, 0, i.Players)).ToList();
        player.Chooser = new ChooserList(dest, [.. instances.Select(i => i.Id)], atWaypoint, arrivalEntrance);
        await Send(player, InstanceTransfer.InstanceList(dest, entries),
            $"0x00ae instance list for 0x{dest:x4}: {entries.Count} instance(s) [{string.Join(", ", instances.Select(i => $"{i.Id} ({i.SecondsLeft} s left): {string.Join("/", i.Players)}"))}]");
    }

    // A waypoint click (C→S 0x24 on the waypoint, Agents/docs/protocol/16-waypoints.md): walk there, activate it if it is
    // not yet, then open the World panel for travel (S→C 0x7b kind 0). The panel's pick arrives as C→S 0x4d.
    public static async Task UseWaypoint(Player player,
        InstanceTransfer.UseSkillOnObject use, WaypointObject waypoint, double requestedAt)
    {
        if (!TryBeginObjectWalk(player, requestedAt, waypoint.GridX, waypoint.GridY, WaypointObjectSize, out var objectWalk)) return;
        var (action, now, here, stop, arrive) = objectWalk;
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0024 waypoint ({use}) from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now) await AreaDelay(player, arrive - now);
        if (!PlayerActionCurrent(player, action)) return;
        if (!PlayerWalk.InWalkUpRange(stop, (waypoint.GridX, waypoint.GridY), WaypointObjectSize))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} waypoint out of reach from ({stop.X}, {stop.Y})");
            return;
        }
        await ActivateWaypoint(player, "clicked");
        await Send(player, InstanceWaypoints.OpenUi(InstanceWaypoints.WorldPanelUi, WaypointCatalog.ObjectId),
            "0x007b open the World panel for waypoint travel", still: () => PlayerActionCurrent(player, action));
    }

    // Activates this area's waypoint for the character (saved, added to the saved ones) and tells the client: a new 0x4c with
    // the area's bit set plays the waypoint's activation. Towns activate on entry, other waypoints when the player comes near
    // or clicks them.
    public static async Task ActivateWaypoint(Player player, string why)
    {
        if (WaypointCatalog.Areas.Find(player.Layout.Hash) is not { } entry) return;
        if (HardcoreProgressBlocked(player)) return;
        lock (player.Character.Waypoints) if (!player.Character.Waypoints.Add(entry.Id)) return;
        CharacterSaves.AddWaypoints(CharacterName(player), [entry.Id]);
        await SendWaypointList(player, $"waypoint of {entry.Name} ({entry.Id}) activated: {why}");
    }

    // After the player is in: a town's waypoint activates on entry, then the character's waypoints (S→C 0x4c).
    public static async Task SendWaypoints(Player player)
    {
        if (WaypointCatalog.Areas.Entries.Count == 0) return;
        if (WaypointCatalog.Areas.Find(player.Layout.Hash) is { IsTown: true } town)
        {
            if (HardcoreProgressBlocked(player)) return;
            bool added;
            lock (player.Character.Waypoints) added = player.Character.Waypoints.Add(town.Id);
            if (added) CharacterSaves.AddWaypoints(CharacterName(player), [town.Id]);
        }
        await SendWaypointList(player, "the character's waypoints");
    }

    public static async Task SendWaypointList(Player player, string why)
    {
        var entries = WaypointCatalog.Areas.Entries;
        List<int> bits;
        // The World panel's Labyrinth pin (SpecialMapPins Labyrinth) shows only with its area's waypoint bit: always set.
        lock (player.Character.Waypoints) bits = [.. Enumerable.Range(0, entries.Count)
            .Where(i => player.Character.Waypoints.Contains(entries[i].Id) || entries[i].Id == WaypointCatalog.LabyrinthPlaza)];
        // The pins need their areas' access quest flags first (the player object carried the ones known at creation).
        if (player.Connection.PlayerSent)
        {
            await Send(player, [], "0x02bc player quest flags (waypoints, current)", atGate: () => QuestFlagPresentation(player));
        }
        await Send(player, InstanceWaypoints.Waypoints(bits), $"0x004c waypoints: {bits.Count} activated ({why})");
    }

    // The player's quest flags as the client keeps them (QuestFlags rows, 16-waypoints.md §3): the flags the character's
    // quests set (12-quests.md §5; the client checks them too: Lioneye's Watch is accessible only with a1q1-KilledHillock),
    // and what the World panel needs (16-waypoints.md §4): the visit flags of every area entered or whose waypoint is
    // activated (they reveal the neighbouring pins and so the connection lines), and the access flags of the activated
    // waypoints' areas (their pins become travel targets).
    public static List<int> PlayerQuestFlags(Player player)
    {
        // ThisIsMyHideout only in a hideout and the Mine Encampment, where Niko needs it for his own options; elsewhere NPCs
        // show their "not my hideout" rows. (It does not hide Sell Items: the client offers Sell only in towns and hideouts.)
        bool inHideout = HideoutCatalog.ByArea(player.Layout.Hash) is not null || player.Layout.Hash == StashCatalog.DelveHub;
        // The quest flags as a snapshot, taken before the Waypoints lock (Player.QuestFlagsSnapshot): the two locks are
        // never held together. The endgame flags not earned yet (Quests.EndgameOnly: EndGameAvailable before Kitava) only
        // in a hideout and a map area, where the map device and the maps need them.
        var own = player.Character.QuestFlagsSnapshot();
        BanditChoices.AddDialogueFlags(player, own);
        string[] endgame;
        endgame = [.. Quests.EndgameOnly.Where(player.Character.UnearnedQuestFlags.Contains)];
        if (endgame.Length > 0 && (inHideout || MapDevices.IsMapArea(player.Layout.Hash))) own.UnionWith(endgame);
        lock (player.Character.Waypoints)
            return [.. WaypointCatalog.Areas.Entries.Where(e => player.Character.Waypoints.Contains(e.Id)).SelectMany(e => e.AccessFlags ?? [])
                .Concat(player.Character.Visited.Concat(player.Character.Waypoints).SelectMany(id => WaypointCatalog.Visits.For(id)))
                .Concat(Quests.Rows(own.Where(f => OwnHideout(player) || player.Layout.Hash == StashCatalog.DelveHub || f != Quests.ThisIsMyHideout)))
                .Distinct().Order()];
    }

    // Entering an area marks it visited (saved, added to the saved ones) after the player object is sent so the client's
    // 0x2bc quest-flag diff sees the area's StartQuestFlags (Visited-*) transition 0 -> 1 and plays its Enter* voice line.
    public static bool RecordVisit(Player player)
    {
        if (WaypointCatalog.Visits.For(player.Layout.Id).Count == 0) return false;
        if (HardcoreProgressBlocked(player)) return false;
        lock (player.Character.Waypoints) if (!player.Character.Visited.Add(player.Layout.Id)) return false;
        CharacterSaves.AddVisited(CharacterName(player), [player.Layout.Id]);
        return true;
    }

    // C→S 0x4d: a waypoint picked on the World panel. An activated waypoint changes area (the player arrives next to its
    // waypoint); with Ctrl, the instance chooser first.
    public static async Task TravelByWaypoint(Player player, InstanceWaypoints.Travel travel)
    {
        var entry = WaypointCatalog.Areas.Find(travel.AreaHash16);
        bool activated;
        lock (player.Character.Waypoints) activated = entry is not null && (player.Character.Waypoints.Contains(entry.Id) || entry.Id == WaypointCatalog.LabyrinthPlaza);
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x004d waypoint travel to 0x{travel.AreaHash16:x4} ({entry?.Name ?? "no waypoint"}), " +
            $"u32 0x{travel.U32:x}{(travel.Ctrl ? ", Ctrl" : "")}{(activated ? "" : ": not activated, ignored")}");
        if (!activated) return;
        if (travel.Ctrl) await ShowInstances(player, travel.AreaHash16, atWaypoint: true);
        else await ChangeArea(player, new PendingTransfer(travel.AreaHash16, 0, false, AtWaypoint: true), $"waypoint to {entry!.Name}");
    }

    // To the character's hideout (21-hideouts.md §3): "/hideout" (C→S 0x16 command 45) or the World panel's Hideout button
    // (C→S 0x17a). Its own instance (Auth re-enters the live one), arriving next to its waypoint.
    public static async Task WarpToHideout(Player player, string why)
    {
        if (HomeHideout(player) is not { } hideout)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} {why}: no data/hideouts.json (run scripts/dev/extract-data.ps1)");
            return;
        }
        if (player.Layout.Hash == hideout.AreaHash16 && OwnHideout(player)) { Console.WriteLine($"[gateway] {player.Connection.Remote} {why}: already in the {hideout.Name}"); return; }
        await ChangeArea(player, new PendingTransfer(hideout.AreaHash16, 0, false, AtWaypoint: true), $"{why}: {hideout.Name}");
    }

    public static HideoutCatalog.Hideout? HomeHideout(Player player) =>
        HideoutCatalog.Selected(SharedHideout(player)?.Area ?? CharacterSaves.Load(CharacterName(player))?.SelectedHideout);

    public static HideoutScene? HideoutFurniture(Player player) => player.Furniture ??=
        HideoutCatalog.ByArea(player.Layout.Hash) is { } hideout && HideoutCatalog.Default is { } fallback
            ? HideoutEditing.Apply(HideoutScene.Build(hideout, fallback, player.Layout.Walkable), SavedHideoutPlacements(player)) : null;

    public static async Task SelectHideout(Player player, (int Row, bool Guild) selected)
    {
        if (selected.Guild || player.PendingTransfer is not null) return;
        var hideout = HideoutCatalog.Hideouts.FirstOrDefault(h => h.Row == selected.Row);
        if (hideout is null) return;
        SelectAccountHideout(player, hideout.AreaId);
        player.TalkingTo = 0;
        player.InNpcTalk = false;
        await Send(player, InstanceNpcs.Talk(0), "0x0084 close hideout dialogue");
        await SendWorldRecord(player, player.Id, InstanceEntities.PlayerHideoutsUpdate(player.Id, hideout.Hash16, 0),
            $"0x02bc selected personal hideout: {hideout.Name}");
        Console.WriteLine($"[gateway] {CharacterName(player)} selected hideout row {hideout.Row}: {hideout.AreaId}");
        await WarpToHideout(player, "Helena: Select Hideout");
    }

    // 0x2b4 now; the move itself (0x27) once the client acknowledges with 0x2cd.
    public static async Task ChangeArea(Player player, PendingTransfer t, string why)
    {
        foreach (var check in GameEvents.TravelRefusal.For(player))
            if (check(player, t) is { } refusal)
            {
                t.Done?.Invoke(new() { Ok = false, Error = refusal });
                await player.Commands!.Reply(refusal);
                return;
            }
        if (!player.TryBeginAreaChange(t, out var projectiles))
        {
            t.Done?.Invoke(new() { Ok = false, Error = "area change refused" });
            return;
        }
        await GameEvents.RunPlayerLeaving(player, "left the area");
        // Remove outstanding visuals while the old world still exists, before 0x2b4. If Auth refuses the transfer,
        // their canceled tasks must not leave permanent projectile/explosion objects in the retained world.
        foreach (uint id in projectiles)
            await Game.SendWorldRemoval(player, id, $"0x02bd remove projectile 0x{id:x} (leaving area)",
                still: () => !player.Closed);
        await Send(player, InstanceTransfer.ChangeArea(t.Area), $"0x02b4 change area to 0x{t.Area:x4} ({why})");
        // If the client never acknowledges, move it anyway after a moment.
        _ = AreaFrames.Start(async () => { await AreaDelay(player, 2); if (player.PendingTransfer is { } p) await Transfer(player, p); });
    }

    // The capture tour's next step (CaptureTour), after an area load (0x7d) or when /tour starts: at the tour's current
    // stop, wait for scripts/walkmaps to write it, then go to a new instance of the next (area, seed). A tour saved before
    // a restart (data/tour-left.txt) resumes at the character's first area load.
    public static async Task ContinueTour(Player player, bool starting = false)
    {
        // Tours record with scripts/walkmaps: without it a saved tour (data/tour-left.txt) is left alone.
        if (!CurrentGameWork || !AreaLayouts.Recording) return;
        string character = CharacterName(player);
        if (!starting && CaptureTour.Resume(character) is { } resumed)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} {resumed}");
            await player.Commands!.Reply(resumed);
            if (!CurrentGameWork) return;
            starting = true;
        }
        if (!CaptureTour.Active(character)) return;
        if (!starting && CaptureTour.IsCurrent(character, player.Layout.Id, player.Seed))
        {
            CaptureTour.MarkArrived(character);
            bool ok = await CaptureTour.WaitForCapture(character, player.Layout.Id, player.Seed);
            if (!CurrentGameWork) return;
            Console.WriteLine($"[gateway] {player.Connection.Remote} tour: {player.Layout.Id} seed {player.Seed} {(ok ? "captured" : "NOT captured (120 s; is live.ps1 -Walkmaps running?)")}");
            TourRecords.Write(player.Layout.Id, player.Seed, ok ? "captured" : "not captured", layout: player.Layout);
        }
        // An area the tour did not send the player to, unless the tour waits for the client to come back.
        else if (!starting && !CaptureTour.Waiting(character)) return;
        string done = CaptureTour.Summary(character);
        if (CaptureTour.Next(character) is not var (area, hash, seed))
        {
            await player.Commands!.Reply($"Tour finished: {done}.");
            if (!CurrentGameWork) return;
            return;
        }
        await ChangeArea(player, new PendingTransfer(hash, 0, true, AtSpawn: true) { Seed = seed }, $"tour: {area} seed {seed}");
        if (!CurrentGameWork) return;
        // A seed the client itself cannot generate ("Terrain generation failed" in Client/logs/Client.txt): the client
        // shows the exception and hangs. The stop goes to tour-failures.txt and the tour waits: nothing more is sent to
        // that client; after a restart (live.ps1) the next area load goes on.
        for (var until = DateTime.UtcNow.AddMinutes(10); DateTime.UtcNow < until && CaptureTour.IsCurrent(character, area, seed); await AreaWait(PlayerUpdateRole.Tour, 2000))
            if (CaptureTour.Failed(character, area, seed) is { } failure)
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} tour: {area} seed {seed} failed in the client ({failure}); waiting: restart the client with live.ps1 and log in, the tour goes on");
                return;
            }
    }
    // Auth moves the player and issues the destination ticket; 0x27 sends the client there (it reconnects to this Gateway).
    public static async Task Transfer(Player player,
        PendingTransfer t, Func<ControlRequest, Task<ControlResponse>>? control = null)
    {
        if (!ReferenceEquals(player.PendingTransfer, t)) return; // the acknowledgement and fallback timer can race
        player.PendingTransfer = null;
        if (!RetireVitalsForTransfer(player))
        {
            t.Done?.Invoke(new() { Ok = false, Error = "connection replaced" });
            player.ResumeAfterFailedTransfer();
            Console.WriteLine($"[gateway] {player.Connection.Remote} transfer refused: saved life and mana belong to a newer connection");
            return;
        }
        var call = control ?? (request => InstanceControl.CallAsync(request));
        ulong join = t.Instance != 0 || t.New ? 0 : InstanceEvents.TransferInstance.For(player).Select(run => run(player, t)).FirstOrDefault(i => i != 0);
        var r = await call(new ControlRequest
        {
            Op = "transfer", Ticket = player.Ticket, Area = t.Area, Instance = join != 0 ? join : t.Instance, New = t.New, Seed = t.Seed,
        });
        // The party member's instance may be gone or closed to this player: the default choice then.
        if (join != 0 && !r.Ok)
            r = await call(new ControlRequest { Op = "transfer", Ticket = player.Ticket, Area = t.Area, Instance = t.Instance, New = t.New, Seed = t.Seed });
        else if (join != 0) Console.WriteLine($"[gateway] {player.Connection.Remote} transfer: joins party instance {join}");
        t.Done?.Invoke(r);
        if (!r.Ok)
        {
            ResumeVitalsAfterFailedTransfer(player);
            player.ResumeAfterFailedTransfer();
            Console.WriteLine($"[gateway] {player.Connection.Remote} transfer failed: {r.Error}");
            foreach (var resolved in GameEvents.TransferResolved.For(player)) await resolved(player, t, r);
            return;
        }
        player.TransferAccepted = true;
        player.TransferredTo = (r.Area, r.Instance);
        foreach (var resolved in GameEvents.TransferResolved.For(player)) await resolved(player, t, r);
        CarryCharges(player, r.Ticket);
        CarryFortification(player, r.Ticket);
        CarryRampage(player, r.Ticket);
        CarryResourceEffects(player, r.Ticket);
        CarryAntiquarian(player, r.Ticket);
        CarryRage(player, r.Ticket);
        CarryVaalSouls(player, r.Ticket);
        player.Retire();
        FlushAreaProgress(player);
        LeechOf(player).Clear(AreaTime(player));
        player.InvalidateIncomingHits(); // Recent enemy contacts never cross an area connection.
        if (t.AtWaypoint) WaypointArrivals[r.Ticket] = 0;
        else if (t.ArriveAt is { } spot) SpotArrivals[r.Ticket] = spot;
        else if (!t.AtSpawn)
        {
            AreaLayouts.CameFrom[r.Ticket] = KitavaActTen.ArrivalSource(player.Layout.Id, t.Area) ?? player.Layout.Hash;
            if (t.ArrivalEntrance is int entrance) AreaLayouts.ArrivalEntrances[r.Ticket] = entrance;
        }
        if (t.Map is { } map) MapDevice.TicketArrivals[r.Ticket] = map;
        string host = GatewayEndpoint.Host;
        ushort port = GatewayEndpoint.Port;
        await Send(player, InstanceTransfer.ConnectInstance(host, port, r.Ticket, r.Area, player.Connection.TicketKey(r.Ticket)),
            $"0x0027 connect to instance {r.Instance} of area 0x{r.Area:x4} (seed {r.Seed}) at {host}:{port}, ticket 0x{r.Ticket:x8}");
    }
}
