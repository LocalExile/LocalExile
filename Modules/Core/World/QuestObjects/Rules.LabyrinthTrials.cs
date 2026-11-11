using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;
using GameServer.Data.Terrain.Generation;

static partial class QuestobjectsRules
{
    // A Labyrinth room's glyph statue stands about 50 cells (|dx| + |dy|) from the exit it belongs to.
    const int MapStatueReach = 100;

    // Whether the six Normal trials are done (Labyrinths RequiredTrials; the character's flag or the league's).
    static bool NormalTrialsDone(ClientState conn, out int done, out int count) => LabyrinthTrialsDone(conn, 1, out done, out count);

    // The plaza's device (Act3Objects.LabyrinthSelect): the Labyrinth's choice of difficulty (0x7b kind 0x10,
    // LabyrinthSelectWindow); the choice comes back as C→S 0x212 (ChooseLabyrinth).
    internal static async Task UseLabyrinthSelect(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn) =>
        await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(InstanceWaypoints.LabyrinthSelectUi), "0x007b open the Labyrinth select window");

    // The Ascendancy Chamber's font (Act3Objects.AscendancyFont): the Labyrinth's two ascendancy points (once per tier; Izaro
    // is not fought yet, so reaching the font is enough), then 0x7b kind 0x0a: the client opens its class selector for an
    // unascended character (its choice comes back as C→S 0x39, ChooseAscendancyClass), else the passive tree.
    internal static async Task UseAscendancyFont(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (LabyrinthDay.DifficultyOf(conn.Layout.Id) is not int tier) return;
        try
        {
            int earned = await AwardAscendancyPoints(remote, stream, ciphers, conn, [global::AscendancyRules.AwardKeys[tier - 1]]);
            Console.WriteLine($"[gateway] {remote} the font: Labyrinth {tier}'s points, {earned} earned");
        }
        catch (Exception ex) when (ex is AscendancyRuleException or PassiveProgressConflictException)
        {
            Console.WriteLine($"[gateway] {remote} the font's points refused: {ex.Message}");
        }
        await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(InstanceWaypoints.AscendancySelectUi), "0x007b open the ascendancy class selector");
    }

    // The plaza's portal (Act3Objects.ReturnPortal, "Return To Town") and the Ascendancy Chamber's
    // (Act3Objects.ChamberPortal): to the character's last town, else Act 3's.
    internal static async Task UsePlazaReturnPortal(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        ushort town = conn.LastTown != 0 ? conn.LastTown : AreaTransitionCatalog.HashOf(Act3Areas.SarnEncampment) ?? 0;
        if (town != 0) await ChangeArea(remote, stream, ciphers, conn, new PendingTransfer(town, 0, false), "the plaza's portal back to town");
    }

    // The Sarn Encampment's Ascendancy plaque (Act3Objects.LabyrinthPlaque), once the player is at it: with the six Normal
    // trials done (the character's flag or the league's) the statue opens for good (Act3Objects.StatueOpened, which the
    // statue, the plaque and the way to the plaza follow); before, the trials done (the Trial Plaque window).
    internal static async Task UseSarnPlaque(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        bool all = NormalTrialsDone(conn, out int done, out int count);
        Console.WriteLine($"[gateway] {remote} Sarn plaque: {done} of {count} Normal trials done");
        if (all)
        {
            if (!conn.HasQuestFlag(Act3Objects.StatueOpened))
                await SetQuestFlags(remote, stream, ciphers, conn, [Act3Objects.StatueOpened, Act3Objects.LabyrinthAccess]);
            return;
        }
        await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(InstanceWaypoints.TrialPlaqueUi), "0x007b open the Trial Plaque window (Sarn plaque)");
    }

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
        int tilesFrom = ServerLevels.For(conn.Layout.Id, conn.Seed)?.RoomObjects.Count ?? all.Count;
        if (all.Count == 0 && !labyrinth) all = AreaPopulation.LoadObjects(conn.Layout.Id, conn.Seed, notes);
        var wiring = WireRoomObjects(conn, all, links);
        Wirings.AddOrUpdate(conn, wiring);
        SetSwapLevers(conn, all, links);
        await SpawnTrialTraps(remote, stream, ciphers, conn, [.. all.Where(o => LabyrinthTrials.IsSpikeTrap(o.Type))],
            [.. all.Where(o => o.Type == LabyrinthTrials.Lever).Select(o => (o.X, o.Y))],
            [.. all.Where(o => o.Type is LabyrinthTrials.Door or LabyrinthTrials.IntroDoor).Select(o => (o.X, o.Y))],
            [.. all.Where(o => LabyrinthTrials.Trigger(o.Type) is not null)], all, links);
        await SpawnArrowTraps(remote, stream, ciphers, conn, [.. all.Where(o => LabyrinthTrials.IsArrowTrap(o.Type))]);
        await SpawnEdgeBlades(remote, stream, ciphers, conn, [.. all.Take(tilesFrom).Where(o => LabyrinthTrials.IsEdgeBlade(o.Type))]);
        await SpawnTileTraps(remote, stream, ciphers, conn, [.. all.Skip(tilesFrom).Where(o => LabyrinthTrials.IsEdgeBlade(o.Type) || LabyrinthTrials.IsRoller(o.Type))]);
        // The Chamber of Sins and Crypt trials' track pieces: server objects the client shows.
        foreach (var rail in all.Where(o => o.Type == LabyrinthTrials.VisibleRail))
        {
            uint id = conn.NextObjectId++;
            await global::Game.SendWorldCreate(remote, stream, ciphers, conn, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(rail.Type),
                LabyrinthTrials.RailBlob(rail.X, rail.Y, rail.Angle, rail.Model)), null);
            await global::Game.SendWorldRecord(remote, stream, ciphers, conn, id, InstanceProtocol.EnterWakeZone(id), null);
        }
        await SpawnRoombas(remote, stream, ciphers, conn,
            [.. all.Where(o => o.Type is LabyrinthTrials.Roomba or LabyrinthTrials.FlyingRoomba or LabyrinthTrials.Spinner || LabyrinthTrials.IsSawblade(o.Type))],
            [.. all.Where(o => o.Type is LabyrinthTrials.Rail or LabyrinthTrials.VisibleRail || LabyrinthTrials.IsRailSwitch(o.Type))],
            [.. all.Where(o => o.Type == LabyrinthTrials.Switch).Select(o => (o.X, o.Y))],
            all.FirstOrDefault(o => o.Type == LabyrinthTrials.IntroDoor) is { } entrance ? (entrance.X, entrance.Y) : default,
            labyrinth ? LabyrinthScratches(conn) : null, all, links);
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
        // The rooms' stashes (the account's, as a town's: before Izaro's arenas and in the Ascendancy Chamber).
        if (labyrinth) await PlaceLabyrinthStashes(remote, stream, ciphers, conn, all);
        // Izaro's arena (Rules.Izaro.cs): his elevator, its doors and his trial.
        if (labyrinth) await RunIzaroArena(remote, stream, ciphers, conn, all);
        // The statues wake only in the Labyrinth's own rooms (a trial's tiles keep theirs as they were).
        if (!labyrinth) all = [.. all.Where(o => !LabyrinthTrials.IsStatueSpawner(o.Type))];
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
            if (open && LabyrinthTrials.GoesWhenOpened(o.Type, o.Model)) continue;   // an opened Locked Door is gone (RemoveOpenedDoor)
            uint id = conn.NextObjectId++;
            lock (conn.Monsters)
            {
                conn.TrialObjects[id] = o;
                wiring.Ids[index] = id;
            }
            // A disabled lever shows pulled (it cannot be used), a spent crank down for good.
            byte state = o.Type == LabyrinthTrials.RailSwitchSwapped ? LabyrinthTrials.SwapClientState(o.Mirrored, open)
                : LabyrinthTrials.IsStatueSpawner(o.Type) ? LabyrinthTrials.Open
                : wiring.Graph.IsSpent(index) ? LabyrinthTrials.Spent
                : open || wiring.Graph.IsDisabled(index) ? LabyrinthTrials.Open : LabyrinthTrials.Closed;
            await global::Game.SendWorldCreate(remote, stream, ciphers, conn, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(o.Type),
                    LabyrinthTrials.Blob(o.Type, o.Model, o.X, o.Y, o.Angle, o.Scale, state,
                        LabyrinthTrials.IsHotPlate(o.Type) || LabyrinthTrials.AtTileHeight(o.Type) ? o.Height : null)),
                $"0x02bb trial object 0x{id:x} {o.Type} at ({o.X}, {o.Y}), angle {o.Angle:0.###}, state {state}");
            await global::Game.SendWorldRecord(remote, stream, ciphers, conn, id, InstanceProtocol.EnterWakeZone(id), null);
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
        List<KeyValuePair<uint, AreaPopulation.RoomObject>> plates, statues;
        lock (conn.Monsters)
        {
            plates = [.. conn.TrialObjects.Where(t => LabyrinthTrials.IsHotPlate(t.Value.Type))];
            statues = [.. conn.TrialObjects.Where(t => LabyrinthTrials.IsStatueSpawner(t.Value.Type) && !conn.Instance.Transitioned(t.Value.Type, t.Value.X, t.Value.Y))];
        }
        if (statues.Count > 0)
        {
            Console.WriteLine($"[gateway] {remote} {what}: {statues.Count} statue(s) waiting to wake");
            _ = WakeStatues(remote, stream, ciphers, conn, statues);
        }
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
        // The tiles' own (the doorways' rollers, the abyss edges' blades) after the rooms', whose indexes the links are.
        return [.. level.RoomObjects.Select(o => new AreaPopulation.RoomObject(o.Type, o.Model, o.X, o.Y,
            LabyrinthTrials.PlacedAngle(o.Model, o.Rotation, o.Code), o.Scale, float.IsNaN(o.Z) ? null : o.Z, o.Code is >= 4 and < 8)),
            .. level.TileLabyrinthObjects.Select(o => new AreaPopulation.RoomObject(o.Type, o.Model, o.X, o.Y, o.Rotation, o.Scale,
                o.Z == 0 ? null : o.Z, o.Code is >= 4 and < 8))];
    }

    // A room area's wiring on this connection: its objects' links, and the ids its placed objects have.
    sealed class RoomWiring(LabyrinthWiring graph, List<AreaPopulation.RoomObject> all, List<PlacedLink> links)
    {
        public readonly LabyrinthWiring Graph = graph;
        public readonly List<AreaPopulation.RoomObject> All = all;
        public readonly List<PlacedLink> Links = links;
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
        var wiring = new RoomWiring(new LabyrinthWiring([.. all.Select(o => o.Type)], links), all, links);
        for (int i = 0; i < all.Count; i++)
            if (wiring.Graph.Wired(i) && conn.Instance.Transitioned(all[i].Type, all[i].X, all[i].Y)) wiring.Graph.Turn(i, true);
        for (int i = 0; i < all.Count; i++)
            if (LabyrinthWiring.IsCrank(all[i].Type) && wiring.Graph.IsOn(i) && !wiring.Graph.IsSpent(i))
                foreach (var (k, _) in wiring.Graph.Turn(i, false)) conn.Instance.SetTransitioned(all[k].Type, all[k].X, all[k].Y, false);
        return wiring;
    }

    // The connection's room wiring. A hot reload of this module starts Wirings over while the player stays in the room
    // (a lever then did nothing until the next area): it is built again from what the connection keeps, the room's
    // objects and links, what this instance turned on, and the ids its placed objects have. A crank still down comes
    // up here as on a new entry; the old module's timer brings it up on the client.
    static bool TryWiring(ClientState conn, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RoomWiring? wiring)
    {
        if (Wirings.TryGetValue(conn, out wiring)) return true;
        List<KeyValuePair<uint, AreaPopulation.RoomObject>> placed;
        lock (conn.Monsters) placed = [.. conn.TrialObjects];
        if (placed.Count == 0 || conn.Closed) return false;
        wiring = Wirings.GetValue(conn, _ =>
        {
            var links = new List<PlacedLink>();
            var all = LabyrinthRoomObjects(conn, [], links);
            // A trial the Gateway does not generate has room-points.json's objects, unlinked.
            if (all.Count == 0) all = [.. placed.OrderBy(p => p.Key).Select(p => p.Value)];
            var rebuilt = WireRoomObjects(conn, all, links);
            var taken = new HashSet<int>();
            foreach (var (id, o) in placed)
                for (int i = 0; i < all.Count; i++)
                    if (all[i] == o && taken.Add(i)) { rebuilt.Ids[i] = id; break; }
            Console.WriteLine($"[gateway] {conn.Layout.Id}: room wiring built again after a reload ({links.Count} link(s), {rebuilt.Ids.Count} of {placed.Count} placed object(s))");
            return rebuilt;
        });
        return true;
    }

    // The room objects one links to by these names (its indexes in the room's objects); none when it has no links.
    static List<int> LinkedFrom(RoomWiring wiring, AreaPopulation.RoomObject from, Func<string, bool> name) =>
        wiring.All.IndexOf(from) is int at and >= 0 ? [.. wiring.Links.Where(l => l.From == at && name(l.Name)).Select(l => l.To)] : [];

    // Whether the room links say what this object drives.
    static bool HasLinks(RoomWiring wiring, AreaPopulation.RoomObject from) => LinkedFrom(wiring, from, _ => true).Count > 0;

    // The placed objects of these room indexes.
    static List<(uint Id, AreaPopulation.RoomObject Object)> Placed(ClientState conn, RoomWiring wiring, IEnumerable<int> indexes)
    {
        lock (conn.Monsters)
            return [.. indexes.Where(i => wiring.Ids.TryGetValue(i, out uint id) && conn.TrialObjects.ContainsKey(id)).Select(i => (wiring.Ids[i], wiring.All[i]))];
    }

    // The sawblades the pulled levers stop by the room links (live 2026-11-01, the original: the Chamber of Sins trial's
    // as its links say): each pulled lever's pause, and on along each stopped sawblade's own pause.
    static HashSet<int> PausedSaws(ClientState conn, RoomWiring wiring)
    {
        var stopped = new HashSet<int>();
        var todo = new Stack<int>(wiring.Links.Where(l => l.Name == "pause" && wiring.All[l.From].Type is LabyrinthTrials.Switch
            && conn.Instance.Transitioned(wiring.All[l.From].Type, wiring.All[l.From].X, wiring.All[l.From].Y)).Select(l => l.To));
        while (todo.TryPop(out int saw))
            if (stopped.Add(saw))
                foreach (var l in wiring.Links.Where(l => l.Name == "pause" && l.From == saw)) todo.Push(l.To);
        return stopped;
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
            await global::Game.SendWorldRecord(remote, stream, ciphers, conn, at, InstanceEntities.TransitionableUpdate(at, LabyrinthTrials.TransitionableIndex(o.Type), state),
                $"0x02bc wired {o.Type.Split('/')[^1]} 0x{at:x} at ({o.X}, {o.Y}): {change}");
        }
    }

    // A Labyrinth spinner room's swap piece (Rail_Visible_Swap_Anims10) and the levers that turn it: each Switch whose
    // nearest rail switch it is, with the lever's gears (each with its nearest lever).
    // With the room links, a lever is the swap piece's when its next links reach the piece, through its gears.
    static List<(AreaPopulation.RoomObject Lever, List<AreaPopulation.RoomObject> Gears)> SwapLevers(
        IReadOnlyList<AreaPopulation.RoomObject> all, AreaPopulation.RoomObject swap, IReadOnlyList<PlacedLink>? links = null)
    {
        if (links is { Count: > 0 } && all.ToList().IndexOf(swap) is int piece and >= 0)
        {
            var found = new List<(AreaPopulation.RoomObject, List<AreaPopulation.RoomObject>)>();
            for (int lever = 0; lever < all.Count; lever++)
            {
                if (all[lever].Type != LabyrinthTrials.Switch) continue;
                var gears = new List<int>();
                bool reaches = false;
                var todo = new Stack<int>([lever]);
                var seen = new HashSet<int> { lever };
                while (todo.TryPop(out int at))
                    foreach (var l in links.Where(l => l.From == at && l.Name == "next"))
                    {
                        if (l.To == piece) reaches = true;
                        else if (all[l.To].Type == LabyrinthTrials.Gears && seen.Add(l.To)) { gears.Add(l.To); todo.Push(l.To); }
                    }
                if (reaches) found.Add((all[lever], [.. gears.Select(g => all[g])]));
            }
            if (found.Count > 0) return found;
        }
        static long Distance(AreaPopulation.RoomObject a, AreaPopulation.RoomObject b) => (long)(a.X - b.X) * (a.X - b.X) + (long)(a.Y - b.Y) * (a.Y - b.Y);
        var railSwitches = all.Where(o => LabyrinthTrials.IsRailSwitch(o.Type)).ToList();
        var levers = all.Where(o => o.Type is LabyrinthTrials.Switch or LabyrinthTrials.MultiSwitch or LabyrinthTrials.Lever).ToList();
        return [.. levers.Where(l => l.Type == LabyrinthTrials.Switch && railSwitches.MinBy(r => Distance(l, r)) == swap)
            .Select(l => (l, all.Where(g => g.Type == LabyrinthTrials.Gears && levers.MinBy(k => Distance(g, k)) == l).ToList()))];
    }

    // Each swap piece's levers as the piece stands in this instance (LabyrinthTrials.OnFirstArm): the one by the part of
    // the track the spinner does not run is pulled, its gears turning.
    static void SetSwapLevers(ClientState conn, IReadOnlyList<AreaPopulation.RoomObject> all, IReadOnlyList<PlacedLink> links)
    {
        foreach (var swap in all.Where(o => o.Type == LabyrinthTrials.RailSwitchSwapped))
        {
            bool other = conn.Instance.Transitioned(swap.Type, swap.X, swap.Y);
            foreach (var (lever, gears) in SwapLevers(all, swap, links))
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
        await global::Game.SendWorldRecord(remote, stream, ciphers, conn, swapId, InstanceEntities.TransitionableUpdate(swapId, LabyrinthTrials.TransitionableIndex(swap.Type),
            LabyrinthTrials.SwapClientState(swap.Mirrored, other)), $"0x02bc swap piece 0x{swapId:x} at ({swap.X}, {swap.Y}) to its {(other ? "other" : "first")} arm");
        List<KeyValuePair<uint, AreaPopulation.RoomObject>> placed;
        lock (conn.Monsters) placed = [.. conn.TrialObjects];
        var levers = TryWiring(conn, out var wiring) ? SwapLevers(wiring.All, swap, wiring.Links) : SwapLevers([.. placed.Select(p => p.Value)], swap);
        foreach (var (l, gears) in levers)
        {
            bool pulled = LabyrinthTrials.OnFirstArm((l.X, l.Y), (swap.X, swap.Y), swap.Angle, swap.Mirrored) == other;
            foreach (var o in gears.Prepend(l))
            {
                if (conn.Instance.Transitioned(o.Type, o.X, o.Y) == pulled) continue;
                conn.Instance.SetTransitioned(o.Type, o.X, o.Y, pulled);
                if (placed.FirstOrDefault(p => p.Value == o) is not { Value: not null } at) continue;
                uint id = at.Key;
                await global::Game.SendWorldRecord(remote, stream, ciphers, conn, id, InstanceEntities.TransitionableUpdate(id, LabyrinthTrials.TransitionableIndex(o.Type),
                    pulled ? LabyrinthTrials.Open : LabyrinthTrials.Closed), $"0x02bc swap {o.Type.Split('/')[^1]} 0x{id:x} {(pulled ? "on" : "off")}");
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
                        await global::Game.SendWorldRecord(remote, stream, ciphers, conn, id, InstanceEntities.TransitionableUpdate(id, LabyrinthTrials.TransitionableIndex(plate.Type), LabyrinthTrials.Open), null);
                    (spills, cools) = (next + LabyrinthTrials.LavaSpillSeconds, next + LabyrinthTrials.LavaHeatSeconds + LabyrinthTrials.LavaBurnSeconds);
                    drains = cools + LabyrinthTrials.LavaDrainSeconds;
                    next = cools + LabyrinthTrials.LavaCoolSeconds + LabyrinthTrials.LavaRestSeconds;
                }
                if (now >= cools)
                {
                    foreach (var (id, plate) in plates)
                        await global::Game.SendWorldRecord(remote, stream, ciphers, conn, id, InstanceEntities.TransitionableUpdate(id, LabyrinthTrials.TransitionableIndex(plate.Type), LabyrinthTrials.Closed), null);
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
        if (!use.Started)   // a lockstep client got its start from the frame pass
            await global::Game.SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, o.X, o.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
                $"0x01ab player uses trial object 0x{use.TargetId:x} {o.Type} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (conn.ActionCount != action || conn.Closed) return true;
        if (!GameServer.Core.World.PlayerWalk.InWalkUpRange(stop, (o.X, o.Y), size))
            Console.WriteLine($"[gateway] {remote} trial object 0x{use.TargetId:x} out of reach from ({stop.X}, {stop.Y})");
        else if (IzaroStatues.IsStatue(o.Type))
            await UseIzaroStatue(remote, stream, ciphers, conn, use.TargetId);
        else if ((o.Type is LabyrinthTrials.Lever or LabyrinthWiring.OnceMultiSwitch || LabyrinthWiring.IsCrank(o.Type))
                 && TryWiring(conn, out var wiring) && wiring.IndexOf(use.TargetId) is int wired && wiring.Graph.Wired(wired))
            await PullWired(remote, stream, ciphers, conn, wiring, wired);
        else switch (o.Type)
        {
            case IzaroFights.Gate:
                await OpenIzaroGate(remote, stream, ciphers, conn, use.TargetId, o);
                break;
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
            // A smashable wall breaks at a hit or a click (a cast in reach too: BreakSmashableWalls); a hidden switch
            // turns, and its secret wall opens once both switches on that side of it have (no link names them: a switch
            // is its nearest wall's; live 2026-11-01, the original: two on each side, either pair opens it).
            case LabyrinthTrials.SmashableWall:
                await OpenTrialObject(remote, stream, ciphers, conn, use.TargetId, o);
                break;
            case LabyrinthTrials.HiddenSwitch:
                if (!await OpenTrialObject(remote, stream, ciphers, conn, use.TargetId, o)) break;
                if (NearestTrialObject(conn, LabyrinthTrials.HiddenWall, (o.X, o.Y), closed: true) is not { } wall) break;
                List<AreaPopulation.RoomObject> switches, walls;
                lock (conn.Monsters)
                {
                    switches = [.. conn.TrialObjects.Values.Where(t => t.Type == LabyrinthTrials.HiddenSwitch)];
                    walls = [.. conn.TrialObjects.Values.Where(t => t.Type == LabyrinthTrials.HiddenWall)];
                }
                static long Distance(AreaPopulation.RoomObject a, AreaPopulation.RoomObject b) => (long)(a.X - b.X) * (a.X - b.X) + (long)(a.Y - b.Y) * (a.Y - b.Y);
                // Its side of the wall: across the wall's line (its angle's width, as a door's), the way through it.
                var w0 = wall.Object;
                int Side(AreaPopulation.RoomObject s) => Math.Sign((s.Y - w0.Y) * Math.Cos(w0.Angle) - (s.X - w0.X) * Math.Sin(w0.Angle));
                var own = switches.Where(s => walls.MinBy(w => Distance(s, w)) == w0 && Side(s) == Side(o)).ToList();
                Console.WriteLine($"[gateway] {remote} hidden switch at ({o.X}, {o.Y}): {own.Count(s => conn.Instance.Transitioned(s.Type, s.X, s.Y))} of {own.Count} " +
                    $"on its side pulled for the secret wall at ({w0.X}, {w0.Y})");
                if (own.All(s => conn.Instance.Transitioned(s.Type, s.X, s.Y)))
                    await OpenTrialObject(remote, stream, ciphers, conn, wall.Id, wall.Object);
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
                        await global::Game.SendWorldRecord(remote, stream, ciphers, conn, id, InstanceEntities.TransitionableUpdate(id, LabyrinthTrials.TransitionableIndex(portal.Type), LabyrinthTrials.Open),
                            $"0x02bc trial portal 0x{id:x} opens: trial {trial.Flag} plaque used");
                // Every use shows the trials done (the window reads their flags itself).
                await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(InstanceWaypoints.TrialPlaqueUi), "0x007b open the Trial Plaque window");
                break;
            case LabyrinthTrials.Map:
            {
                // The statue stands by one of the room's exits: the room behind it is shown, the way to it in green.
                var exit = conn.Layout.AreaTransitions.Select(t => t.Transition).Where(t => t.Entrance is not null)
                    .MinBy(t => (t.GridX - o.X) * (t.GridX - o.X) + (t.GridY - o.Y) * (t.GridY - o.Y));
                var shown = exit is { Entrance: int number } && Math.Abs(exit.GridX - o.X) + Math.Abs(exit.GridY - o.Y) <= MapStatueReach
                    ? LabyrinthDay.Reveal(CharacterName(conn), conn.Layout.Id, conn.Seed, number) : null;
                if (shown is not null)
                    await Send(remote, stream, ciphers, shown.Progress, $"0x01cb labyrinth progress: the map shows entrance{exit!.Entrance}'s room (row {shown.Row})");
                await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(InstanceWaypoints.LabyrinthMapUi, (uint)(shown?.Row ?? 0)),
                    $"0x007b open the Labyrinth map{(shown is null ? "" : $", the way to row {shown.Row} in green")}");
                break;
            }
            case LabyrinthTrials.Portal:
                if (!conn.Instance.Transitioned(o.Type, o.X, o.Y)) break;   // closed until the plaque is used
                if (NearestTrialObject(conn, LabyrinthTrials.IntroDoor, (o.X, o.Y), closed: false) is not { } entrance) break;
                var to = conn.Layout.Near((entrance.Object.X, entrance.Object.Y));
                lock (conn.Monsters)
                {
                    Interlocked.Increment(ref conn.ActionCount);
                    conn.Walk.Teleport(to.X, to.Y, conn.Clock.Elapsed.TotalSeconds);
                }
                await global::Game.SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.ActionEnded(PlayerId), null);
                await global::Game.SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.Teleport(PlayerId, to.X, to.Y),
                    $"0x01b7 trial portal: back to the trial's door at ({to.X}, {to.Y})");
                return true;
        }
        await global::Game.SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.ActionEnded(PlayerId), null);
        return true;
    }

    // Transitions a trial object once in this instance: its Transitionable steps to the open state (the client plays the
    // .ot's on_transition), a door also unblocks (0x1c6) and leaves the server's walk estimate. False when already open.
    static async Task<bool> OpenTrialObject(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id,
        AreaPopulation.RoomObject o)
    {
        if (!conn.Instance.Transition(o.Type, o.X, o.Y)) return false;
        await global::Game.SendWorldRecord(remote, stream, ciphers, conn, id, InstanceEntities.TransitionableUpdate(id, LabyrinthTrials.TransitionableIndex(o.Type), LabyrinthTrials.Open),
            $"0x02bc trial object 0x{id:x} {o.Type} at ({o.X}, {o.Y}) opens");
        if (!LabyrinthTrials.IsDoor(o.Type)) return true;
        await global::Game.SendWorldRecord(remote, stream, ciphers, conn, id, InstanceEntities.BlockageUpdate(id, false), $"0x01c6 trial door 0x{id:x} unblocked");
        lock (conn.Monsters)
        {
            conn.DoorBarriers.Remove(id);
            conn.UpdateBarrier();
        }
        if (LabyrinthTrials.GoesWhenOpened(o.Type, o.Model)) _ = RemoveOpenedDoor(remote, stream, ciphers, conn, id);
        return true;
    }

    // The Labyrinth's statues (LabyrinthTrials.IsStatueSpawner): the spawner stands transitioned (its stone hidden), and its
    // monster (LabyrinthTrials.StatueOf, normal, at the area's level) waits on the pedestal in its Emerge clip's first frame
    // (0x1b2 at speed 0), turned as the statue, out of the AI. It wakes as the player comes within
    // LabyrinthTrials.StatueWakeCells or hits it: its Emerge (the .act's wake clip: stepping down), busy until it has
    // played. The instance keeps the statue spent once its monster stands (the monster is kept with the others).
    static async Task WakeStatues(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        List<KeyValuePair<uint, AreaPopulation.RoomObject>> statues)
    {
        byte level = (byte)Math.Clamp(ServerLevels.AreaLevel(conn.Layout.Id), 1, 100);
        var waiting = new List<(uint Mob, AreaPopulation.RoomObject Statue, MonsterCatalog.Variety Monster)>();
        try
        {
            foreach (var (id, statue) in statues)
            {
                if (!conn.Instance.Transition(statue.Type, statue.X, statue.Y)) continue;
                await global::Game.SendWorldRecord(remote, stream, ciphers, conn, id, InstanceEntities.BlockageUpdate(id, false), null);
                if (Monsters.Find(LabyrinthTrials.StatueOf(statue.Type)) is not { Supported: true } monster)
                {
                    Console.WriteLine($"[gateway] {remote} statue at ({statue.X}, {statue.Y}): {LabyrinthTrials.StatueOf(statue.Type)} cannot be created");
                    continue;
                }
                uint mob = await SpawnMonster(remote, stream, ciphers, conn, monster, level, MonsterRarity.Normal, statue.X, statue.Y, log: false);
                lock (conn.Monsters)
                    if (conn.Monsters.TryGetValue(mob, out var m)) (m.BusyUntil, m.Aggro) = (double.PositiveInfinity, false);
                await global::Game.SendWorldRecord(remote, stream, ciphers, conn, mob, InstanceProtocol.Orientation(mob, statue.Angle), null);
                if (Emerges.ClipIndex(monster) is uint clip) await global::Game.SendWorldRecord(remote, stream, ciphers, conn, mob, InstanceProtocol.AnimationUpdate(mob, 0, clip, 0), null);
                waiting.Add((mob, statue, monster));
            }
            Console.WriteLine($"[gateway] {remote} statues: {waiting.Count} monster(s) waiting on their pedestals");
            while (!conn.Closed && waiting.Count > 0)
            {
                double now = conn.Clock.Elapsed.TotalSeconds;
                (int X, int Y) here;
                lock (conn.Monsters) here = conn.Walk.Position(now);
                double reach = LabyrinthTrials.StatueWakeCells * LabyrinthTrials.StatueWakeCells;
                foreach (var w in waiting.ToList())
                {
                    bool hit, gone;
                    lock (conn.Monsters)
                    {
                        gone = !conn.Monsters.TryGetValue(w.Mob, out var m) || m.Dead;
                        hit = !gone && m!.Life < m.MaxLife;
                    }
                    bool near = (w.Statue.X - here.X) * (double)(w.Statue.X - here.X) + (w.Statue.Y - here.Y) * (double)(w.Statue.Y - here.Y) <= reach;
                    if (!gone && !near && !hit) continue;
                    waiting.Remove(w);
                    if (gone) continue;
                    lock (conn.Monsters)
                        if (conn.Monsters.TryGetValue(w.Mob, out var woken)) (woken.BusyUntil, woken.Aggro) = (now + Emerges.Seconds(w.Monster) + 0.3, true);
                    if (Emerges.SkillId is ushort emerge)
                        await Send(remote, stream, ciphers, [.. InstanceProtocol.ActionStarted(w.Mob, w.Statue.X, w.Statue.Y, w.Statue.X, w.Statue.Y, emerge, 0xffff, conn.MonsterOrderFlags),
                            .. InstanceProtocol.ActionEnded(w.Mob)], $"0x01ab statue monster 0x{w.Mob:x} {w.Monster.Name} at ({w.Statue.X}, {w.Statue.Y}) steps down");
                }
                await Task.Delay(200);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] {remote} statues stopped: {ServerLog.Describe(ex)}"); }
    }

    // The smashable walls within reach of a cast the player makes break (LabyrinthTrials.SmashReach: the cast stands for its
    // hit, Smashable's transition_on_damage_taken).
    internal static async Task BreakSmashableWalls(ClientState conn, (int X, int Y) from)
    {
        List<KeyValuePair<uint, AreaPopulation.RoomObject>> walls;
        lock (conn.Monsters)
            walls = [.. conn.TrialObjects.Where(t => t.Value.Type == LabyrinthTrials.SmashableWall
                && (long)(t.Value.X - from.X) * (t.Value.X - from.X) + (long)(t.Value.Y - from.Y) * (t.Value.Y - from.Y) <= LabyrinthTrials.SmashReach * LabyrinthTrials.SmashReach)];
        if (walls.Count == 0) return;
        var (remote, stream, ciphers) = conn.Wire;
        try { foreach (var (id, wall) in walls) await OpenTrialObject(remote, stream, ciphers, conn, id, wall); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] {remote} smashable wall not broken: {ServerLog.Describe(ex)}"); }
    }

    // An opened Locked Door's opening lifts the bars out of the way, so once it has played the door goes (0x2bd), and a
    // later entry does not create an opened one. (Its cells once stayed closed after 0x1c6, live 2026-10-31, while its
    // Positioned had a static position: LabyrinthTrials.Blob.)
    static async Task RemoveOpenedDoor(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id)
    {
        await Task.Delay(TimeSpan.FromSeconds(DoorOpeningSeconds));
        if (conn.Closed) return;
        lock (conn.Monsters) conn.TrialObjects.Remove(id);
        await global::Game.SendWorldRemoval(remote, stream, ciphers, conn, id, $"0x02bd opened trial door 0x{id:x} removed");
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
        // With the room links, what the lever drives is theirs (its next, transitionN and pause links); without, the nearest.
        var wiring = TryWiring(conn, out var w) && HasLinks(w, o) ? w : null;
        List<KeyValuePair<uint, AreaPopulation.RoomObject>> railSwitches;
        lock (conn.Monsters) railSwitches = [.. conn.TrialObjects.Where(t => LabyrinthTrials.IsRailSwitch(t.Value.Type))];
        if (wiring is not null && o.Type == LabyrinthTrials.Switch)
        {
            if (wiring.All.FirstOrDefault(s => s.Type == LabyrinthTrials.RailSwitchSwapped && SwapLevers(wiring.All, s, wiring.Links).Any(l => l.Lever == o)) is { } swap
                && railSwitches.FirstOrDefault(r => r.Value == swap) is { Value: not null } placedSwap)
            {
                await TurnSwap(remote, stream, ciphers, conn, o, placedSwap.Key, placedSwap.Value);
                return;
            }
        }
        else if (o.Type == LabyrinthTrials.Switch && railSwitches.Count > 0
            && railSwitches.MinBy(r => (long)(r.Value.X - o.X) * (r.Value.X - o.X) + (long)(r.Value.Y - o.Y) * (r.Value.Y - o.Y)) is var nearest
            && nearest.Value.Type == LabyrinthTrials.RailSwitchSwapped)
        {
            await TurnSwap(remote, stream, ciphers, conn, o, nearest.Key, nearest.Value);
            return;
        }
        bool pulled = !conn.Instance.Transitioned(o.Type, o.X, o.Y);
        conn.Instance.SetTransitioned(o.Type, o.X, o.Y, pulled);
        byte state = pulled ? LabyrinthTrials.Open : LabyrinthTrials.Closed;
        await global::Game.SendWorldRecord(remote, stream, ciphers, conn, id, InstanceEntities.TransitionableUpdate(id, LabyrinthTrials.TransitionableIndex(o.Type), state),
            $"0x02bc trial switch 0x{id:x} at ({o.X}, {o.Y}) {(pulled ? "pulled" : "pulled back")}");
        // Its cages (Door_Open) go the other way, each as it stands (several levers may share one: the Labyrinth's Prison
        // bottleneck_01); its Locked Doors (Door_Closed, a Switch_Multi's transitionN) open for good.
        var linked = wiring is null ? [] : Placed(conn, wiring, LinkedFrom(wiring, o, n => n == "next" || n.StartsWith("transition", StringComparison.Ordinal)));
        // Its gears (TimerGears) turn, and what their next links lead to is the lever's too (1_Labyrinth_RT_branch_bottleneck_2:
        // Switch → TimerGears → Door_Closed; live 2026-11-08, the door did not open without it).
        if (wiring is not null && linked.Where(t => t.Object.Type == LabyrinthTrials.Gears).ToList() is { Count: > 0 } gears)
        {
            if (pulled)
                foreach (var (gearId, gear) in gears) await OpenTrialObject(remote, stream, ciphers, conn, gearId, gear);
            linked = [.. linked, .. Placed(conn, wiring, gears.SelectMany(g => LinkedFrom(wiring, g.Object, n => n == "next")))];
        }
        var cages = wiring is not null
            ? linked.Where(t => t.Object.Type == LabyrinthTrials.OpenDoor).ToList()
            : NearestTrialObject(conn, LabyrinthTrials.OpenDoor, (o.X, o.Y), closed: false) is { } nearestCage ? [nearestCage] : [];
        foreach (var cage in cages)
        {
            bool shut = wiring is null ? pulled : !conn.Instance.Transitioned(cage.Object.Type, cage.Object.X, cage.Object.Y);
            conn.Instance.SetTransitioned(cage.Object.Type, cage.Object.X, cage.Object.Y, shut);
            await global::Game.SendWorldRecord(remote, stream, ciphers, conn, cage.Id, InstanceEntities.TransitionableUpdate(cage.Id, LabyrinthTrials.TransitionableIndex(cage.Object.Type),
                    shut ? LabyrinthTrials.Open : LabyrinthTrials.Closed),
                $"0x02bc trial cage 0x{cage.Id:x} at ({cage.Object.X}, {cage.Object.Y}) {(shut ? "shuts" : "opens")}");
            BarCage(conn, cage.Id, cage.Object, shut);
            await global::Game.SendWorldRecord(remote, stream, ciphers, conn, cage.Id, InstanceEntities.BlockageUpdate(cage.Id, shut), null);
        }
        foreach (var (doorId, door) in linked.Where(t => t.Object.Type == LabyrinthTrials.Door))
            await OpenTrialObject(remote, stream, ciphers, conn, doorId, door);
        List<KeyValuePair<uint, AreaPopulation.RoomObject>> rails, levers;
        lock (conn.Monsters)
        {
            rails = [.. conn.TrialObjects.Where(t => LabyrinthTrials.IsRailSwitch(t.Value.Type))];
            levers = [.. conn.TrialObjects.Where(t => t.Value.Type is LabyrinthTrials.Switch or LabyrinthTrials.MultiSwitch)];
        }
        var moved = wiring is not null
            ? Placed(conn, wiring, LinkedFrom(wiring, o, n => n == "next" || n.StartsWith("transition", StringComparison.Ordinal)))
                .Where(t => t.Object.Type == LabyrinthTrials.RailSwitch).ToList()
            : [.. LabyrinthTrials.SwitchedRails([.. rails.Select(r => (r.Value.X, r.Value.Y))], [.. levers.Select(l => (l.Value.X, l.Value.Y))],
                levers.FindIndex(l => l.Key == id)).Select(i => (rails[i].Key, rails[i].Value))];
        foreach (var (railId, rail) in moved)
        {
            conn.Instance.SetTransitioned(rail.Type, rail.X, rail.Y, pulled);
            await global::Game.SendWorldRecord(remote, stream, ciphers, conn, railId, InstanceEntities.TransitionableUpdate(railId, LabyrinthTrials.TransitionableIndex(rail.Type), state),
                $"0x02bc trial rail switch 0x{railId:x} at ({rail.X}, {rail.Y}) to state {state}");
        }
        if (TrapRuns.TryGetValue(conn, out var linkedRuns) && wiring is not null && wiring.Links.Any(l => l.Name == "pause"))
        {
            var paused = PausedSaws(conn, wiring);
            foreach (var r in linkedRuns.Where(r => LabyrinthTrials.IsSawblade(r.Roomba.Type))) r.Halted = paused.Contains(wiring.All.IndexOf(r.Roomba));
            Console.WriteLine($"[gateway] {remote} trial switch at ({o.X}, {o.Y}) {(pulled ? "pulled" : "pulled back")}: sawblades stopped now " +
                string.Join(", ", paused.Select(i => $"({wiring.All[i].X}, {wiring.All[i].Y})")));
        }
        // Without the room links, the sawblades nearest the lever stop (the links say otherwise where a room has them).
        else if (wiring is null && TrapRuns.TryGetValue(conn, out var runs))
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
    // is in the area (RunSpikeTraps). Their chains and triggers are the room links' (LabyrinthTrials.LinkedSpikes); without
    // links (room-points.json's objects) the traps a room's Trigger_* line takes fire on its timer
    // (LabyrinthTrials.TriggeredSpikes), the others as the trials' chains (LabyrinthTrials.SpikeChains).
    static async Task SpawnTrialTraps(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        List<AreaPopulation.RoomObject> traps, List<(int X, int Y)> levers, List<(int X, int Y)> doors, List<AreaPopulation.RoomObject> triggers,
        List<AreaPopulation.RoomObject> all, List<PlacedLink> links)
    {
        if (traps.Count == 0) return;
        // A step fires a pressure spike trap: it is in no chain.
        var pressure = traps.Where(t => LabyrinthTrials.IsPressureSpikeTrap(t.Type)).ToList();
        traps = [.. traps.Except(pressure)];
        var chains = LabyrinthTrials.SpikeChains([.. traps.Select(t => (t.X, t.Y))], levers, doors);
        var triggered = LabyrinthTrials.TriggeredSpikes([.. traps.Select(t => (t.X, t.Y, t.Angle))], [.. triggers.Select(t => (t.X, t.Y))]);
        // The room links' chains, when the traps have them: a room index to the trap's or trigger's own.
        var trapAt = new Dictionary<int, int>();
        var triggerAt = new Dictionary<int, int>();
        for (int i = 0; i < traps.Count; i++) trapAt.TryAdd(all.IndexOf(traps[i]), i);
        for (int i = 0; i < triggers.Count; i++) triggerAt.TryAdd(all.IndexOf(triggers[i]), i);
        var trapLinks = links.Where(l => l.Name == "next" && trapAt.ContainsKey(l.From) && trapAt.ContainsKey(l.To)).Select(l => (trapAt[l.From], trapAt[l.To])).ToList();
        if (trapLinks.Count > 0)
        {
            var linked = LabyrinthTrials.LinkedSpikes([.. traps.Select(t => (t.X, t.Y))], trapLinks,
                [.. links.Where(l => l.Name == "next" && triggerAt.ContainsKey(l.From) && trapAt.ContainsKey(l.To)).Select(l => (triggerAt[l.From], trapAt[l.To]))],
                [.. triggered.Select(t => t.Trigger)]);
            chains = [.. linked.Select(c => (c.Chain, c.Order, c.Checker))];
            triggered = [.. linked.Select(c => (c.Trigger, c.Order))];
        }
        // Triggered chains numbered past the others (a linked chain's number is below the count of traps).
        int firstTriggered = trapLinks.Count > 0 ? traps.Count : chains.Length == 0 ? 0 : chains.Max(c => c.Chain) + 1;
        var placed = new List<SpikeTrap>();
        for (int i = 0; i < traps.Count; i++)
        {
            uint id = conn.NextObjectId++;
            await global::Game.SendWorldCreate(remote, stream, ciphers, conn, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(traps[i].Type),
                LabyrinthTrials.SpikeTrapBlob(traps[i].X, traps[i].Y)), null);
            await global::Game.SendWorldRecord(remote, stream, ciphers, conn, id, InstanceProtocol.EnterWakeZone(id), null);
            placed.Add(triggered[i].Trigger is int t and >= 0
                ? new(id, traps[i], firstTriggered + t, triggered[i].Order, false) { Trigger = LabyrinthTrials.Trigger(triggers[t].Type) }
                : new(id, traps[i], chains[i].Chain, chains[i].Order, chains[i].Checker));
        }
        foreach (var trap in pressure)
        {
            uint id = conn.NextObjectId++;
            await global::Game.SendWorldCreate(remote, stream, ciphers, conn, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(trap.Type),
                LabyrinthTrials.SpikeTrapBlob(trap.X, trap.Y, trap.Type)), null);
            await global::Game.SendWorldRecord(remote, stream, ciphers, conn, id, InstanceProtocol.EnterWakeZone(id), null);
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
            await global::Game.SendWorldRecord(remote, stream, ciphers, conn, t.Id, InstanceProtocol.ActorAnimation(t.Id, (float)LabyrinthTrials.SpikeAnimationSeconds, LabyrinthTrials.EmergeAnimation),
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
            await global::Game.SendWorldCreate(remote, stream, ciphers, conn, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(blade.Type),
                LabyrinthTrials.EdgeBladeBlob(blade.X, blade.Y, blade.Angle)), null);
            await global::Game.SendWorldRecord(remote, stream, ciphers, conn, id, InstanceProtocol.EnterWakeZone(id), null);
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
                        await global::Game.SendWorldRecord(remote, stream, ciphers, conn, id, InstanceProtocol.ActorAnimation(id, (float)LabyrinthTrials.EdgeBladeClipSeconds, LabyrinthTrials.EmergeAnimation), null);
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

    // The Labyrinth's tiles' traps (LabyrinthTrials.TileTrapRestSeconds hidden between two goes, each its own while
    // after the start by its place): the doorway rollers and the abyss edges' blades. While out they close their strip
    // (0x1c6 and the server's walk estimate, as a spike trap's square, not under a player in it).
    // The dropping roller (idle_top, down, idle_bottom, up by 0x1b2) throws a player under it out as it lands and cuts
    // whoever is in or by its strip while down, as a roomba (ClientState.TrapDegenPerSecond, the roomba's buff and its
    // blood). The rising spikes (LabyrinthTrials.RisingSpikes) and the blades hit a player in the strip once a strike, from
    // the strike to the next (the last's until they are gone), with bleed, each throwing them a little back.
    static async Task SpawnTileTraps(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        List<AreaPopulation.RoomObject> traps)
    {
        if (traps.Count == 0) return;
        var placed = new List<TileTrap>();
        foreach (var trap in traps)
        {
            uint id = conn.NextObjectId++;
            bool blade = LabyrinthTrials.IsEdgeBlade(trap.Type);
            await global::Game.SendWorldCreate(remote, stream, ciphers, conn, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(trap.Type), blade
                ? LabyrinthTrials.EdgeBladeBlob(trap.X, trap.Y, trap.Angle)
                : LabyrinthTrials.SpikeTrapBlob(trap.X, trap.Y, angle: trap.Angle, height: trap.Height)), null);
            await global::Game.SendWorldRecord(remote, stream, ciphers, conn, id, InstanceProtocol.EnterWakeZone(id), null);
            var (across, along) = blade ? (LabyrinthTrials.EdgeBladeHalfAcross, LabyrinthTrials.EdgeBladeHalfAlong) : LabyrinthTrials.RollerHalf(trap.Type);
            var (wallAcross, wallAlong) = blade ? (across, along) : LabyrinthTrials.RollerWallHalf(trap.Type);
            placed.Add(new TileTrap(id, trap, across, [.. LabyrinthTrials.HullCells(trap.X, trap.Y, trap.Angle, across, along)],
                [.. LabyrinthTrials.HullCells(trap.X, trap.Y, trap.Angle, wallAcross, wallAlong)]));
        }
        Console.WriteLine($"[gateway] {remote} tile traps: {placed.Count(p => LabyrinthTrials.IsRoller(p.Trap.Type))} doorway roller(s), " +
            $"{placed.Count(p => !LabyrinthTrials.IsRoller(p.Trap.Type))} abyss edge blade(s)");
        _ = RunTileTraps(remote, stream, ciphers, conn, placed);
    }

    // Its strip (where it hits) and its wall (the cells it closes while out: the same, but _02's narrower).
    sealed record TileTrap(uint Id, AreaPopulation.RoomObject Trap, double Across, HashSet<(int X, int Y)> Strip, HashSet<(int X, int Y)> Wall)
    {
        public double Start = double.PositiveInfinity;
        // Whether it is out, and whether its strip's cells are closed on the server's walk estimate.
        public bool Blocked, Barred;
        public int Struck = -1, Played = -1;
    }

    static async Task RunTileTraps(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, List<TileTrap> traps)
    {
        var degen = new TrapDegen();
        double first = conn.Clock.Elapsed.TotalSeconds + 1;
        foreach (var t in traps) t.Start = first + (t.Trap.X * 31 + t.Trap.Y * 17) % 50 / 10.0;
        try
        {
            while (!conn.Closed)
            {
                double now = conn.Clock.Elapsed.TotalSeconds;
                (int X, int Y) here;
                lock (conn.Monsters) here = conn.Walk.Position(now);
                bool cut = false;
                foreach (var t in traps)
                {
                    if (now < t.Start) continue;
                    double at = now - t.Start;
                    string type = t.Trap.Type;
                    if (LabyrinthTrials.IsDroppingRoller(type))
                    {
                        double hold = LabyrinthTrials.RollerDownSeconds + LabyrinthTrials.RollerHoldSeconds, end = hold + LabyrinthTrials.RollerUpSeconds;
                        // Its clips: down, then idle_bottom, then up, then back to idle_top.
                        int phase = at < LabyrinthTrials.RollerDownSeconds ? 0 : at < hold ? 1 : at < end ? 2 : 3;
                        if (phase != t.Played)
                        {
                            t.Played = phase;
                            uint clip = phase switch { 0 => LabyrinthTrials.RollerDownClip, 1 => LabyrinthTrials.RollerBottomClip, 2 => LabyrinthTrials.RollerUpClip, _ => LabyrinthTrials.RollerTopClip };
                            await global::Game.SendWorldRecord(remote, stream, ciphers, conn, t.Id, InstanceProtocol.AnimationUpdate(t.Id, 0, clip, 1), null);
                        }
                        bool down = at >= LabyrinthTrials.RollerLandsSeconds && at < hold + LabyrinthTrials.RollerLiftsSeconds;
                        await BlockTileTrap(remote, stream, ciphers, conn, t, down, throwOut: true);
                        if (down && NearStrip(t, here)) cut = true;
                        if (phase == 3) (t.Start, t.Played) = (now + LabyrinthTrials.TileTrapRestSeconds, -1);
                        continue;
                    }
                    bool blade = LabyrinthTrials.IsEdgeBlade(type);
                    // A blade swings with its Emerge (blade.ao's hit, its sounds); the spikes go up, stay out and go back
                    // down by their own clips (0x1b2), whose sounds those are.
                    double[] strikes;
                    double open, done;
                    if (blade)
                    {
                        (strikes, open, done) = ([LabyrinthTrials.EdgeBladeImpactSeconds], LabyrinthTrials.EdgeBladeClipSeconds - 0.3, LabyrinthTrials.EdgeBladeClipSeconds);
                        if (t.Played < 0)
                        {
                            t.Played = 0;
                            await global::Game.SendWorldRecord(remote, stream, ciphers, conn, t.Id, InstanceProtocol.ActorAnimation(t.Id, (float)LabyrinthTrials.EdgeBladeClipSeconds, LabyrinthTrials.EmergeAnimation), null);
                        }
                    }
                    else
                    {
                        var go = LabyrinthTrials.RisingSpikes(type);
                        // Closed until they have gone back down (live 2026-11-01: they let the player through while still out).
                        (strikes, open, done) = (go.Strikes, go.Up + go.Out + go.Down - 0.2, go.Up + go.Out + go.Down);
                        int phase = at < go.Up ? 0 : at < go.Up + go.Out ? 1 : at < done ? 2 : 3;
                        if (phase != t.Played)
                        {
                            t.Played = phase;
                            uint clip = phase switch { 0 => LabyrinthTrials.SpikesUpClip, 1 => LabyrinthTrials.SpikesOutClip, 2 => LabyrinthTrials.SpikesDownClip, _ => LabyrinthTrials.SpikesRestClip };
                            await global::Game.SendWorldRecord(remote, stream, ciphers, conn, t.Id, InstanceProtocol.AnimationUpdate(t.Id, 0, clip, 1), null);
                        }
                    }
                    // A strike first: a player in the strip is hit, the last strike throwing them back out.
                    int strike = at < open ? Array.FindLastIndex(strikes, s => at >= s) : -1;
                    if (strike > t.Struck && t.Strip.Contains(here))
                    {
                        t.Struck = strike;
                        await TileTrapHit(remote, stream, ciphers, conn, t, blade, now);
                    }
                    await BlockTileTrap(remote, stream, ciphers, conn, t, at >= strikes[0] && at < open, throwOut: false);
                    if (at >= done) (t.Start, t.Played, t.Struck) = (now + LabyrinthTrials.TileTrapRestSeconds, -1, -1);
                }
                double perSecond = 0;
                if (cut)
                {
                    var (lifeShare, shieldShare) = LabyrinthTrials.RoombaDamagePerSecond;
                    lock (conn.Monsters) perSecond = conn.PlayerMaxLife * lifeShare + conn.EnergyShield.Maximum * shieldShare;
                }
                await degen.Set(remote, stream, ciphers, conn, perSecond, LabyrinthTrials.RoombaBuffRow(LabyrinthTrials.Roomba), "under a doorway roller");
                await Task.Delay(SpikeTickMilliseconds);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] {remote} tile traps stopped: {ServerLog.Describe(ex)}"); }
    }

    // Whether the player is in a dropping roller's strip or within two cells of it.
    static bool NearStrip(TileTrap t, (int X, int Y) at)
    {
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                if (t.Strip.Contains((at.X + dx, at.Y + dy))) return true;
        return false;
    }

    // A tile trap's strip closes (the client's TriggerableBlockage, the server's walk estimate) or opens. A player in it
    // as it closes is thrown back out first (throwOut: the dropping roller), or keeps the cells under them open, as a spike
    // trap's square does (the collision escape would throw them off).
    // Called every tick: the cells left open under a player close once they are out of the strip.
    static async Task BlockTileTrap(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, TileTrap t, bool blocked,
        bool throwOut)
    {
        bool changed = t.Blocked != blocked;
        t.Blocked = blocked;
        bool inside;
        lock (conn.Monsters) inside = t.Wall.Contains(conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds));
        if (changed && blocked && inside && throwOut) await Knockback(remote, stream, ciphers, conn, t, outOfStrip: true);
        lock (conn.Monsters)
        {
            inside = t.Wall.Contains(conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds));
            bool bar = blocked && !inside;
            if (bar != t.Barred)
            {
                t.Barred = bar;
                if (!bar) conn.DoorBarriers.Remove(t.Id);
                else if (conn.Layout.Walkable is { } walkable) conn.DoorBarriers[t.Id] = t.Wall.Where(c => walkable(c.X, c.Y)).ToHashSet();
                conn.UpdateBarrier();
            }
        }
        if (changed) await global::Game.SendWorldRecord(remote, stream, ciphers, conn, t.Id, InstanceEntities.BlockageUpdate(t.Id, blocked), null);
    }

    // A rising roller's or a blade's strike on the player in its strip: a spike's hit (a blade's its own), with the bleed,
    // and thrown a little back.
    static async Task TileTrapHit(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, TileTrap t, bool blade, double now)
    {
        uint life;
        int damage;
        lock (conn.Monsters)
        {
            if ((blade ? LabyrinthTrials.EdgeBladeDamage(conn.PlayerMaxLife, conn.EnergyShield.Maximum)
                    : LabyrinthTrials.SpikeDamage(conn.PlayerMaxLife, conn.EnergyShield.Maximum)) is not int dealt
                || TrapHitLocked(conn, dealt, now) is not uint left) return;
            (damage, life) = (dealt, left);
        }
        await Knockback(remote, stream, ciphers, conn, t, outOfStrip: false);
        if (!await PresentTrapHit(remote, stream, ciphers, conn, t.Id, $"{(blade ? "abyss edge blade" : "doorway spikes")} 0x{t.Id:x} at ({t.Trap.X}, {t.Trap.Y})", damage, life))
            return;
        if (blade) await SpikeBleed(remote, stream, ciphers, conn, damage, LabyrinthTrials.EdgeBladeBleedPerSecond, LabyrinthTrials.EdgeBladeBleedSeconds);
        else await SpikeBleed(remote, stream, ciphers, conn, damage, LabyrinthTrials.SpikeBleedPerSecond, LabyrinthTrials.SpikeBleedSeconds);
    }

    // Throws the player across a tile trap's strip, back the way they stand from its middle: a hit's
    // LabyrinthTrials.KnockbackCells, or (outOfStrip) out of it, LabyrinthTrials.ThrowOutCells beyond; onto a walkable cell,
    // the other way if not; nowhere to go, they stay.
    static async Task Knockback(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, TileTrap t, bool outOfStrip)
    {
        (int X, int Y) to;
        lock (conn.Monsters)
        {
            var at = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            double cos = Math.Cos(t.Trap.Angle), sin = Math.Sin(t.Trap.Angle);
            double off = (at.X - t.Trap.X) * cos + (at.Y - t.Trap.Y) * sin;
            (int X, int Y)? found = null;
            foreach (int side in off >= 0 ? new[] { 1, -1 } : [-1, 1])
            {
                double push = outOfStrip ? side * (t.Across + LabyrinthTrials.ThrowOutCells) - off : side * LabyrinthTrials.KnockbackCells;
                var cell = ((int)Math.Round(at.X + push * cos), (int)Math.Round(at.Y + push * sin));
                if (conn.Layout.Walkable is { } walk && walk(cell.Item1, cell.Item2) && (!outOfStrip || !t.Strip.Contains(cell))) { found = cell; break; }
            }
            if (found is not { } cellTo) return;
            to = cellTo;
            Interlocked.Increment(ref conn.ActionCount);
            conn.Walk.Teleport(to.X, to.Y, conn.Clock.Elapsed.TotalSeconds);
        }
        await global::Game.SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.ActionEnded(PlayerId), null);
        await global::Game.SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.Teleport(PlayerId, to.X, to.Y), $"0x01b7 thrown back from tile trap 0x{t.Id:x} to ({to.X}, {to.Y})");
    }

    // A pressure spike trap's click: spiketrap_trigger.ao, whose default clip plays the Trigger sound, as an effect object
    // on the trap for as long as it plays.
    static async Task PressureClick(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, AreaPopulation.RoomObject trap)
    {
        uint id = conn.NextObjectId++;
        await global::Game.SendWorldCreate(remote, stream, ciphers, conn, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
            new EffectObject(trap.X, trap.Y, LabyrinthTrials.PressureSpikeClick).Blob()), null);
        await global::Game.SendWorldRecord(remote, stream, ciphers, conn, id, InstanceProtocol.EnterWakeZone(id), null);
        await Task.Delay(TimeSpan.FromSeconds(LabyrinthTrials.PressureSpikeDelaySeconds));
        if (!conn.Closed) await global::Game.SendWorldRemoval(remote, stream, ciphers, conn, id, null);
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
        await global::Game.SendWorldRecord(remote, stream, ciphers, conn, t.Id, InstanceEntities.BlockageUpdate(t.Id, blocked), null);
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
    internal static uint? TrapHitLocked(ClientState conn, int damage, double now)
    {
        if (conn.PlayerLife == 0 || HardcoreProgressBlocked(conn)) return null;
        long before = conn.PlayerLife, lifeDamage = DamagePlayerHitTyped(conn, damage, 0, 0, now, CharacterPlayerStats(conn),
            GameServer.Core.World.DamagePacket.Of(GameServer.Core.World.DamageType.Physical, damage, damage));
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
        await global::Game.SendWorldRecord(remote, stream, ciphers, conn, id, InstanceProtocol.ActionHit(id, 0xffff, PlayerId, flags: MonsterHitFlags(false, false),
            kind: 0x03, code: HitCode(taken)), null);
        await SendPlayerEnergyShield(remote, stream, ciphers, conn);
        if (life == 0)
            await global::Game.SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.StatsUpdate(PlayerId, [(StatKeys.HideMiniLifeBar, 1)]),
                "0x01b3 player hide_mini_life_bar = 1");
        await global::Game.SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.LifeUpdate(PlayerId, life, conn.PlayerReservedLife),
            $"0x01b1 player hit by {trap} for {damage}, life {life}/{conn.PlayerMaxLife}{(life == 0 ? " (dies)" : "")}");
        await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 player mana after {trap}");
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

    // virtual_unaffected_by_bleeding, as the stats hold it now (Leech.WithLeechingDefences).
    static bool UnaffectedByBleeding(ClientState conn) =>
        Stats.Key("virtual_unaffected_by_bleeding") is uint key && CharacterPlayerStats(conn).GetValueOrDefault(key) != 0;

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
            await global::Game.SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.AddBuff(PlayerId, buff, row,
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
                    double factor = conn.Walk.Moving(now) ? LabyrinthTrials.SpikeBleedMovingFactor : 1;
                    dealt = (int)Math.Round(bleed.PerSecond * factor * SpikeBleedTickSeconds);
                    // Unaffected by Bleeding (Endless Hunger while leeching): the bleed stays on and deals nothing.
                    if (UnaffectedByBleeding(conn)) dealt = 0;
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
                        _ = global::Game.SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.RemoveBuff(PlayerId, old), null);
                        _ = global::Game.SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.AddBuff(PlayerId, shown, LabyrinthTrials.BleedingBuffRow.Value,
                            u68: BitConverter.SingleToUInt32Bits((float)(bleed.Until - now))), null);
                    }
                }
                await SendPlayerEnergyShield(remote, stream, ciphers, conn);
                await global::Game.SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.LifeUpdate(PlayerId, life, conn.PlayerReservedLife),
                    life == 0 ? $"0x01b1 player bleeds {dealt} from a trial spike trap and dies" : null);
                if (over) break;
            }
            if (!conn.Closed && LabyrinthTrials.BleedingBuffRow is not null)
                await global::Game.SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.RemoveBuff(PlayerId, shown), null);
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
            await global::Game.SendWorldCreate(remote, stream, ciphers, conn, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(trap.Type),
                LabyrinthTrials.ArrowTrapBlob(trap.X, trap.Y, trap.Angle, trap.Model, trap.Type)), null);
            await global::Game.SendWorldRecord(remote, stream, ciphers, conn, id, InstanceProtocol.EnterWakeZone(id), null);
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
            await global::Game.SendWorldRecord(remote, stream, ciphers, conn, id, InstanceProtocol.ActorAnimation(id, 1, LabyrinthTrials.CastAnimation), null);
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
                            await global::Game.SendWorldRecord(remote, stream, ciphers, conn, id, InstanceEntities.TransitionableUpdate(id, LabyrinthTrials.TransitionableIndex(plate.Type), LabyrinthTrials.Closed),
                                $"0x02bc pressure plate 0x{id:x} at ({plate.X}, {plate.Y}) comes up");
                        }
                        bool again = plate.Type.EndsWith("Multi_4", StringComparison.Ordinal);
                        if (!down && on && (left || again))
                        {
                            (down, at, left) = (true, now, false);
                            int room = RoomOf(plate.X, plate.Y);
                            var fired = TryWiring(conn, out var wiring) && LinkedDarts(wiring, plate) is { Count: > 0 } linked
                                ? triggered.Where(t => linked.Contains(wiring.All.IndexOf(t.Trap))).ToList()
                                : triggered.Where(t => RoomOf(t.Trap.X, t.Trap.Y) == room).ToList();
                            await global::Game.SendWorldRecord(remote, stream, ciphers, conn, id, InstanceEntities.TransitionableUpdate(id, LabyrinthTrials.TransitionableIndex(plate.Type), LabyrinthTrials.Open),
                                $"0x02bc pressure plate 0x{id:x} at ({plate.X}, {plate.Y}) pressed: {fired.Count} wall dart(s) fire");
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

    // The darts a pressure plate fires by the room links: its next (a ResetMulti_4 plate's transitionN), and on along each
    // dart's next, delay and delayed (var_3_1_trap: the plate's dart, then the other three in a row). All fire at once
    // (our reading: the links' delay has no time of its own in the data). None without links.
    static HashSet<int> LinkedDarts(RoomWiring wiring, AreaPopulation.RoomObject plate)
    {
        var darts = new HashSet<int>();
        var todo = new Stack<int>(LinkedFrom(wiring, plate, n => n == "next" || n.StartsWith("transition", StringComparison.Ordinal)));
        while (todo.TryPop(out int at))
            if (wiring.All[at].Type == LabyrinthTrials.TriggeredArrowTrap && darts.Add(at))
                foreach (int to in LinkedFrom(wiring, wiring.All[at], n => n is "next" or "delay" or "delayed")) todo.Push(to);
        return darts;
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
            await global::Game.SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.AddBuff(PlayerId, buff, row,
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
                    await global::Game.SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.LifeUpdate(PlayerId, life, conn.PlayerReservedLife),
                        died ? $"0x01b1 player takes {dealt} poison damage from a trial dart trap and dies" : null);
                if (LabyrinthTrials.PoisonBuffRow is not null)
                    foreach (ushort old in ended) await global::Game.SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.RemoveBuff(PlayerId, old), null);
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
    // trial's door). With the room links, the rails join as they link (next, previous, teleport), and a roomba with a
    // reset rail goes its links' way from there (LabyrinthTrials.LinkedPath).
    static async Task SpawnRoombas(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        List<AreaPopulation.RoomObject> roombas, List<AreaPopulation.RoomObject> rails, List<(int X, int Y)> switches, (int X, int Y) entrance,
        List<(int X, int Y)>? scratches, List<AreaPopulation.RoomObject> all, List<PlacedLink> links)
    {
        if (roombas.Count == 0) return;
        // A visible rail is a track piece, joined along its angle only; the Labyrinth's rooms' runs follow their scratches.
        var invisible = rails.Where(r => r.Type == LabyrinthTrials.Rail).ToList();
        var railAt = new Dictionary<int, int>();
        for (int i = 0; i < rails.Count; i++) railAt.TryAdd(all.IndexOf(rails[i]), i);
        var railLinks = links.Where(l => l.Name is "next" or "previous" or "teleport" && railAt.ContainsKey(l.From) && railAt.ContainsKey(l.To))
            .Select(l => (From: railAt[l.From], To: railAt[l.To], Jump: l.Name == "teleport")).ToList();
        var paths = LabyrinthTrials.RoombaPaths([.. rails.Select(r => (r.X, r.Y))], [.. roombas.Select(r => (r.X, r.Y, r.Angle))],
            [.. rails.Select(r => r.Type == LabyrinthTrials.VisibleRail ? r.Angle : float.NaN)],
            joins: railLinks.Count == 0 ? null : [.. railLinks.Select(l => (l.From, l.To))],
            jumps: [.. railLinks.Where(l => l.Jump).Select(l => (l.From, l.To))]);
        if (railLinks.Count > 0)
            for (int i = 0; i < roombas.Count; i++)
                if (links.FirstOrDefault(l => l.Name == "reset" && l.From == all.IndexOf(roombas[i]) && railAt.ContainsKey(l.To)) is { } reset)
                    paths[i] = LabyrinthTrials.LinkedPath([.. rails.Select(r => (r.X, r.Y))], [.. railLinks.Where(l => l.Jump || links.Any(k =>
                        k.Name == "next" && railAt.GetValueOrDefault(k.From, -1) == l.From && railAt.GetValueOrDefault(k.To, -1) == l.To))], railAt[reset.To]);
        if (railLinks.Count == 0 && scratches is not null && invisible.Count > 0)
        {
            var scratched = LabyrinthTrials.RoombaPaths([.. invisible.Select(r => (r.X, r.Y))], [.. roombas.Select(r => (r.X, r.Y, r.Angle))],
                null, scratches, [.. roombas.Select(r => r.Type == LabyrinthTrials.FlyingRoomba)]);
            for (int i = 0; i < roombas.Count; i++)
                if (roombas[i].Type is LabyrinthTrials.Roomba or LabyrinthTrials.FlyingRoomba) paths[i] = scratched[i];
        }
        var saws = Enumerable.Range(0, roombas.Count).Where(i => LabyrinthTrials.IsSawblade(roombas[i].Type)).ToList();
        var halted = TryWiring(conn, out var wiring) && links.Any(l => l.Name == "pause")
            ? PausedSaws(conn, wiring).Select(i => roombas.IndexOf(all[i])).Where(i => i >= 0).ToHashSet()
            : Enumerable.Range(0, switches.Count)
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
            await global::Game.SendWorldCreate(remote, stream, ciphers, conn, id, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(r.Type), spinner
                ? LabyrinthTrials.SpinnerBlob(r.X, r.Y, r.Angle, r.Model)
                : LabyrinthTrials.RoombaBlob(r.Type, r.X, r.Y, r.Angle, r.Model, stopped ? LabyrinthTrials.Open : LabyrinthTrials.Closed)), null);
            await global::Game.SendWorldRecord(remote, stream, ciphers, conn, id, InstanceProtocol.EnterWakeZone(id), null);
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
            Target = path.FromFirst ? 0
                : (Enumerable.Range(0, legs).MinBy(i => Distance((roomba.X, roomba.Y), rails[i], rails[(i + 1) % rails.Count])) + 1) % rails.Count;
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
            if (Path!.Ring) { Target = Target + 1 < Path.Rails.Count ? Target + 1 : Path.Loop; return; }
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
                                $"0x02bc trial sawblade 0x{r.Id:x} stops at ({cell.X}, {cell.Y})");
                            continue;
                        }
                        uint previous = r.Id;
                        lock (conn.Monsters) r.Id = conn.NextObjectId++;
                        await global::Game.SendWorldRemoval(remote, stream, ciphers, conn, previous, null);
                        await global::Game.SendWorldCreate(remote, stream, ciphers, conn, r.Id, InstanceEntities.CreateObject(r.Id, InstanceEntities.ObjectTypeHash(r.Roomba.Type),
                            LabyrinthTrials.RoombaBlob(r.Roomba.Type, cell.X, cell.Y, r.Roomba.Angle, r.Roomba.Model)),
                            $"0x02bb trial sawblade 0x{r.Id:x} (was 0x{previous:x}) runs on from ({cell.X}, {cell.Y})");
                        await global::Game.SendWorldRecord(remote, stream, ciphers, conn, r.Id, InstanceProtocol.EnterWakeZone(r.Id), null);
                        (r.From, r.Stopped, r.LegEnd) = (at, false, double.NegativeInfinity);
                    }
                    if (r.Stopped || now < r.LegEnd + LabyrinthTrials.RoombaPauseSeconds(r.Roomba.Type)) continue;
                    if (double.IsFinite(r.LegEnd)) { r.From = r.To; r.Next(); }
                    // A teleport leg (the room links'): there at once.
                    if (r.Path is { } jumping && jumping.Jumps.Contains((((int)r.From.X, (int)r.From.Y), ((int)r.To.X, (int)r.To.Y))))
                    {
                        (r.From, r.LegStart, r.LegEnd) = (r.To, now, now);
                        await global::Game.SendWorldRecord(remote, stream, ciphers, conn, r.Id, InstanceProtocol.Teleport(r.Id, (int)r.To.X, (int)r.To.Y),
                            $"0x01b7 trial roomba 0x{r.Id:x} jumps to ({r.To.X}, {r.To.Y})");
                        continue;
                    }
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
        // This trap runner's share of ClientState.TrapDegenPerSecond: the runners (hot plates, roombas, doorway rollers)
        // add theirs up, so one out of its traps does not cancel another's.
        double mine;

        // This tick's degeneration (0 out of the trap) and buff row: the buff swapped as the row changes, and the client's
        // recovery rates sent again on entering and leaving (PlayerStatsUpdateAtGate).
        public async Task Set(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, double perSecond, ushort? row,
            string where)
        {
            bool was, degen;
            ushort instance = 0;
            lock (conn.Monsters)
            {
                was = mine > 0;
                if (conn.PlayerLife == 0) perSecond = 0;
                conn.TrapDegenPerSecond = Math.Max(0, conn.TrapDegenPerSecond - mine + perSecond);
                mine = perSecond;
                degen = mine > 0;
                if (!degen) row = null;
                if (row is not null && shown?.Row != row) instance = conn.NextBuff++;
            }
            if (shown is { } old && old.Row != row)
            {
                await global::Game.SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.RemoveBuff(PlayerId, old.Instance), null);
                shown = null;
            }
            if (row is ushort buffRow && shown is null)
            {
                await global::Game.SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.AddBuff(PlayerId, instance, buffRow), null);
                shown = (instance, buffRow);
            }
            if (degen && !was && CharacterPlayerStatsCore(conn, out double lifeRegen) is not null)
                Console.WriteLine($"[gateway] {remote} {where}: degeneration {conn.TrapDegenPerSecond:0}/s (life {conn.PlayerMaxLife}, " +
                    $"energy shield {conn.EnergyShield.Maximum}), life regeneration {lifeRegen:0.#}/s, life {conn.PlayerLife}, " +
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
