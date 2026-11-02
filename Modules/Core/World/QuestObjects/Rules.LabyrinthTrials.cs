using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;
using GameServer.Data.Terrain.Generation;

static partial class QuestobjectsRules
{
    // A Trial of Ascendancy's room objects where the room fit places them (LabyrinthTrials, Agents/docs/game/
    // labyrinth-trials.md): the trial's door, its levers and locked doors, the plaque and the portal back. What this
    // instance transitioned stays so (InstanceState.Transition); the plaque and the portal are lit once the character
    // has the trial's flag. A closed door closes its line across the way between the area's spawn and the plaque on the
    // server's walk estimate (QuestBarriers), as the client's TriggerableBlockage does on its side.
    // The Labyrinth's own rooms (LabyrinthTrials.InLabyrinth) have the same traps, levers and doors, run the same way. The
    // objects are the Gateway's generated rooms' with their links (LabyrinthWiring), for a trial too when the Gateway
    // generates its area (else data/room-points.json's, unlinked; it keeps only the Labyrinth's rooms with points).
    internal static async Task SpawnTrialObjects(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var trial = LabyrinthTrials.For(conn.Layout.Id);
        bool labyrinth = LabyrinthTrials.InLabyrinth(conn.Layout.Id);
        if (trial is null && !labyrinth) return;
        string what = trial is null ? $"Labyrinth room {conn.Layout.Id}" : $"trial {trial.Flag}";
        var notes = new List<string>();
        var links = new List<PlacedLink>();
        var all = LabyrinthRoomObjects(conn, notes, links);
        if (all.Count == 0 && !labyrinth) all = AreaPopulation.LoadObjects(conn.Layout.Id, conn.Seed, notes);
        var wiring = WireRoomObjects(conn, all, links);
        SetSwapLevers(conn, all);
        await SpawnTrialTraps(remote, stream, ciphers, conn, [.. all.Where(o => LabyrinthTrials.IsSpikeTrap(o.Type))],
            [.. all.Where(o => o.Type == LabyrinthTrials.Lever).Select(o => (o.X, o.Y))],
            [.. all.Where(o => o.Type is LabyrinthTrials.Door or LabyrinthTrials.IntroDoor).Select(o => (o.X, o.Y))],
            [.. all.Where(o => LabyrinthTrials.Trigger(o.Type) is not null)]);
        await SpawnArrowTraps(remote, stream, ciphers, conn, [.. all.Where(o => LabyrinthTrials.IsArrowTrap(o.Type))]);
        await SpawnEdgeBlades(remote, stream, ciphers, conn, [.. all.Where(o => LabyrinthTrials.IsEdgeBlade(o.Type))]);
        // The Chamber of Sins and Crypt trials' track pieces: server objects the client shows.
        foreach (var rail in all.Where(o => o.Type == LabyrinthTrials.VisibleRail))
        {
            uint id = conn.NextObjectId++;
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(rail.Type),
                LabyrinthTrials.RailBlob(rail.X, rail.Y, rail.Angle, rail.Model)), null);
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
        }
        await SpawnRoombas(remote, stream, ciphers, conn,
            [.. all.Where(o => o.Type is LabyrinthTrials.Roomba or LabyrinthTrials.FlyingRoomba or LabyrinthTrials.Spinner || LabyrinthTrials.IsSawblade(o.Type))],
            [.. all.Where(o => o.Type is LabyrinthTrials.Rail or LabyrinthTrials.VisibleRail || LabyrinthTrials.IsRailSwitch(o.Type))],
            [.. all.Where(o => o.Type == LabyrinthTrials.Switch).Select(o => (o.X, o.Y))],
            all.FirstOrDefault(o => o.Type == LabyrinthTrials.IntroDoor) is { } entrance ? (entrance.X, entrance.Y) : default,
            labyrinth ? LabyrinthScratches(conn) : null);
        // The Bronze Monograph at the trial's door: an NPCs.dat row on StoryGlyphBase, read like any NPC (its NPCTalk
        // "Read" row), with the room line's StoryGlyph.ao.
        var glyphs = all.Where(o => o.Type.StartsWith(LabyrinthTrials.GlyphPrefix, StringComparison.Ordinal)).ToList();
        Console.WriteLine($"[gateway] {remote} {what}: {glyphs.Count} monograph(s) in its rooms");
        foreach (var glyph in glyphs)
        {
            if (Npcs.Value.Place(new NpcCatalog.Placement(glyph.Type, glyph.X, glyph.Y, glyph.Angle, glyph.Model)) is { Supported: true } npc)
                await SpawnNpc(remote, stream, ciphers, conn, npc, glyph.X, glyph.Y, glyph.Angle);
            else Console.WriteLine($"[gateway] {remote} trial monograph {glyph.Type}: unknown or unsupported NPC");
        }
        var objects = all.Where(o => LabyrinthTrials.Handles(o.Type)).ToList();
        Console.WriteLine($"[gateway] {remote} {what}: {links.Count} room link(s), {Enumerable.Range(0, all.Count).Count(wiring.Graph.Wired)} wired object(s)");
        if (objects.Count == 0)
        {
            Console.WriteLine($"[gateway] {remote} {what}: no trial objects in {conn.Layout.Id} seed {conn.Seed} ({string.Join("; ", notes)})");
            return;
        }
        var plaque = objects.FirstOrDefault(o => o.Type == LabyrinthTrials.Plaque);
        // A done trial's plaque is lit when the player comes (the flag); it counts as used in this instance, so using it
        // again opens the portal without playing its glow again. The portal opens only when the plaque is used (live
        // 2026-10-31, the original: in every trial, done or not), and stays open in this instance.
        if (plaque is not null && trial is not null && ClientHasQuestFlag(conn, trial.Flag)) conn.Instance.Transition(plaque.Type, plaque.X, plaque.Y);
        var from = conn.Layout.Near((conn.Layout.SpawnX, conn.Layout.SpawnY));
        List<(int X, int Y)> guarded = plaque is null ? [] : [conn.Layout.Near((plaque.X, plaque.Y))];
        for (int index = 0; index < all.Count; index++)
        {
            var o = all[index];
            if (!LabyrinthTrials.Handles(o.Type)) continue;
            bool open = conn.Instance.Transitioned(o.Type, o.X, o.Y);
            if (open && o.Type == LabyrinthTrials.Door) continue;   // an opened Locked Door is gone (RemoveOpenedDoor)
            uint id = conn.NextObjectId++;
            lock (conn.Monsters)
            {
                conn.TrialObjects[id] = o;
                wiring.Ids[index] = id;
            }
            // A disabled lever shows pulled (it cannot be used), a spent crank down for good.
            byte state = o.Type == LabyrinthTrials.RailSwitchSwapped ? LabyrinthTrials.SwapClientState(o.Mirrored, open)
                : wiring.Graph.IsSpent(index) ? LabyrinthTrials.Spent
                : open || wiring.Graph.IsDisabled(index) ? LabyrinthTrials.Open : LabyrinthTrials.Closed;
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(o.Type),
                    LabyrinthTrials.Blob(o.Type, o.Model, o.X, o.Y, o.Angle, o.Scale, state, LabyrinthTrials.IsHotPlate(o.Type) ? o.Height : null)),
                $"0x02b9 trial object 0x{id:x} {o.Type} at ({o.X}, {o.Y}), angle {o.Angle:0.###}, state {state}");
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
            if (o.Type == LabyrinthTrials.OpenDoor)
            {
                if (open) BarCage(conn, id, o, true);
                continue;
            }
            if (open || !LabyrinthTrials.IsDoor(o.Type)) continue;
            var cells = conn.Layout.Walkable is { } walkable
                ? QuestBarriers.Cells(walkable, conn.Layout.GridW, conn.Layout.GridH, (o.X, o.Y), o.Angle, from, guarded)
                : [];
            // Where the door's line parts nothing on the server's walk map (the Catacombs' trial, live 2026-10-31: it
            // reached the plaque round some other way), its own Hull closes the way through it, or the walk to the door
            // would stop on its far side, where the client cannot be.
            if (cells.Count == 0 && conn.Layout.Walkable is { } walk)
            {
                var (halfX, halfY) = LabyrinthTrials.DoorHull(o.Model);
                cells = [.. LabyrinthTrials.HullCells(o.X, o.Y, o.Angle, halfX, halfY).Where(c => walk(c.X, c.Y))];
            }
            Console.WriteLine($"[gateway] {remote} trial door 0x{id:x}: {cells.Count} walkable cell(s) closed on the server between " +
                $"({from.X}, {from.Y}) and {string.Join(", ", guarded)}" + (cells.Count == 0 ? "" : $", from {cells.Min()} to {cells.Max()}"));
            lock (conn.Monsters)
            {
                conn.DoorBarriers[id] = cells;
                conn.UpdateBarrier();
            }
        }
        List<KeyValuePair<uint, AreaPopulation.RoomObject>> plates;
        lock (conn.Monsters) plates = [.. conn.TrialObjects.Where(t => LabyrinthTrials.IsHotPlate(t.Value.Type))];
        if (plates.Count > 0)
        {
            // What burns: the hot plates and the half lines by the plates in the walls (server objects only, as those).
            List<AreaPopulation.RoomObject> burning = [.. all.Where(o => LabyrinthTrials.Burns(o.Type))];
            Console.WriteLine($"[gateway] {remote} trial hot plates: {plates.Count} placed " +
                $"({plates.Count(p => LabyrinthTrials.IsInertPlate(p.Value.Type))} in the walls), {burning.Count} burning areas");
            _ = RunHotPlates(remote, stream, ciphers, conn, plates, burning);
        }
    }

    // A Labyrinth room area's roomba scratch decals (LabyrinthTrials.ScratchedRuns), from the Gateway's placed doodads.
    static List<(int X, int Y)> LabyrinthScratches(ClientState conn) =>
        [.. (ServerLevels.For(conn.Layout.Id, conn.Seed)?.Doodads ?? [])
            .Where(d => d.Model.Contains("LabyrinthRoombaKit/R_scratch", StringComparison.OrdinalIgnoreCase)).Select(d => (d.X, d.Y))];

    // A Labyrinth room area's server objects, as the Gateway generated its rooms (ServerLevels: the area is in the levelgen
    // gate's baseline), with each line's scale and fixed height, and their links (indexes in the list returned).
    static List<AreaPopulation.RoomObject> LabyrinthRoomObjects(ClientState conn, List<string> notes, List<PlacedLink> links)
    {
        if (ServerLevels.For(conn.Layout.Id, conn.Seed) is not { } level)
        {
            notes.Add(ServerLevels.Error(conn.Layout.Id, conn.Seed)?.Split('\n')[0] ?? $"{conn.Layout.Id} is not generated by the Gateway");
            return [];
        }
        links.AddRange(level.RoomLinks);
        return [.. level.RoomObjects.Select(o => new AreaPopulation.RoomObject(o.Type, o.Model, o.X, o.Y,
            LabyrinthTrials.PlacedAngle(o.Model, o.Rotation, o.Code), o.Scale, float.IsNaN(o.Z) ? null : o.Z, o.Code is >= 4 and < 8))];
    }

    // A room area's wiring on this connection: its objects' links, and the ids its placed objects have.
    sealed class RoomWiring(LabyrinthWiring graph, List<AreaPopulation.RoomObject> all)
    {
        public readonly LabyrinthWiring Graph = graph;
        public readonly List<AreaPopulation.RoomObject> All = all;
        public readonly Dictionary<int, uint> Ids = [];

        public int? IndexOf(uint id)
        {
            foreach (var (index, placed) in Ids) if (placed == id) return index;
            return null;
        }
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ClientState, RoomWiring> Wirings = new();

    // The room objects' wiring as this instance left it: what it turned on is on again (passing it on sets the counters,
    // the disabled levers and the spent cranks), and a crank still down comes back up (its timer ran on this connection).
    static RoomWiring WireRoomObjects(ClientState conn, List<AreaPopulation.RoomObject> all, List<PlacedLink> links)
    {
        var wiring = new RoomWiring(new LabyrinthWiring([.. all.Select(o => o.Type)], links), all);
        for (int i = 0; i < all.Count; i++)
            if (wiring.Graph.Wired(i) && conn.Instance.Transitioned(all[i].Type, all[i].X, all[i].Y)) wiring.Graph.Turn(i, true);
        for (int i = 0; i < all.Count; i++)
            if (LabyrinthWiring.IsCrank(all[i].Type) && wiring.Graph.IsOn(i) && !wiring.Graph.IsSpent(i))
                foreach (var (k, _) in wiring.Graph.Turn(i, false)) conn.Instance.SetTransitioned(all[k].Type, all[k].X, all[k].Y, false);
        Wirings.AddOrUpdate(conn, wiring);
        return wiring;
    }

    // A wired lever, Switch_Once_Multi or crank used: it turns on and passes it on (LabyrinthWiring); a crank comes back up
    // after its seconds unless a counter spent it meanwhile. A used, disabled or spent one does nothing.
    static async Task PullWired(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, RoomWiring wiring, int index)
    {
        if (wiring.Graph.IsOn(index) || wiring.Graph.IsDisabled(index) || wiring.Graph.IsSpent(index)) return;
        await ApplyWiring(remote, stream, ciphers, conn, wiring, wiring.Graph.Turn(index, true));
        var o = wiring.All[index];
        if (LabyrinthWiring.CrankSeconds(o.Type) is not double seconds) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            if (conn.Closed || !Wirings.TryGetValue(conn, out var now) || now != wiring) return;
            try { await ApplyWiring(remote, stream, ciphers, conn, wiring, wiring.Graph.Turn(index, false)); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            { if (!conn.Closed) Console.WriteLine($"[gateway] {remote} crank at ({o.X}, {o.Y}) not reset: {ServerLog.Describe(ex)}"); }
        });
    }

    // The wiring's changes in this instance and on the client: a door opens (OpenTrialObject), anything else takes its
    // Transitionable state (on 2, off 1, a disabled lever 2, a spent crank 3).
    static async Task ApplyWiring(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, RoomWiring wiring,
        List<(int Index, LabyrinthWiring.Change Change)> changes)
    {
        foreach (var (index, change) in changes)
        {
            var o = wiring.All[index];
            uint? id;
            lock (conn.Monsters) id = wiring.Ids.TryGetValue(index, out uint placed) && conn.TrialObjects.ContainsKey(placed) ? placed : null;
            if (change == LabyrinthWiring.Change.On && LabyrinthTrials.IsDoor(o.Type) && id is uint door)
            {
                await OpenTrialObject(remote, stream, ciphers, conn, door, o);
                continue;
            }
            if (change is LabyrinthWiring.Change.On or LabyrinthWiring.Change.Off)
                conn.Instance.SetTransitioned(o.Type, o.X, o.Y, change == LabyrinthWiring.Change.On);
            if (id is not uint at || !LabyrinthTrials.Handles(o.Type)) continue;
            byte state = change switch
            {
                LabyrinthWiring.Change.Off => LabyrinthTrials.Closed,
                LabyrinthWiring.Change.Spent => LabyrinthTrials.Spent,
                _ => LabyrinthTrials.Open,
            };
            await Send(remote, stream, ciphers, InstanceEntities.TransitionableUpdate(at, LabyrinthTrials.TransitionableIndex(o.Type), state),
                $"0x02ba wired {o.Type.Split('/')[^1]} 0x{at:x} at ({o.X}, {o.Y}): {change}");
        }
    }

    // A Labyrinth spinner room's swap piece (Rail_Visible_Swap_Anims10) and the levers that turn it: each Switch whose
    // nearest rail switch it is, with the lever's gears (each with its nearest lever).
    static List<(AreaPopulation.RoomObject Lever, List<AreaPopulation.RoomObject> Gears)> SwapLevers(
        IReadOnlyCollection<AreaPopulation.RoomObject> all, AreaPopulation.RoomObject swap)
    {
        static long Distance(AreaPopulation.RoomObject a, AreaPopulation.RoomObject b) => (long)(a.X - b.X) * (a.X - b.X) + (long)(a.Y - b.Y) * (a.Y - b.Y);
        var railSwitches = all.Where(o => LabyrinthTrials.IsRailSwitch(o.Type)).ToList();
        var levers = all.Where(o => o.Type is LabyrinthTrials.Switch or LabyrinthTrials.MultiSwitch or LabyrinthTrials.Lever).ToList();
        return [.. levers.Where(l => l.Type == LabyrinthTrials.Switch && railSwitches.MinBy(r => Distance(l, r)) == swap)
            .Select(l => (l, all.Where(g => g.Type == LabyrinthTrials.Gears && levers.MinBy(k => Distance(g, k)) == l).ToList()))];
    }

    // Each swap piece's levers as the piece stands in this instance (LabyrinthTrials.OnFirstArm): the one by the part of
    // the track the spinner does not run is pulled, its gears turning.
    static void SetSwapLevers(ClientState conn, IReadOnlyCollection<AreaPopulation.RoomObject> all)
    {
        foreach (var swap in all.Where(o => o.Type == LabyrinthTrials.RailSwitchSwapped))
        {
            bool other = conn.Instance.Transitioned(swap.Type, swap.X, swap.Y);
            foreach (var (lever, gears) in SwapLevers(all, swap))
            {
                bool pulled = LabyrinthTrials.OnFirstArm((lever.X, lever.Y), (swap.X, swap.Y), swap.Angle, swap.Mirrored) == other;
                foreach (var o in gears.Prepend(lever)) conn.Instance.SetTransitioned(o.Type, o.X, o.Y, pulled);
            }
        }
    }

    // A lever by a swap piece pulled: when it is the one by the part the spinner runs, the piece turns to the other part
    // (the spinner goes over when it next comes by), this lever and its gears start and the other lever and its gears
    // stop. The pulled one does nothing.
    static async Task TurnSwap(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, AreaPopulation.RoomObject lever,
        uint swapId, AreaPopulation.RoomObject swap)
    {
        if (conn.Instance.Transitioned(lever.Type, lever.X, lever.Y)) return;
        bool other = !conn.Instance.Transitioned(swap.Type, swap.X, swap.Y);
        conn.Instance.SetTransitioned(swap.Type, swap.X, swap.Y, other);
        await Send(remote, stream, ciphers, InstanceEntities.TransitionableUpdate(swapId, LabyrinthTrials.TransitionableIndex(swap.Type),
            LabyrinthTrials.SwapClientState(swap.Mirrored, other)), $"0x02ba swap piece 0x{swapId:x} at ({swap.X}, {swap.Y}) to its {(other ? "other" : "first")} arm");
        List<KeyValuePair<uint, AreaPopulation.RoomObject>> placed;
        lock (conn.Monsters) placed = [.. conn.TrialObjects];
        foreach (var (l, gears) in SwapLevers([.. placed.Select(p => p.Value)], swap))
        {
            bool pulled = LabyrinthTrials.OnFirstArm((l.X, l.Y), (swap.X, swap.Y), swap.Angle, swap.Mirrored) == other;
            foreach (var o in gears.Prepend(l))
            {
                if (conn.Instance.Transitioned(o.Type, o.X, o.Y) == pulled) continue;
                conn.Instance.SetTransitioned(o.Type, o.X, o.Y, pulled);
                uint id = placed.First(p => p.Value == o).Key;
                await Send(remote, stream, ciphers, InstanceEntities.TransitionableUpdate(id, LabyrinthTrials.TransitionableIndex(o.Type),
                    pulled ? LabyrinthTrials.Open : LabyrinthTrials.Closed), $"0x02ba swap {o.Type.Split('/')[^1]} 0x{id:x} {(pulled ? "on" : "off")}");
            }
        }
    }

    // The Crematorium trial's hot plates, all together, over and over (LabyrinthTrials.LavaHeatSeconds and the rest): each
    // heats (Transitionable 2), stays at its peak LavaBurnSeconds, cools (1) and rests cold. A player in a burning area
    // (LabyrinthTrials.Burns) while the lava lies (LavaSpillSeconds into the heating to LavaDrainSeconds into the cooling) burns
    // (LabyrinthTrials.LavaDamagePerSecond less their fire resistance, ClientState.TrapDegenPerSecond) with its
    // labyrinth_lava_aura buff.
    static async Task RunHotPlates(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        List<KeyValuePair<uint, AreaPopulation.RoomObject>> plates, List<AreaPopulation.RoomObject> burning)
    {
        var degen = new TrapDegen();
        try
        {
            double next = conn.Clock.Elapsed.TotalSeconds + 1, spills = double.PositiveInfinity, drains = double.PositiveInfinity,
                cools = double.PositiveInfinity;
            while (!conn.Closed)
            {
                double now = conn.Clock.Elapsed.TotalSeconds;
                if (now >= next)
                {
                    foreach (var (id, plate) in plates)
                        await Send(remote, stream, ciphers, InstanceEntities.TransitionableUpdate(id, LabyrinthTrials.TransitionableIndex(plate.Type), LabyrinthTrials.Open), null);
                    (spills, cools) = (next + LabyrinthTrials.LavaSpillSeconds, next + LabyrinthTrials.LavaHeatSeconds + LabyrinthTrials.LavaBurnSeconds);
                    drains = cools + LabyrinthTrials.LavaDrainSeconds;
                    next = cools + LabyrinthTrials.LavaCoolSeconds + LabyrinthTrials.LavaRestSeconds;
                }
                if (now >= cools)
                {
                    foreach (var (id, plate) in plates)
                        await Send(remote, stream, ciphers, InstanceEntities.TransitionableUpdate(id, LabyrinthTrials.TransitionableIndex(plate.Type), LabyrinthTrials.Closed), null);
                    cools = double.PositiveInfinity;
                }
                (int X, int Y) here;
                lock (conn.Monsters) here = conn.Walk.Position(now);
                bool burns = now >= spills && now < drains && burning.Any(p => LabyrinthTrials.OnPlate(p.Type, p.X, p.Y, p.Angle, here));
                double perSecond = 0;
                if (burns)
                {
                    var fire = GameServer.Core.World.DamageType.Fire;
                    var (lifeShare, shieldShare) = LabyrinthTrials.LavaDamagePerSecond;
                    lock (conn.Monsters)
                        perSecond = GameServer.Core.World.DamageModel.Mitigate(conn.PlayerMaxLife * lifeShare + conn.EnergyShield.Maximum * shieldShare,
                            Resistance(conn, fire), MaxResistance(conn, fire));
                }
                await degen.Set(remote, stream, ciphers, conn, perSecond, burns ? LabyrinthTrials.LavaBuffRow : null, "on a trial hot plate");
                await Task.Delay(SpikeTickMilliseconds);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] {remote} trial hot plates stopped: {ServerLog.Describe(ex)}"); }
        finally { lock (conn.Monsters) conn.TrapDegenPerSecond = 0; }
    }

    // A trial object used: the player walks up to it, then the trial's door (or a plain door) opens, a lever opens the nearest locked door,
    // the plaque completes the trial (its flag) and opens the portal (every use), and the open portal returns to the trial's door.
    // False when the object is not a trial's.
    internal static async Task<bool> UseTrialObject(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        InstanceTransfer.UseSkillOnObject use)
    {
        AreaPopulation.RoomObject? o;
        lock (conn.Monsters) o = conn.TrialObjects.GetValueOrDefault(use.TargetId);
        if (o is null) return false;
        var trial = LabyrinthTrials.For(conn.Layout.Id);
        int action = Interlocked.Increment(ref conn.ActionCount);
        double now = conn.Clock.Elapsed.TotalSeconds;
        int size = LabyrinthTrials.Size(o.Type);
        var (here, stop, arrive) = conn.Walk.WalkUpTo(o.X, o.Y, size, now);
        await Send(remote, stream, ciphers, InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, o.X, o.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
            $"0x01ab player uses trial object 0x{use.TargetId:x} {o.Type} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (conn.ActionCount != action || conn.Closed) return true;
        if (!GameServer.Core.World.PlayerWalk.InWalkUpRange(stop, (o.X, o.Y), size))
            Console.WriteLine($"[gateway] {remote} trial object 0x{use.TargetId:x} out of reach from ({stop.X}, {stop.Y})");
        else if ((o.Type is LabyrinthTrials.Lever or LabyrinthWiring.OnceMultiSwitch || LabyrinthWiring.IsCrank(o.Type))
                 && Wirings.TryGetValue(conn, out var wiring) && wiring.IndexOf(use.TargetId) is int wired && wiring.Graph.Wired(wired))
            await PullWired(remote, stream, ciphers, conn, wiring, wired);
        else switch (o.Type)
        {
            case LabyrinthTrials.IntroDoor or LabyrinthTrials.BarDoor:
                await OpenTrialObject(remote, stream, ciphers, conn, use.TargetId, o);
                break;
            case LabyrinthTrials.Lever:
                if (!await OpenTrialObject(remote, stream, ciphers, conn, use.TargetId, o)) break;
                if (NearestTrialObject(conn, LabyrinthTrials.Door, (o.X, o.Y), closed: true) is { } door)
                    await OpenTrialObject(remote, stream, ciphers, conn, door.Id, door.Object);
                // The Crematorium's gears beside the lever start turning (Transitionable 2: start, then idle looped).
                List<KeyValuePair<uint, AreaPopulation.RoomObject>> gears, pulls;
                lock (conn.Monsters)
                {
                    gears = [.. conn.TrialObjects.Where(t => t.Value.Type == LabyrinthTrials.Gears)];
                    pulls = [.. conn.TrialObjects.Where(t => t.Value.Type == LabyrinthTrials.Lever)];
                }
                foreach (int i in LabyrinthTrials.TurnedGears([.. gears.Select(g => (g.Value.X, g.Value.Y))],
                             [.. pulls.Select(l => (l.Value.X, l.Value.Y))], pulls.FindIndex(l => l.Key == use.TargetId)))
                    await OpenTrialObject(remote, stream, ciphers, conn, gears[i].Key, gears[i].Value);
                break;
            case LabyrinthTrials.Switch or LabyrinthTrials.MultiSwitch:
                await ToggleTrialSwitch(remote, stream, ciphers, conn, use.TargetId, o);
                break;
            case LabyrinthTrials.Plaque when trial is not null:
                if (await OpenTrialObject(remote, stream, ciphers, conn, use.TargetId, o))
                {
                    // The character's own flag, and the league's: the trial counts for every character of the league.
                    await SetQuestFlags(remote, stream, ciphers, conn, [trial.Flag]);
                    await SetLeagueQuestFlags(remote, stream, ciphers, conn, [trial.LeagueFlag]);
                }
                List<KeyValuePair<uint, AreaPopulation.RoomObject>> portals;
                lock (conn.Monsters) portals = [.. conn.TrialObjects.Where(t => t.Value.Type == LabyrinthTrials.Portal)];
                foreach (var (id, portal) in portals)
                    if (conn.Instance.Transition(portal.Type, portal.X, portal.Y))
                        await Send(remote, stream, ciphers, InstanceEntities.TransitionableUpdate(id, LabyrinthTrials.TransitionableIndex(portal.Type), LabyrinthTrials.Open),
                            $"0x02ba trial portal 0x{id:x} opens: trial {trial.Flag} plaque used");
                // Every use shows the trials done (the window reads their flags itself).
                await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(InstanceWaypoints.TrialPlaqueUi), "0x007b open the Trial Plaque window");
                break;
            case LabyrinthTrials.Portal:
                if (!conn.Instance.Transitioned(o.Type, o.X, o.Y)) break;   // closed until the plaque is used
                if (NearestTrialObject(conn, LabyrinthTrials.IntroDoor, (o.X, o.Y), closed: false) is not { } entrance) break;
                var to = conn.Layout.Near((entrance.Object.X, entrance.Object.Y));
                lock (conn.Monsters)
                {
                    Interlocked.Increment(ref conn.ActionCount);
                    conn.Walk.Teleport(to.X, to.Y, conn.Clock.Elapsed.TotalSeconds);
                }
                await Send(remote, stream, ciphers, InstanceProtocol.ActionEnded(PlayerId), null);
                await Send(remote, stream, ciphers, InstanceProtocol.Teleport(PlayerId, to.X, to.Y),
                    $"0x01b7 trial portal: back to the trial's door at ({to.X}, {to.Y})");
                return true;
        }
        await Send(remote, stream, ciphers, InstanceProtocol.ActionEnded(PlayerId), null);
        return true;
    }

    // Transitions a trial object once in this instance: its Transitionable steps to the open state (the client plays the
    // .ot's on_transition), a door also unblocks (0x1c6) and leaves the server's walk estimate. False when already open.
    static async Task<bool> OpenTrialObject(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id,
        AreaPopulation.RoomObject o)
    {
        if (!conn.Instance.Transition(o.Type, o.X, o.Y)) return false;
        await Send(remote, stream, ciphers, InstanceEntities.TransitionableUpdate(id, LabyrinthTrials.TransitionableIndex(o.Type), LabyrinthTrials.Open),
            $"0x02ba trial object 0x{id:x} {o.Type} at ({o.X}, {o.Y}) opens");
        if (!LabyrinthTrials.IsDoor(o.Type)) return true;
        await Send(remote, stream, ciphers, InstanceEntities.BlockageUpdate(id, false), $"0x01c6 trial door 0x{id:x} unblocked");
        lock (conn.Monsters)
        {
            conn.DoorBarriers.Remove(id);
            conn.UpdateBarrier();
        }
        if (o.Type == LabyrinthTrials.Door) _ = RemoveOpenedDoor(remote, stream, ciphers, conn, id);
        return true;
    }

    // An opened Locked Door's opening lifts the bars out of the way, so once it has played the door goes (0x2bb), and a
    // later entry does not create an opened one. (Its cells once stayed closed after 0x1c6, live 2026-10-31, while its
    // Positioned had a static position: LabyrinthTrials.Blob.)
    static async Task RemoveOpenedDoor(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id)
    {
        await Task.Delay(TimeSpan.FromSeconds(DoorOpeningSeconds));
        if (conn.Closed) return;
        lock (conn.Monsters) conn.TrialObjects.Remove(id);
        await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb opened trial door 0x{id:x} removed");
    }

    // How long a Locked Door's opening plays before it goes (our own number: the animation's length is not read).
    const double DoorOpeningSeconds = 1.5;

    // The Chamber of Sins trial's lever (Switch) goes both ways (live 2026-11-01, the original): pulled, it shuts the cage
    // nearest it (Door_Open, round the lever's own room) and stops one sawblade of every pair, the inner or the outer
    // lane's (LabyrinthTrials.SwitchedSaws); pulled back, the cage opens and they run on. The Crypt trial's levers
    // (Switch, Switch_Multi) move the rail switches nearest them (LabyrinthTrials.SwitchedRails) the same way: its
    // spinners read them as they come (SpinnerTrack). This instance keeps where it all stands.
    static async Task ToggleTrialSwitch(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id,
        AreaPopulation.RoomObject o)
    {
        List<KeyValuePair<uint, AreaPopulation.RoomObject>> railSwitches;
        lock (conn.Monsters) railSwitches = [.. conn.TrialObjects.Where(t => LabyrinthTrials.IsRailSwitch(t.Value.Type))];
        if (o.Type == LabyrinthTrials.Switch && railSwitches.Count > 0
            && railSwitches.MinBy(r => (long)(r.Value.X - o.X) * (r.Value.X - o.X) + (long)(r.Value.Y - o.Y) * (r.Value.Y - o.Y)) is var nearest
            && nearest.Value.Type == LabyrinthTrials.RailSwitchSwapped)
        {
            await TurnSwap(remote, stream, ciphers, conn, o, nearest.Key, nearest.Value);
            return;
        }
        bool pulled = !conn.Instance.Transitioned(o.Type, o.X, o.Y);
        conn.Instance.SetTransitioned(o.Type, o.X, o.Y, pulled);
        byte state = pulled ? LabyrinthTrials.Open : LabyrinthTrials.Closed;
        await Send(remote, stream, ciphers, InstanceEntities.TransitionableUpdate(id, LabyrinthTrials.TransitionableIndex(o.Type), state),
            $"0x02ba trial switch 0x{id:x} at ({o.X}, {o.Y}) {(pulled ? "pulled" : "pulled back")}");
        if (NearestTrialObject(conn, LabyrinthTrials.OpenDoor, (o.X, o.Y), closed: false) is { } cage)
        {
            conn.Instance.SetTransitioned(cage.Object.Type, cage.Object.X, cage.Object.Y, pulled);
            await Send(remote, stream, ciphers, InstanceEntities.TransitionableUpdate(cage.Id, LabyrinthTrials.TransitionableIndex(cage.Object.Type), state),
                $"0x02ba trial cage 0x{cage.Id:x} at ({cage.Object.X}, {cage.Object.Y}) {(pulled ? "shuts" : "opens")}");
            BarCage(conn, cage.Id, cage.Object, pulled);
            await Send(remote, stream, ciphers, InstanceEntities.BlockageUpdate(cage.Id, pulled), null);
        }
        List<KeyValuePair<uint, AreaPopulation.RoomObject>> rails, levers;
        lock (conn.Monsters)
        {
            rails = [.. conn.TrialObjects.Where(t => LabyrinthTrials.IsRailSwitch(t.Value.Type))];
            levers = [.. conn.TrialObjects.Where(t => t.Value.Type is LabyrinthTrials.Switch or LabyrinthTrials.MultiSwitch)];
        }
        foreach (int i in LabyrinthTrials.SwitchedRails([.. rails.Select(r => (r.Value.X, r.Value.Y))],
                     [.. levers.Select(l => (l.Value.X, l.Value.Y))], levers.FindIndex(l => l.Key == id)))
        {
            var (railId, rail) = rails[i];
            conn.Instance.SetTransitioned(rail.Type, rail.X, rail.Y, pulled);
            await Send(remote, stream, ciphers, InstanceEntities.TransitionableUpdate(railId, LabyrinthTrials.TransitionableIndex(rail.Type), state),
                $"0x02ba trial rail switch 0x{railId:x} at ({rail.X}, {rail.Y}) to state {state}");
        }
        if (TrapRuns.TryGetValue(conn, out var runs))
        {
            var saws = runs.Where(r => LabyrinthTrials.IsSawblade(r.Roomba.Type)).ToList();
            List<(int X, int Y)> switches;
            lock (conn.Monsters) switches = [.. conn.TrialObjects.Values.Where(t => t.Type == LabyrinthTrials.Switch).Select(t => (t.X, t.Y))];
            var entrance = NearestTrialObject(conn, LabyrinthTrials.IntroDoor, (o.X, o.Y), closed: false) is { } door ? (door.Object.X, door.Object.Y) : default;
            var stopped = LabyrinthTrials.SwitchedSaws([.. saws.Select(r => (r.Roomba.X, r.Roomba.Y))], switches, switches.IndexOf((o.X, o.Y)), entrance)
                .Select(i => saws[i]).ToList();
            foreach (var r in stopped) r.Halted = pulled;
            Console.WriteLine($"[gateway] {remote} trial switch at ({o.X}, {o.Y}): sawblades {string.Join(", ", stopped.Select(r => $"({r.Roomba.X}, {r.Roomba.Y})"))} " +
                (pulled ? "stop" : "run on"));
        }
    }

    // A shut cage (Door_Open) closes its own walk Hull on the server's walk estimate, as its TriggerableBlockage does on
    // the client's.
    static void BarCage(ClientState conn, uint id, AreaPopulation.RoomObject cage, bool shut)
    {
        lock (conn.Monsters)
        {
            var (halfX, halfY) = LabyrinthTrials.DoorHull(cage.Model);
            if (!shut) conn.DoorBarriers.Remove(id);
            else if (conn.Layout.Walkable is { } walk)
                conn.DoorBarriers[id] = LabyrinthTrials.HullCells(cage.X, cage.Y, cage.Angle, halfX, halfY).Where(c => walk(c.X, c.Y)).ToHashSet();
            conn.UpdateBarrier();
        }
    }

    // A trial's spike traps (LabyrinthTrials.SpikeTrap): created at rest, then fired in waves for as long as the player
    // is in the area (RunSpikeTraps). The traps a room's Trigger_* line takes fire on its timer (LabyrinthTrials.TriggeredSpikes),
    // the others as the trials' chains.
    static async Task SpawnTrialTraps(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        List<AreaPopulation.RoomObject> traps, List<(int X, int Y)> levers, List<(int X, int Y)> doors, List<AreaPopulation.RoomObject> triggers)
    {
        if (traps.Count == 0) return;
        // A step fires a pressure spike trap: it is in no chain.
        var pressure = traps.Where(t => LabyrinthTrials.IsPressureSpikeTrap(t.Type)).ToList();
        traps = [.. traps.Except(pressure)];
        var chains = LabyrinthTrials.SpikeChains([.. traps.Select(t => (t.X, t.Y))], levers, doors);
        var triggered = LabyrinthTrials.TriggeredSpikes([.. traps.Select(t => (t.X, t.Y, t.Angle))], [.. triggers.Select(t => (t.X, t.Y))]);
        int firstTriggered = chains.Length == 0 ? 0 : chains.Max(c => c.Chain) + 1;
        var placed = new List<SpikeTrap>();
        for (int i = 0; i < traps.Count; i++)
        {
            uint id = conn.NextObjectId++;
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(traps[i].Type),
                LabyrinthTrials.SpikeTrapBlob(traps[i].X, traps[i].Y)), null);
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
            placed.Add(triggered[i].Trigger is int t and >= 0
                ? new(id, traps[i], firstTriggered + t, triggered[i].Order, false) { Trigger = LabyrinthTrials.Trigger(triggers[t].Type) }
                : new(id, traps[i], chains[i].Chain, chains[i].Order, chains[i].Checker));
        }
        foreach (var trap in pressure)
        {
            uint id = conn.NextObjectId++;
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(trap.Type),
                LabyrinthTrials.SpikeTrapBlob(trap.X, trap.Y, trap.Type)), null);
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
            placed.Add(new(id, trap, -1, 0, false));
        }
        Console.WriteLine($"[gateway] {remote} trial spike traps: {placed.Count} placed in {placed.Select(p => p.Chain).Distinct().Count()} chain(s), " +
            $"{placed.Count(p => p.Trigger is not null)} on {triggers.Count} trigger(s), {pressure.Count} a step fires");
        _ = RunSpikeTraps(remote, stream, ciphers, conn, placed);
    }

    sealed record SpikeTrap(uint Id, AreaPopulation.RoomObject Trap, int Chain, int Order, bool Checker)
    {
        // The room's Trigger_* line that fires it, if one does. A pressure trap: whether a step has it about to fire, and
        // whether a step on it may (the player stepped off since it last fired).
        public LabyrinthTrials.SpikeTrigger? Trigger { get; init; }
        public bool Stepped, Armed = true;
        // When its last clip started (Clock seconds), and whether that clip has hit.
        public double Fired = double.NegativeInfinity;
        public bool HasHit;
        public bool Up(double now) => now >= Fired + LabyrinthTrials.SpikeHitWindow.From && now < Fired + LabyrinthTrials.SpikeHitWindow.To;
        public bool Holds((int X, int Y) at) =>
            Math.Abs(at.X - Trap.X) <= LabyrinthTrials.SpikeHalf && Math.Abs(at.Y - Trap.Y) <= LabyrinthTrials.SpikeHalf;
        public bool Near((int X, int Y) at) =>
            Math.Abs(at.X - Trap.X) <= LabyrinthTrials.SpikeHalf * 5 && Math.Abs(at.Y - Trap.Y) <= LabyrinthTrials.SpikeHalf * 5;
    }

    // Each chain fires as a wave, over and over (LabyrinthTrials.SpikeWaveSeconds); the cascade traps do not answer a step
    // (the ones that do are another trap). A stretched Emerge animation (0x1d1) plays the spikes; while they are up
    // (LabyrinthTrials.SpikeHitWindow) a player in the square is hit once (SpikeTrapHit); the square is a wall until they
    // are down (LabyrinthTrials.SpikeWallWindow).
    static async Task RunSpikeTraps(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        List<SpikeTrap> traps)
    {
        const int Wave = 0, Block = 1, Unblock = 2, Fired = 3;
        // A chain's wave takes its step when it starts (so /trial_spike_step applies to whole waves).
        var due = new PriorityQueue<(SpikeTrap Trap, int Stage), double>();
        var chains = traps.Where(t => t.Chain >= 0).GroupBy(t => t.Chain).ToDictionary(g => g.Key, g => g.ToList());
        double start = conn.Clock.Elapsed.TotalSeconds + 1;
        foreach (var chain in chains.Values) due.Enqueue((chain[0], Wave), start + (chain[0].Trigger?.Delay ?? 0));
        async Task Fire(SpikeTrap t, double at)
        {
            (t.Fired, t.HasHit, t.Stepped) = (at, false, false);
            (int X, int Y) player;
            lock (conn.Monsters) player = conn.Walk.Position(at);
            // Logged only near the player (about two tiles), to line a hit up with the spikes the client shows.
            string? log = !t.Near(player) ? null : $"trial spike trap 0x{t.Id:x} at ({t.Trap.X}, {t.Trap.Y}) fires (chain {t.Chain} #{t.Order}), player at ({player.X}, {player.Y})";
            await Send(remote, stream, ciphers, InstanceProtocol.ActorAnimation(t.Id, (float)LabyrinthTrials.SpikeAnimationSeconds, LabyrinthTrials.EmergeAnimation),
                log is null ? null : "0x01d1 " + log);
            var (from, to) = LabyrinthTrials.SpikeWallWindow;
            due.Enqueue((t, Block), at + from);
            due.Enqueue((t, Unblock), at + to);
        }
        try
        {
            while (!conn.Closed)
            {
                double now = conn.Clock.Elapsed.TotalSeconds;
                while (due.TryPeek(out var next, out double at) && at <= now)
                {
                    due.Dequeue();
                    var t = next.Trap;
                    switch (next.Stage)
                    {
                        // A chain's wave: each trap at its place in it, and the next wave once it has gone round.
                        case Wave:
                            var chain = chains[t.Chain];
                            double step = t.Trigger?.Step ?? LabyrinthTrials.SpikeStep(t.Checker);
                            foreach (var c in chain) due.Enqueue((c, Fired), at + c.Order * step);
                            due.Enqueue((t, Wave), at + (t.Trigger?.Period ?? LabyrinthTrials.SpikeWaveSeconds(chain.Max(c => c.Order) + 1, step)));
                            break;
                        case Fired: await Fire(t, at); break;
                        case Block: await BlockSpikeTrap(remote, stream, ciphers, conn, t, true); break;
                        case Unblock: await BlockSpikeTrap(remote, stream, ciphers, conn, t, false); break;
                    }
                }
                (int X, int Y) here;
                lock (conn.Monsters) here = conn.Walk.Position(now);
                // A step on a pressure trap at rest clicks, and it fires a while later.
                foreach (var t in traps)
                {
                    if (t.Chain >= 0) continue;
                    if (!t.Holds(here)) { t.Armed = true; continue; }
                    if (!t.Armed || t.Stepped || now < t.Fired + LabyrinthTrials.SpikeAnimationSeconds) continue;
                    (t.Armed, t.Stepped) = (false, true);
                    due.Enqueue((t, Fired), now + LabyrinthTrials.PressureSpikeDelaySeconds);
                    _ = PressureClick(remote, stream, ciphers, conn, t.Trap);
                    Console.WriteLine($"[gateway] {remote} pressure spike trap 0x{t.Id:x} at ({t.Trap.X}, {t.Trap.Y}) stepped on: fires in {LabyrinthTrials.PressureSpikeDelaySeconds} s");
                }
                foreach (var t in traps)
                    if (!t.HasHit && t.Up(now) && t.Holds(here))
                    {
                        t.HasHit = true;
                        await SpikeTrapHit(remote, stream, ciphers, conn, t);
                    }
                await Task.Delay(SpikeTickMilliseconds);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] {remote} trial spike traps stopped: {ServerLog.Describe(ex)}"); }
    }

    // The Labyrinth's abyss edge blades: each swings on its timer (LabyrinthTrials.EdgeBladeTimer) with its Emerge clip
    // (0x1d1) and, at the clip's impact, hits a player in its strip once (LabyrinthTrials.EdgeBladeDamage), who bleeds.
    static async Task SpawnEdgeBlades(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        List<AreaPopulation.RoomObject> blades)
    {
        if (blades.Count == 0) return;
        var placed = new List<(uint Id, AreaPopulation.RoomObject Blade, HashSet<(int X, int Y)> Strip)>();
        foreach (var blade in blades)
        {
            uint id = conn.NextObjectId++;
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(blade.Type),
                LabyrinthTrials.EdgeBladeBlob(blade.X, blade.Y, blade.Angle)), null);
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
            placed.Add((id, blade, [.. LabyrinthTrials.HullCells(blade.X, blade.Y, blade.Angle, LabyrinthTrials.EdgeBladeHalfAcross, LabyrinthTrials.EdgeBladeHalfAlong)]));
        }
        Console.WriteLine($"[gateway] {remote} abyss edge blades: {placed.Count} placed");
        _ = RunEdgeBlades(remote, stream, ciphers, conn, placed);
    }

    static async Task RunEdgeBlades(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        List<(uint Id, AreaPopulation.RoomObject Blade, HashSet<(int X, int Y)> Strip)> blades)
    {
        double start = conn.Clock.Elapsed.TotalSeconds + 1;
        var next = blades.Select(b => start + LabyrinthTrials.EdgeBladeTimer(b.Blade.Type).Delay).ToArray();
        var impact = blades.Select(_ => double.PositiveInfinity).ToArray();
        try
        {
            while (!conn.Closed)
            {
                double now = conn.Clock.Elapsed.TotalSeconds;
                for (int i = 0; i < blades.Count; i++)
                {
                    var (id, blade, strip) = blades[i];
                    if (now >= next[i])
                    {
                        next[i] = Math.Max(next[i] + LabyrinthTrials.EdgeBladeTimer(blade.Type).Period, now);
                        impact[i] = now + LabyrinthTrials.EdgeBladeImpactSeconds;
                        await Send(remote, stream, ciphers, InstanceProtocol.ActorAnimation(id, (float)LabyrinthTrials.EdgeBladeClipSeconds, LabyrinthTrials.EmergeAnimation), null);
                    }
                    if (now < impact[i]) continue;
                    impact[i] = double.PositiveInfinity;
                    uint life;
                    int damage;
                    lock (conn.Monsters)
                    {
                        if (!strip.Contains(conn.Walk.Position(now))
                            || LabyrinthTrials.EdgeBladeDamage(conn.PlayerMaxLife, conn.EnergyShield.Maximum) is not int dealt
                            || TrapHitLocked(conn, dealt, now) is not uint left) continue;
                        (damage, life) = (dealt, left);
                    }
                    if (await PresentTrapHit(remote, stream, ciphers, conn, id, $"abyss edge blade 0x{id:x} at ({blade.X}, {blade.Y})", damage, life))
                        await SpikeBleed(remote, stream, ciphers, conn, damage, LabyrinthTrials.EdgeBladeBleedPerSecond, LabyrinthTrials.EdgeBladeBleedSeconds);
                }
                await Task.Delay(SpikeTickMilliseconds);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] {remote} abyss edge blades stopped: {ServerLog.Describe(ex)}"); }
    }

    // A pressure spike trap's click: spiketrap_trigger.ao, whose default clip plays the Trigger sound, as an effect object
    // on the trap for as long as it plays.
    static async Task PressureClick(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, AreaPopulation.RoomObject trap)
    {
        uint id = conn.NextObjectId++;
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
            new EffectObject(trap.X, trap.Y, LabyrinthTrials.PressureSpikeClick).Blob()), null);
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
        await Task.Delay(TimeSpan.FromSeconds(LabyrinthTrials.PressureSpikeDelaySeconds));
        if (!conn.Closed) await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), null);
    }

    // Risen spikes are impassable (wiki: "Spike traps are impassable and block movement while they are out"): the trap's
    // TriggerableBlockage (0x1c6) for the client, and its square's walkable cells on the server's walk estimate.
    static async Task BlockSpikeTrap(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, SpikeTrap t,
        bool blocked)
    {
        lock (conn.Monsters)
        {
            if (!blocked) conn.DoorBarriers.Remove(t.Id);
            // A player already on the tile when the spikes rise stays there: closing its cells under them made the
            // collision escape throw them off the tile (live 2026-10-31); the wall only keeps others out.
            else if (t.Holds(conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds))) { }
            else if (conn.Layout.Walkable is { } walkable)
            {
                var cells = new HashSet<(int X, int Y)>();
                for (int y = t.Trap.Y - LabyrinthTrials.SpikeHalf; y <= t.Trap.Y + LabyrinthTrials.SpikeHalf; y++)
                    for (int x = t.Trap.X - LabyrinthTrials.SpikeHalf; x <= t.Trap.X + LabyrinthTrials.SpikeHalf; x++)
                        if (walkable(x, y)) cells.Add((x, y));
                conn.DoorBarriers[t.Id] = cells;
            }
            conn.UpdateBarrier();
        }
        await Send(remote, stream, ciphers, InstanceEntities.BlockageUpdate(t.Id, blocked), null);
    }

    // How often the spike traps' timers and the player's steps are looked at (our own number).
    const int SpikeTickMilliseconds = 50;

    // A player in a spike trap's square while its spikes are up takes the trap's share of life and energy shield
    // (LabyrinthTrials.SpikeDamage) as physical damage, energy shield first, and may die of it.
    static async Task SpikeTrapHit(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, SpikeTrap spike)
    {
        var (id, trap) = (spike.Id, spike.Trap);
        uint life;
        int damage;
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            if (!spike.Holds(conn.Walk.Position(now))
                || LabyrinthTrials.SpikeDamage(conn.PlayerMaxLife, conn.EnergyShield.Maximum) is not int dealt
                || TrapHitLocked(conn, dealt, now) is not uint left) return;
            (damage, life) = (dealt, left);
        }
        if (await PresentTrapHit(remote, stream, ciphers, conn, id, $"trial spike trap 0x{id:x} at ({trap.X}, {trap.Y})", damage, life))
            await SpikeBleed(remote, stream, ciphers, conn, damage);
    }

    // Under conn.Monsters: a trap's hit of so much physical damage on the player, energy shield first, which may kill them.
    // The life left, or null when the player is dead already (or a hardcore death holds them).
    static uint? TrapHitLocked(ClientState conn, int damage, double now)
    {
        if (conn.PlayerLife == 0 || HardcoreProgressBlocked(conn)) return null;
        long before = conn.PlayerLife, lifeDamage = DamagePlayerHit(conn, damage, 0, now, CharacterPlayerStats(conn));
        conn.PlayerLife = (uint)Math.Max(0, before - lifeDamage);
        if (conn.PlayerLife == 0) { conn.InvalidateIncomingHits(); ClearAntiquarianOnDeath(conn); }
        RecordHardcoreDeath(conn);
        return conn.PlayerLife;
    }

    // A trap's hit as the client shows it: the hit from the trap (0x1af), the pools and, when it killed, the death screen.
    // True while the player lives.
    static async Task<bool> PresentTrapHit(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id,
        string trap, int damage, uint life)
    {
        var taken = GameServer.Core.World.DamagePacket.Empty.Add(GameServer.Core.World.DamageType.Physical, new(damage, damage));
        await Send(remote, stream, ciphers, InstanceProtocol.ActionHit(id, 0xffff, PlayerId, flags: MonsterHitFlags(false, false),
            kind: 0x03, code: HitCode(taken)), null);
        await SendPlayerEnergyShield(remote, stream, ciphers, conn);
        if (life == 0)
            await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, [(StatKeys.HideMiniLifeBar, 1)]),
                "0x01b3 player hide_mini_life_bar = 1");
        await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, life),
            $"0x01b1 player hit by {trap} for {damage}, life {life}/{conn.PlayerMaxLife}{(life == 0 ? " (dies)" : "")}");
        if (life == 0) await PlayerDied(remote, stream, ciphers, conn, $"0x0055 death screen ({conn.League ?? "?"} league)");
        return life > 0;
    }

    // Whether the player cannot bleed (the character panel's cannot_gain_bleeding, virtual_immune_to_bleeding: a flask's
    // immunity too): no spike trap bleed. A roomba's cut is no bleed: it harms such a player too (live 2026-11-01).
    static bool CannotBleed(ClientState conn)
    {
        var stats = CharacterPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        return Get("cannot_gain_bleeding") != 0 || Get("virtual_immune_to_bleeding") != 0;
    }

    // The bleed a spike hit leaves on a connection: its damage a second while standing, and until when (Clock seconds).
    sealed class Bleed { public double PerSecond, Until; public ushort Buff; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ClientState, Bleed> Bleeds = new();

    // A spike hit bleeds (LabyrinthTrials.SpikeBleedPerSecond of its damage a second, three times that while the player
    // moves, for SpikeBleedSeconds); only the strongest bleed runs, so a weaker one while another lasts is dropped. Its
    // damage goes to energy shield first, then life, in SpikeBleedTickSeconds ticks, off the traps' loop.
    static async Task SpikeBleed(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, int hit,
        double share = LabyrinthTrials.SpikeBleedPerSecond, double seconds = LabyrinthTrials.SpikeBleedSeconds)
    {
        if (CannotBleed(conn)) return;
        var bleed = Bleeds.GetValue(conn, _ => new());
        double perSecond = hit * share, until;
        ushort buff;
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            if (now < bleed.Until && bleed.PerSecond > perSecond) return;
            bool running = now < bleed.Until;
            (bleed.PerSecond, bleed.Until) = (perSecond, until = now + seconds);
            buff = bleed.Buff = conn.NextBuff++;
            if (running) return;   // the running ticks take the new bleed on
        }
        if (LabyrinthTrials.BleedingBuffRow is ushort row)
            await Send(remote, stream, ciphers, InstanceEntities.AddBuff(PlayerId, buff, row,
                u68: BitConverter.SingleToUInt32Bits((float)seconds)), null);
        _ = Task.Run(async () =>
        {
            ushort shown = buff;
            bool died = false;
            while (!conn.Closed)
            {
                await Task.Delay(TimeSpan.FromSeconds(SpikeBleedTickSeconds));
                uint life;
                int dealt;
                bool over;
                lock (conn.Monsters)
                {
                    double now = conn.Clock.Elapsed.TotalSeconds;
                    // Dead of something else: the bleed ends, and its buff (removed below) with it.
                    if (conn.PlayerLife == 0 || HardcoreProgressBlocked(conn)) { bleed.Until = double.NegativeInfinity; break; }
                    double factor = now < conn.Walk.ArrivesAt ? LabyrinthTrials.SpikeBleedMovingFactor : 1;
                    dealt = (int)Math.Round(bleed.PerSecond * factor * SpikeBleedTickSeconds);
                    long lifeDamage = conn.EnergyShield.Damage(dealt, false, now);
                    conn.PlayerLife = (uint)Math.Max(0, conn.PlayerLife - lifeDamage);
                    if (conn.PlayerLife == 0) { conn.InvalidateIncomingHits(); ClearAntiquarianOnDeath(conn); }
                    RecordHardcoreDeath(conn);
                    life = conn.PlayerLife;
                    died = life == 0;
                    over = died || now + SpikeBleedTickSeconds / 2 >= bleed.Until;
                    if (over) bleed.Until = double.NegativeInfinity;   // a later hit starts a bleed of its own
                    if (bleed.Buff != shown && LabyrinthTrials.BleedingBuffRow is not null)
                    {
                        // A stronger bleed took over: its buff instance replaces the shown one.
                        (ushort old, shown) = (shown, bleed.Buff);
                        _ = Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, old), null);
                        _ = Send(remote, stream, ciphers, InstanceEntities.AddBuff(PlayerId, shown, LabyrinthTrials.BleedingBuffRow.Value,
                            u68: BitConverter.SingleToUInt32Bits((float)(bleed.Until - now))), null);
                    }
                }
                await SendPlayerEnergyShield(remote, stream, ciphers, conn);
                await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, life),
                    life == 0 ? $"0x01b1 player bleeds {dealt} from a trial spike trap and dies" : null);
                if (over) break;
            }
            if (!conn.Closed && LabyrinthTrials.BleedingBuffRow is not null)
                await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, shown), null);
            if (!conn.Closed && died)
                await PlayerDied(remote, stream, ciphers, conn, $"0x0055 death screen ({conn.League ?? "?"} league)");
        });
    }

    // How often a spike trap's bleed deals its damage (our own number).
    const double SpikeBleedTickSeconds = 0.2;

    // A trial's dart traps (LabyrinthTrials.ArrowTrap, the Imperial Gardens' 14 turrets): created at rest, then all fire
    // together every LabyrinthTrials.ArrowVolleySeconds for as long as the player is in the area.
    static async Task SpawnArrowTraps(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        List<AreaPopulation.RoomObject> traps)
    {
        if (traps.Count == 0) return;
        var placed = new List<(uint Id, AreaPopulation.RoomObject Trap)>();
        foreach (var trap in traps)
        {
            uint id = conn.NextObjectId++;
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(trap.Type),
                LabyrinthTrials.ArrowTrapBlob(trap.X, trap.Y, trap.Angle, trap.Model, trap.Type)), null);
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
            placed.Add((id, trap));
            // Its Hull, as the client stamps it: a wall to the darts and to the player's walk estimate.
            lock (conn.Monsters)
            {
                conn.ProjectileWalls.UnionWith(LabyrinthTrials.HullCells(trap.X, trap.Y, trap.Angle, LabyrinthTrials.ArrowTrapProjectileHalf, LabyrinthTrials.ArrowTrapProjectileHalf));
                conn.DoorBarriers[id] = LabyrinthTrials.HullCells(trap.X, trap.Y, trap.Angle, LabyrinthTrials.ArrowTrapWalkHalf, LabyrinthTrials.ArrowTrapWalkHalf).ToHashSet();
                conn.UpdateBarrier();
            }
        }
        Console.WriteLine($"[gateway] {remote} trial dart traps: {placed.Count} placed");
        _ = RunArrowTraps(remote, stream, ciphers, conn, placed);
    }

    // Each turret's volley (LabyrinthTrials.Volley) on its own timer: it plays its cast (0x1d1) and releases its darts
    // into the connection's projectile world, each its turn from its facing and its time into the volley; the world flies
    // them, stops them at walls and turrets and hands a contact to ArrowHit. A player stepping on a pressure plate presses
    // it (Transitionable 2) and fires the wall darts its room's plates fire (LabyrinthTrials.TriggeredArrowTrap), one dart
    // ahead each; the plate comes up as its kind does (LabyrinthTrials.PlateDownSeconds).
    static async Task RunArrowTraps(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        List<(uint Id, AreaPopulation.RoomObject Trap)> traps)
    {
        var skills = SkillsFor(conn);
        double speed = ProjectileSpeed.CellsPerSecond(skills.Projectile(LabyrinthTrials.ArrowProjectile)?.Speed ?? 0, _ => 0, _ => 0);
        if (speed <= 0)
        {
            Console.WriteLine($"[gateway] {remote} trial dart traps: no {LabyrinthTrials.ArrowProjectile} in Projectiles.dat, they stay still");
            return;
        }
        // The dart's rig: spine_shoot in flight, spine_shatter where it stops (data/projectile-clips.json), from the
        // turret's cast contact. A dart flies until a wall or the player stops it, at most the projectile range.
        var presentation = (MonsterProjectilePresentation(skills.Clips(LabyrinthTrials.ArrowProjectile), 1) ?? new())
            with { LaunchHeight = LabyrinthTrials.ArrowLaunchHeight };
        async Task Fire(uint id, AreaPopulation.RoomObject trap, IEnumerable<(double Turn, double At)> shots)
        {
            await Send(remote, stream, ciphers, InstanceProtocol.ActorAnimation(id, 1, LabyrinthTrials.CastAnimation), null);
            var session = ProjectilesFor(remote, stream, ciphers, conn);
            session.Advance();
            foreach (var (turn, at) in shots)
            {
                double a = trap.Angle + turn;
                var direction = new ProjectileVector(Math.Sin(a), -Math.Cos(a));
                var origin = new ProjectileVector(trap.X, trap.Y) + direction * LabyrinthTrials.ArrowForwardCells;
                var owner = new ProjectileActorKey(id);
                var profile = new ProjectileProfile(LabyrinthTrials.ArrowProjectile) { Speed = speed, Radius = 1, Count = 1 };
                session.Schedule(session.Runtime.Now + GameServer.Core.Projectiles.ProjectileRuntime.Time(at), () => !conn.Closed, () =>
                    new ProjectileUse(profile, owner, owner, 2, origin, origin + direction * 10)
                    {
                        Payload = new ObjectProjectileOffense($"trial dart trap 0x{id:x}",
                            (shot, contact) => ArrowHit(shot, contact, id, trap)) { Presentation = presentation, Quiet = true },
                    });
            }
        }
        var timed = traps.Select(t => (t.Id, t.Trap, Volley: LabyrinthTrials.Volley(t.Trap.Type))).Where(t => t.Volley is not null).ToList();
        var triggered = traps.Where(t => t.Trap.Type == LabyrinthTrials.TriggeredArrowTrap).ToList();
        var rooms = ServerLevels.For(conn.Layout.Id, conn.Seed)?.Rooms ?? [];
        int RoomOf(int x, int y) => rooms.ToList().FindLastIndex(r =>
            x >= r.X && y >= r.Y && x < r.X + (r.Code % 2 == 1 ? r.Height : r.Width) && y < r.Y + (r.Code % 2 == 1 ? r.Width : r.Height));
        var plates = new Dictionary<uint, (bool Down, double At, bool Left)>();
        try
        {
            double start = conn.Clock.Elapsed.TotalSeconds + 1;
            var next = timed.Select(t => start + t.Volley!.Delay).ToArray();
            while (!conn.Closed)
            {
                double now = conn.Clock.Elapsed.TotalSeconds;
                for (int i = 0; i < timed.Count; i++)
                {
                    if (now < next[i]) continue;
                    next[i] = Math.Max(next[i] + timed[i].Volley!.Period, now);
                    await Fire(timed[i].Id, timed[i].Trap, timed[i].Volley!.Shots);
                }
                if (triggered.Count > 0)
                {
                    List<KeyValuePair<uint, AreaPopulation.RoomObject>> placed;
                    (int X, int Y) here;
                    lock (conn.Monsters)
                    {
                        placed = [.. conn.TrialObjects.Where(t => LabyrinthTrials.IsPressurePlate(t.Value.Type))];
                        here = conn.Walk.Position(now);
                    }
                    foreach (var (id, plate) in placed)
                    {
                        bool on = Math.Abs(here.X - plate.X) <= LabyrinthTrials.PlateHalf && Math.Abs(here.Y - plate.Y) <= LabyrinthTrials.PlateHalf;
                        var (down, at, left) = plates.GetValueOrDefault(id, (false, 0, true));
                        left |= !on;
                        double? hold = LabyrinthTrials.PlateDownSeconds(plate.Type);
                        if (down && (hold is double h ? now >= at + h : !on))
                        {
                            down = false;
                            await Send(remote, stream, ciphers, InstanceEntities.TransitionableUpdate(id, LabyrinthTrials.TransitionableIndex(plate.Type), LabyrinthTrials.Closed),
                                $"0x02ba pressure plate 0x{id:x} at ({plate.X}, {plate.Y}) comes up");
                        }
                        bool again = plate.Type.EndsWith("Multi_4", StringComparison.Ordinal);
                        if (!down && on && (left || again))
                        {
                            (down, at, left) = (true, now, false);
                            int room = RoomOf(plate.X, plate.Y);
                            var fired = triggered.Where(t => RoomOf(t.Trap.X, t.Trap.Y) == room).ToList();
                            await Send(remote, stream, ciphers, InstanceEntities.TransitionableUpdate(id, LabyrinthTrials.TransitionableIndex(plate.Type), LabyrinthTrials.Open),
                                $"0x02ba pressure plate 0x{id:x} at ({plate.X}, {plate.Y}) pressed: {fired.Count} wall dart(s) fire");
                            foreach (var (dart, trap) in fired) await Fire(dart, trap, [(0, 0)]);
                        }
                        plates[id] = (down, at, left);
                    }
                }
                await Task.Delay(50);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] {remote} trial dart traps stopped: {ServerLog.Describe(ex)}"); }
    }

    static ProjectileHitResult ArrowHit(ProjectileSession session, ProjectileDamage contact, uint id, AreaPopulation.RoomObject trap)
    {
        var conn = session.Conn;
        if (contact.Target.Id != PlayerId || InGracePeriod(conn)) return ProjectileHitResult.Miss;
        double now = Math.Max(contact.At / (double)ProjectileRuntime.QuantaPerSecond, conn.Clock.Elapsed.TotalSeconds);
        // The dart is an arrow (its skill's skill_can_fire_arrows): a physical hit, which the player's armour reduces.
        if (LabyrinthTrials.ArrowDamage(conn.PlayerMaxLife, conn.EnergyShield.Maximum) is not int share) return ProjectileHitResult.Miss;
        int damage = PlayerHitMitigation.AfterResistance(conn.Pools, GameServer.Core.World.DamageType.Physical, share);
        if (TrapHitLocked(conn, damage, now) is not uint life) return ProjectileHitResult.Miss;
        int poison = LabyrinthTrials.ArrowPoisonBase(conn.PlayerMaxLife) ?? 0;
        session.Enqueue(async () =>
        {
            if (await PresentTrapHit(session.remote, session.stream, session.ciphers, conn, id,
                    $"trial dart trap 0x{id:x} at ({trap.X}, {trap.Y})", damage, life))
            {
                await ArrowPoison(session.remote, session.stream, session.ciphers, conn, poison);
                await ArrowSlow(session.remote, session.stream, session.ciphers, conn);
            }
        });
        return ProjectileHitResult.Hit;
    }

    // The poisons on a connection: each stack's damage a second, until when (Clock seconds) and its buff instance, and
    // whether their ticks run.
    sealed class Poisons
    {
        public readonly List<(double PerSecond, double Until, ushort Buff)> Stacks = [];
        public bool Ticking;
        public double Carried;   // the part of a point dealt but not yet taken
    }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ClientState, Poisons> PoisonStacks = new();

    // A dart's poison (LabyrinthTrials.ArrowPoisonPerSecond of its base, ArrowPoisonBase, a second for ArrowPoisonSeconds),
    // a stack of its own with its own Poisoned buff, which the client counts. The stacks' chaos damage goes past energy
    // shield, less the player's chaos resistance, in SpikeBleedTickSeconds ticks, off the traps' loop.
    static async Task ArrowPoison(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, int hit)
    {
        if (hit <= 0) return;
        var poisons = PoisonStacks.GetValue(conn, _ => new());
        ushort buff;
        bool start;
        lock (conn.Monsters)
        {
            buff = conn.NextBuff++;
            poisons.Stacks.Add((hit * LabyrinthTrials.ArrowPoisonPerSecond, conn.Clock.Elapsed.TotalSeconds + LabyrinthTrials.ArrowPoisonSeconds, buff));
            (start, poisons.Ticking) = (!poisons.Ticking, true);
        }
        if (LabyrinthTrials.PoisonBuffRow is ushort row)
            await Send(remote, stream, ciphers, InstanceEntities.AddBuff(PlayerId, buff, row,
                u68: BitConverter.SingleToUInt32Bits((float)LabyrinthTrials.ArrowPoisonSeconds)), null);
        if (!start) return;   // the running ticks take the new stack on
        _ = Task.Run(async () =>
        {
            while (!conn.Closed)
            {
                await Task.Delay(TimeSpan.FromSeconds(SpikeBleedTickSeconds));
                uint life = 0;
                int dealt = 0;
                bool over, died = false;
                List<ushort> ended;
                lock (conn.Monsters)
                {
                    double now = conn.Clock.Elapsed.TotalSeconds;
                    // Dead, of this or of something else: every stack ends, and its buff with it (the client keeps a
                    // buff until it is removed, through the resurrection too: live 2026-10-31).
                    bool dead = conn.PlayerLife == 0 || HardcoreProgressBlocked(conn);
                    if (!dead)
                    {
                        // Each stack deals for the part of this tick it lasted.
                        double raw = poisons.Stacks.Sum(s => s.PerSecond * Math.Clamp(s.Until - (now - SpikeBleedTickSeconds), 0, SpikeBleedTickSeconds));
                        var chaos = GameServer.Core.World.DamageType.Chaos;
                        poisons.Carried += GameServer.Core.World.DamageModel.Mitigate(raw, Resistance(conn, chaos), MaxResistance(conn, chaos));
                        dealt = (int)poisons.Carried;
                        poisons.Carried -= dealt;
                        long lifeDamage = conn.EnergyShield.Damage(dealt, true, now);
                        conn.PlayerLife = (uint)Math.Max(0, conn.PlayerLife - lifeDamage);
                        if (conn.PlayerLife == 0) { conn.InvalidateIncomingHits(); ClearAntiquarianOnDeath(conn); }
                        RecordHardcoreDeath(conn);
                        life = conn.PlayerLife;
                        dead = died = life == 0;
                    }
                    ended = [.. poisons.Stacks.Where(s => dead || s.Until <= now).Select(s => s.Buff)];
                    poisons.Stacks.RemoveAll(s => dead || s.Until <= now);
                    // Over here, under the lock: a stack added after this starts ticks of its own.
                    over = poisons.Stacks.Count == 0;
                    if (over) (poisons.Ticking, poisons.Carried) = (false, 0);
                }
                if (dealt > 0)
                    await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, life),
                        died ? $"0x01b1 player takes {dealt} poison damage from a trial dart trap and dies" : null);
                if (LabyrinthTrials.PoisonBuffRow is not null)
                    foreach (ushort old in ended) await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, old), null);
                if (died) await PlayerDied(remote, stream, ciphers, conn, $"0x0055 death screen ({conn.League ?? "?"} league)");
                if (over) return;
            }
        });
    }

    // The darts' slow on a connection: whether the pools carry it, until when (Clock seconds) and its buff instance.
    sealed class DartSlow
    {
        public bool Active;
        public double Until;
        public ushort Buff;
    }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ClientState, DartSlow> DartSlows = new();

    // PlayerStatSources: a slowed player's final movement modifier (LabyrinthTrials.ArrowSlowStat), which the pools apply
    // to their whole movement speed.
    internal static Dictionary<uint, int> WithDartSlow(ClientState conn, Dictionary<uint, int> sources)
    {
        if (DartSlows.TryGetValue(conn, out var slow) && slow.Active && Stats.Key(LabyrinthTrials.ArrowSlowStat) is uint key)
            sources[key] = sources.GetValueOrDefault(key) + LabyrinthTrials.ArrowSlowPercent;
        return sources;
    }

    // A dart's slow (LabyrinthTrials.ArrowSlowPercent for ArrowSlowSeconds from the latest hit, not stacking): it starts
    // with the first hit, a later one moves its end, and it ends then or at a death.
    static async Task ArrowSlow(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var slow = DartSlows.GetValue(conn, _ => new());
        lock (conn.Monsters)
        {
            slow.Until = conn.Clock.Elapsed.TotalSeconds + LabyrinthTrials.ArrowSlowSeconds;
            if (slow.Active) return;   // the running watch keeps it on to the new end
            (slow.Active, slow.Buff) = (true, conn.NextBuff++);
        }
        await DartSlowChanged(remote, stream, ciphers, conn, slow);
        _ = Task.Run(async () =>
        {
            while (!conn.Closed)
            {
                double left;
                lock (conn.Monsters)
                {
                    left = conn.PlayerLife == 0 ? 0 : slow.Until - conn.Clock.Elapsed.TotalSeconds;
                    if (left <= 0) slow.Active = false;   // a hit after this starts a slow of its own
                }
                if (left > 0) { await Task.Delay(TimeSpan.FromSeconds(left)); continue; }
                await DartSlowChanged(remote, stream, ciphers, conn, slow);
                return;
            }
        });
    }

    // The slow came or went: the pools again (WithDartSlow), the walk estimate's speed, the client's stats (its own walk
    // and the panel read movement_velocity_+%) and the invisible labyrinth_arrow_slow buff.
    static async Task DartSlowChanged(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, DartSlow slow)
    {
        bool on;
        lock (conn.Monsters)
        {
            on = slow.Active;
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
        }
        if (LabyrinthTrials.ArrowSlowBuffRow is ushort row)
            await Send(remote, stream, ciphers, on
                ? InstanceEntities.AddBuff(PlayerId, slow.Buff, row, u68: BitConverter.SingleToUInt32Bits((float)LabyrinthTrials.ArrowSlowSeconds))
                : InstanceEntities.RemoveBuff(PlayerId, slow.Buff), null);
        await SendPlayerStatSnapshot(remote, stream, ciphers, conn, () => PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)),
            $"0x01b3 player stats (trial dart slow {(on ? "starts" : "ends")}: movement {conn.Pools.MovementVelocityPct:+0;-0}%)");
    }

    // A trial's roombas (the Catacombs: seven on the ground, one flying) and sawblades (the Chamber of Sins: nine), each on
    // its run of rails (LabyrinthTrials.RoombaPaths; a sawblade heads for its track's nearer end first): created where the
    // room puts them, then driven along their rails for as long as the player is in the area (RunRoombas). A sawblade a
    // pulled lever (Switch) stops in this instance starts stopped (LabyrinthTrials.SwitchedSaws: the levers and the
    // trial's door).
    static async Task SpawnRoombas(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        List<AreaPopulation.RoomObject> roombas, List<AreaPopulation.RoomObject> rails, List<(int X, int Y)> switches, (int X, int Y) entrance,
        List<(int X, int Y)>? scratches)
    {
        if (roombas.Count == 0) return;
        // A visible rail is a track piece, joined along its angle only; the Labyrinth's rooms' runs follow their scratches.
        var invisible = rails.Where(r => r.Type == LabyrinthTrials.Rail).ToList();
        var paths = LabyrinthTrials.RoombaPaths([.. rails.Select(r => (r.X, r.Y))], [.. roombas.Select(r => (r.X, r.Y, r.Angle))],
            [.. rails.Select(r => r.Type == LabyrinthTrials.VisibleRail ? r.Angle : float.NaN)]);
        if (scratches is not null && invisible.Count > 0)
        {
            var scratched = LabyrinthTrials.RoombaPaths([.. invisible.Select(r => (r.X, r.Y))], [.. roombas.Select(r => (r.X, r.Y, r.Angle))],
                null, scratches, [.. roombas.Select(r => r.Type == LabyrinthTrials.FlyingRoomba)]);
            for (int i = 0; i < roombas.Count; i++)
                if (roombas[i].Type is LabyrinthTrials.Roomba or LabyrinthTrials.FlyingRoomba) paths[i] = scratched[i];
        }
        var saws = Enumerable.Range(0, roombas.Count).Where(i => LabyrinthTrials.IsSawblade(roombas[i].Type)).ToList();
        var halted = Enumerable.Range(0, switches.Count)
            .Where(k => conn.Instance.Transitioned(LabyrinthTrials.Switch, switches[k].X, switches[k].Y))
            .SelectMany(k => LabyrinthTrials.SwitchedSaws([.. saws.Select(i => (roombas[i].X, roombas[i].Y))], switches, k, entrance))
            .Select(k => saws[k]).ToHashSet();
        // The Crypt trial's spinners go piece to piece on their track, which its rail switches change as they stand in
        // this instance.
        var track = roombas.Any(r => r.Type == LabyrinthTrials.Spinner)
            ? LabyrinthTrials.SpinnerTrack.Build([.. rails.Select(r => (r.X, r.Y, r.Angle, r.Model))], [.. rails.Select(r => r.Mirrored)]) : null;
        bool Lifted(int k) => conn.Instance.Transitioned(LabyrinthTrials.RailSwitch, track!.Rails[k].X, track.Rails[k].Y)
            || conn.Instance.Transitioned(LabyrinthTrials.RailSwitchSwapped, track.Rails[k].X, track.Rails[k].Y);
        var runs = new List<RoombaRun>();
        for (int i = 0; i < roombas.Count; i++)
        {
            var r = roombas[i];
            uint id = conn.NextObjectId++;
            bool stopped = halted.Contains(i), spinner = r.Type == LabyrinthTrials.Spinner;
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(r.Type), spinner
                ? LabyrinthTrials.SpinnerBlob(r.X, r.Y, r.Angle, r.Model)
                : LabyrinthTrials.RoombaBlob(r.Type, r.X, r.Y, r.Angle, r.Model, stopped ? LabyrinthTrials.Open : LabyrinthTrials.Closed)), null);
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
            if (spinner && track?.Start((r.X, r.Y), r.Angle, Lifted) is { } run)
                runs.Add(new RoombaRun(id, r, track, run, Lifted));
            else if (!spinner && paths[i] is { } path)
                runs.Add(new RoombaRun(id, r, LabyrinthTrials.IsSawblade(r.Type) ? path.TowardNearerEnd((r.X, r.Y)) : path) { Halted = stopped, Stopped = stopped });
            else Console.WriteLine($"[gateway] {remote} trial roomba 0x{id:x} {r.Type} at ({r.X}, {r.Y}): no rail under it, it stays");
        }
        Console.WriteLine($"[gateway] {remote} trial roombas, sawblades and spinners: {roombas.Count} placed, " +
            string.Join(", ", runs.Select(r => $"0x{r.Id:x} " + (r.Path is { } p ? $"{(p.Ring ? "round" : "to and fro")} {p.Rails.Count} rails" : $"on the track from {r.To}")
                + (r.Stopped ? " (stopped)" : ""))));
        TrapRuns.AddOrUpdate(conn, runs);
        _ = RunRoombas(remote, stream, ciphers, conn, runs);
    }

    // A connection's roombas and sawblades on their rails, for its levers to stop and start.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ClientState, List<RoombaRun>> TrapRuns = new();

    // A roomba or sawblade on its rails: its object (a stopped sawblade starts again as a new one), the leg it is on (from
    // where, to which rail, when it set out and arrives), which way it goes along a chain, and whether a lever wants it
    // stopped (Halted, set by ToggleTrialSwitch) and whether it is (Stopped, RunRoombas). A spinner goes on its track
    // instead of a path: from piece to piece, which way it turns its corners, and how it reads the rail switches.
    sealed class RoombaRun
    {
        public uint Id;
        public readonly AreaPopulation.RoomObject Roomba;
        public readonly LabyrinthTrials.RoombaPath? Path;
        readonly LabyrinthTrials.SpinnerTrack? track;
        readonly Func<int, bool>? lifted;
        public int Target, Step = 1, Previous, Turn;
        public (double X, double Y) From;
        public double LegStart, LegEnd = double.NegativeInfinity;
        public volatile bool Halted;
        public bool Stopped;

        public RoombaRun(uint id, AreaPopulation.RoomObject roomba, LabyrinthTrials.RoombaPath path)
        {
            (Id, Roomba, Path, From) = (id, roomba, path, (roomba.X, roomba.Y));
            // It heads first for the far rail of the run it stands on: the path's order is the way it faces.
            var rails = path.Rails;
            int legs = path.Ring ? rails.Count : rails.Count - 1;
            Target = (Enumerable.Range(0, legs).MinBy(i => Distance((roomba.X, roomba.Y), rails[i], rails[(i + 1) % rails.Count])) + 1) % rails.Count;
        }

        public RoombaRun(uint id, AreaPopulation.RoomObject spinner, LabyrinthTrials.SpinnerTrack track, (int From, int To) run, Func<int, bool> lifted) =>
            (Id, Roomba, this.track, this.lifted, Previous, Target, From) = (id, spinner, track, lifted, run.From, run.To, (spinner.X, spinner.Y));

        public (double X, double Y) To => track is not null ? track.Rails[Target] : Path!.Rails[Target];

        public (double X, double Y) Position(double now)
        {
            if (Stopped) return From;
            if (now >= LegEnd) return To;
            double t = Math.Clamp((now - LegStart) / Math.Max(1e-6, LegEnd - LegStart), 0, 1);
            return (From.X + (To.X - From.X) * t, From.Y + (To.Y - From.Y) * t);
        }

        // The next rail: round a ring, or on along a chain, turning at its ends; a spinner's as its track decides.
        public void Next()
        {
            if (track is not null)
            {
                (int next, Turn) = track.Next(Previous, Target, Turn, lifted!);
                (Previous, Target) = (Target, next);
                return;
            }
            if (Path!.Ring) { Target = (Target + 1) % Path.Rails.Count; return; }
            if (Target + Step < 0 || Target + Step >= Path.Rails.Count) Step = -Step;
            Target += Step;
        }

        static double Distance((int X, int Y) p, (int X, int Y) a, (int X, int Y) b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, len = dx * dx + dy * dy;
            double t = len == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len, 0, 1);
            return Math.Sqrt(Math.Pow(p.X - a.X - t * dx, 2) + Math.Pow(p.Y - a.Y - t * dy, 2));
        }
    }

    // Each roomba or sawblade glides rail to rail (0x1ba; a roomba turned to where it goes with 0x1b9) and waits
    // LabyrinthTrials.RoombaPauseSeconds at each; a player within LabyrinthTrials.TrapRadius of a running one degenerates
    // for as long as they stay (ClientState.TrapDegenPerSecond). A sawblade a lever stops winds its spin down
    // (Transitionable 2) where it is; started again, it is created anew there, spinning (its .ot plays nothing on the way
    // back to state 1), and goes on to the rail it was heading for.
    static async Task RunRoombas(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, List<RoombaRun> runs)
    {
        var degen = new TrapDegen();
        try
        {
            while (!conn.Closed)
            {
                double now = conn.Clock.Elapsed.TotalSeconds;
                foreach (var r in runs)
                {
                    if (r.Halted != r.Stopped)
                    {
                        var at = r.Position(now);
                        (int X, int Y) cell = ((int)Math.Round(at.X), (int)Math.Round(at.Y));
                        if (r.Halted)
                        {
                            (r.From, r.Stopped) = (at, true);
                            await Send(remote, stream, ciphers, [.. InstanceEntities.TransitionableUpdate(r.Id, LabyrinthTrials.RoombaTransitionableIndex, LabyrinthTrials.Open),
                                .. InstanceProtocol.MoveTo(r.Id, cell.X, cell.Y, 0.05f, InstanceProtocol.FollowTerrain)],
                                $"0x02ba trial sawblade 0x{r.Id:x} stops at ({cell.X}, {cell.Y})");
                            continue;
                        }
                        uint previous = r.Id;
                        lock (conn.Monsters) r.Id = conn.NextObjectId++;
                        await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(previous), null);
                        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(r.Id, InstanceEntities.ObjectTypeHash(r.Roomba.Type),
                            LabyrinthTrials.RoombaBlob(r.Roomba.Type, cell.X, cell.Y, r.Roomba.Angle, r.Roomba.Model)),
                            $"0x02b9 trial sawblade 0x{r.Id:x} (was 0x{previous:x}) runs on from ({cell.X}, {cell.Y})");
                        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(r.Id), null);
                        (r.From, r.Stopped, r.LegEnd) = (at, false, double.NegativeInfinity);
                    }
                    if (r.Stopped || now < r.LegEnd + LabyrinthTrials.RoombaPauseSeconds(r.Roomba.Type)) continue;
                    if (double.IsFinite(r.LegEnd)) { r.From = r.To; r.Next(); }
                    double dx = r.To.X - r.From.X, dy = r.To.Y - r.From.Y;
                    double seconds = Math.Max(0.05, Math.Sqrt(dx * dx + dy * dy) / LabyrinthTrials.RoombaSpeed(r.Roomba.Type));
                    (r.LegStart, r.LegEnd) = (now, now + seconds);
                    var move = InstanceProtocol.MoveTo(r.Id, (int)Math.Round(r.To.X), (int)Math.Round(r.To.Y), (float)seconds, InstanceProtocol.FollowTerrain);
                    // A roomba turns to where it goes (rotate_to_movement_direction); a sawblade keeps its room line's
                    // angle, along its track, and only goes back at an end (live 2026-11-01, the original), as a
                    // spinner does.
                    await Send(remote, stream, ciphers, !LabyrinthTrials.TurnsToMovement(r.Roomba.Type) ? move
                        : [.. InstanceProtocol.Orientation(r.Id, (float)Math.Atan2(dx, -dy)), .. move], null);
                }
                // In a roomba: its degeneration on the player and its buff, whose visual plays the roomba's cut on them.
                (int X, int Y) here;
                lock (conn.Monsters) here = conn.Walk.Position(now);
                var inside = runs.FirstOrDefault(r => !r.Stopped && r.Position(now) is var p && LabyrinthTrials.TrapRadius(r.Roomba.Type) is var radius
                    && (p.X - here.X) * (p.X - here.X) + (p.Y - here.Y) * (p.Y - here.Y) <= radius * radius);
                double perSecond = 0;
                if (inside is not null)
                {
                    var (lifeShare, shieldShare) = LabyrinthTrials.TrapDegenShares(inside.Roomba.Type);
                    lock (conn.Monsters) perSecond = conn.PlayerMaxLife * lifeShare + conn.EnergyShield.Maximum * shieldShare;
                }
                await degen.Set(remote, stream, ciphers, conn, perSecond, inside is null ? null : LabyrinthTrials.RoombaBuffRow(inside.Roomba.Type), "in a trial roomba");
                await Task.Delay(SpikeTickMilliseconds);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] {remote} trial roombas stopped: {ServerLog.Describe(ex)}"); }
        finally { lock (conn.Monsters) conn.TrapDegenPerSecond = 0; }
    }

    // A trap's degeneration on the player while they are in it (RunResourceRecovery deals ClientState.TrapDegenPerSecond)
    // and the trap's buff shown on them (its instance and row).
    sealed class TrapDegen
    {
        (ushort Instance, ushort Row)? shown;

        // This tick's degeneration (0 out of the trap) and buff row: the buff swapped as the row changes, and the client's
        // recovery rates sent again on entering and leaving (PlayerStatsUpdateAtGate).
        public async Task Set(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, double perSecond, ushort? row,
            string where)
        {
            bool was, degen;
            ushort instance = 0;
            lock (conn.Monsters)
            {
                was = conn.TrapDegenPerSecond > 0;
                conn.TrapDegenPerSecond = conn.PlayerLife == 0 ? 0 : perSecond;
                degen = conn.TrapDegenPerSecond > 0;
                if (!degen) row = null;
                if (row is not null && shown?.Row != row) instance = conn.NextBuff++;
            }
            if (shown is { } old && old.Row != row)
            {
                await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, old.Instance), null);
                shown = null;
            }
            if (row is ushort buffRow && shown is null)
            {
                await Send(remote, stream, ciphers, InstanceEntities.AddBuff(PlayerId, instance, buffRow), null);
                shown = (instance, buffRow);
            }
            if (degen && !was)
                Console.WriteLine($"[gateway] {remote} {where}: degeneration {conn.TrapDegenPerSecond:0}/s (life {conn.PlayerMaxLife}, " +
                    $"energy shield {conn.EnergyShield.Maximum}), life regeneration {LifeRegenPerSecond(conn):0.#}/s, life {conn.PlayerLife}, " +
                    $"energy shield {conn.EnergyShield.Current}");
            if (degen != was) await SendCharacterStats(remote, stream, ciphers, conn, degen ? where : "out of the trial's traps");
        }
    }

    // A Bronze Monograph reads at once (live 2026-10-31, the original: no "Read" option to choose, and the voice goes on
    // after the player walks away): S→C 0x84 with its only NPCTalk row's index (0, "Read") opens the talk on that row.
    internal static Task ReadTrialMonograph(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id,
        SpawnedNpc npc) =>
        Send(remote, stream, ciphers, InstanceNpcs.Talk(id, LabyrinthTrials.GlyphTalk), $"0x0084 read trial monograph 0x{id:x} {npc.Npc.Id}");

    // The trial object of this type nearest to a cell, closed or not in this instance.
    static (uint Id, AreaPopulation.RoomObject Object)? NearestTrialObject(ClientState conn, string type, (int X, int Y) at, bool closed)
    {
        lock (conn.Monsters)
            return conn.TrialObjects.Where(t => t.Value.Type == type && (!closed || !conn.Instance.Transitioned(type, t.Value.X, t.Value.Y)))
                .OrderBy(t => (long)(t.Value.X - at.X) * (t.Value.X - at.X) + (long)(t.Value.Y - at.Y) * (t.Value.Y - at.Y))
                .Select(t => ((uint, AreaPopulation.RoomObject)?)(t.Key, t.Value)).FirstOrDefault();
    }
}
