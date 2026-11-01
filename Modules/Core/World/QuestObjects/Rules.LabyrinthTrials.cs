using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;

static partial class QuestobjectsRules
{
    // A Trial of Ascendancy's room objects where the room fit places them (LabyrinthTrials, Agents/docs/game/
    // labyrinth-trials.md): the trial's door, its levers and locked doors, the plaque and the portal back. What this
    // instance transitioned stays so (InstanceState.Transition); the plaque and the portal are lit once the character
    // has the trial's flag. A closed door closes its line across the way between the area's spawn and the plaque on the
    // server's walk estimate (QuestBarriers), as the client's TriggerableBlockage does on its side.
    internal static async Task SpawnTrialObjects(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (LabyrinthTrials.For(conn.Layout.Id) is not { } trial) return;
        var notes = new List<string>();
        var all = AreaPopulation.LoadObjects(conn.Layout.Id, conn.Seed, notes);
        await SpawnTrialTraps(remote, stream, ciphers, conn, [.. all.Where(o => o.Type == LabyrinthTrials.SpikeTrap)],
            [.. all.Where(o => o.Type == LabyrinthTrials.Lever).Select(o => (o.X, o.Y))],
            [.. all.Where(o => o.Type is LabyrinthTrials.Door or LabyrinthTrials.IntroDoor).Select(o => (o.X, o.Y))]);
        // The Bronze Monograph at the trial's door: an NPCs.dat row on StoryGlyphBase, read like any NPC (its NPCTalk
        // "Read" row), with the room line's StoryGlyph.ao.
        var glyphs = all.Where(o => o.Type.StartsWith(LabyrinthTrials.GlyphPrefix, StringComparison.Ordinal)).ToList();
        Console.WriteLine($"[gateway] {remote} trial {trial.Flag}: {glyphs.Count} monograph(s) in its rooms");
        foreach (var glyph in glyphs)
        {
            if (Npcs.Value.Place(new NpcCatalog.Placement(glyph.Type, glyph.X, glyph.Y, glyph.Angle, glyph.Model)) is { Supported: true } npc)
                await SpawnNpc(remote, stream, ciphers, conn, npc, glyph.X, glyph.Y, glyph.Angle);
            else Console.WriteLine($"[gateway] {remote} trial monograph {glyph.Type}: unknown or unsupported NPC");
        }
        var objects = all.Where(o => LabyrinthTrials.Handles(o.Type)).ToList();
        if (objects.Count == 0)
        {
            Console.WriteLine($"[gateway] {remote} trial {trial.Flag}: no trial objects in {conn.Layout.Id} seed {conn.Seed} ({string.Join("; ", notes)})");
            return;
        }
        var plaque = objects.FirstOrDefault(o => o.Type == LabyrinthTrials.Plaque);
        bool lit = ClientHasQuestFlag(conn, trial.Flag) || plaque is not null && conn.Instance.Transitioned(plaque.Type, plaque.X, plaque.Y);
        var from = conn.Layout.Near((conn.Layout.SpawnX, conn.Layout.SpawnY));
        List<(int X, int Y)> guarded = plaque is null ? [] : [conn.Layout.Near((plaque.X, plaque.Y))];
        foreach (var o in objects)
        {
            bool open = o.Type is LabyrinthTrials.Plaque or LabyrinthTrials.Portal ? lit : conn.Instance.Transitioned(o.Type, o.X, o.Y);
            if (open && o.Type == LabyrinthTrials.Door) continue;   // an opened Locked Door is gone (RemoveOpenedDoor)
            uint id = conn.NextObjectId++;
            lock (conn.Monsters) conn.TrialObjects[id] = o;
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(o.Type),
                    LabyrinthTrials.Blob(o.Type, o.Model, o.X, o.Y, o.Angle, o.Scale, open ? LabyrinthTrials.Open : LabyrinthTrials.Closed)),
                $"0x02b9 trial object 0x{id:x} {o.Type} at ({o.X}, {o.Y}), angle {o.Angle:0.###}, {(open ? "open" : "closed")}");
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
            if (open || o.Type is not (LabyrinthTrials.IntroDoor or LabyrinthTrials.Door)) continue;
            var cells = conn.Layout.Walkable is { } walkable
                ? QuestBarriers.Cells(walkable, conn.Layout.GridW, conn.Layout.GridH, (o.X, o.Y), o.Angle, from, guarded)
                : [];
            Console.WriteLine($"[gateway] {remote} trial door 0x{id:x}: {cells.Count} walkable cell(s) closed on the server between " +
                $"({from.X}, {from.Y}) and {string.Join(", ", guarded)}" + (cells.Count == 0 ? "" : $", from {cells.Min()} to {cells.Max()}"));
            lock (conn.Monsters)
            {
                conn.DoorBarriers[id] = cells;
                conn.UpdateBarrier();
            }
        }
    }

    // A trial object used: the player walks up to it, then the trial's door opens, a lever opens the nearest locked door,
    // the plaque completes the trial (its flag) and opens the portal, and the open portal returns to the trial's door.
    // False when the object is not a trial's.
    internal static async Task<bool> UseTrialObject(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        InstanceTransfer.UseSkillOnObject use)
    {
        AreaPopulation.RoomObject? o;
        lock (conn.Monsters) o = conn.TrialObjects.GetValueOrDefault(use.TargetId);
        if (o is null || LabyrinthTrials.For(conn.Layout.Id) is not { } trial) return false;
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
        else switch (o.Type)
        {
            case LabyrinthTrials.IntroDoor:
                await OpenTrialObject(remote, stream, ciphers, conn, use.TargetId, o);
                break;
            case LabyrinthTrials.Lever:
                if (!await OpenTrialObject(remote, stream, ciphers, conn, use.TargetId, o)) break;
                if (NearestTrialObject(conn, LabyrinthTrials.Door, (o.X, o.Y), closed: true) is { } door)
                    await OpenTrialObject(remote, stream, ciphers, conn, door.Id, door.Object);
                break;
            case LabyrinthTrials.Plaque:
                if (await OpenTrialObject(remote, stream, ciphers, conn, use.TargetId, o))
                {
                    await SetQuestFlags(remote, stream, ciphers, conn, [trial.Flag]);
                    List<KeyValuePair<uint, AreaPopulation.RoomObject>> portals;
                    lock (conn.Monsters) portals = [.. conn.TrialObjects.Where(t => t.Value.Type == LabyrinthTrials.Portal)];
                    foreach (var (id, portal) in portals)
                        await Send(remote, stream, ciphers, InstanceEntities.TransitionableUpdate(id, LabyrinthTrials.TransitionableIndex(portal.Type), LabyrinthTrials.Open),
                            $"0x02ba trial portal 0x{id:x} opens: trial {trial.Flag} complete");
                }
                // Every use shows the trials done (the window reads their flags itself).
                await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(InstanceWaypoints.TrialPlaqueUi), "0x007b open the Trial Plaque window");
                break;
            case LabyrinthTrials.Portal:
                if (!TrialLit(conn, trial)) break;
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
        if (o.Type is not (LabyrinthTrials.IntroDoor or LabyrinthTrials.Door)) return true;
        await Send(remote, stream, ciphers, InstanceEntities.BlockageUpdate(id, false), $"0x01c6 trial door 0x{id:x} unblocked");
        lock (conn.Monsters)
        {
            conn.DoorBarriers.Remove(id);
            conn.UpdateBarrier();
        }
        if (o.Type == LabyrinthTrials.Door) _ = RemoveOpenedDoor(remote, stream, ciphers, conn, id);
        return true;
    }

    // Live (2026-10-31), an opened Locked Door kept its cells closed to the client's walking (a movement skill still
    // crossed) after 0x1c6, and also when created again open and unblocked: the client's object pass (0x141d88690)
    // stamps some types' Hull whatever their TriggerableBlockage says. Its opening lifts the bars out of the way, so once
    // it has played the door goes (0x2bb), and a later entry does not create an opened one.
    static async Task RemoveOpenedDoor(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id)
    {
        await Task.Delay(TimeSpan.FromSeconds(DoorOpeningSeconds));
        if (conn.Closed) return;
        lock (conn.Monsters) conn.TrialObjects.Remove(id);
        await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb opened trial door 0x{id:x} removed");
    }

    // How long a Locked Door's opening plays before it goes (our own number: the animation's length is not read).
    const double DoorOpeningSeconds = 1.5;

    // A trial's spike traps (LabyrinthTrials.SpikeTrap): created at rest, then fired in waves for as long as the player
    // is in the area (RunSpikeTraps).
    static async Task SpawnTrialTraps(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        List<AreaPopulation.RoomObject> traps, List<(int X, int Y)> levers, List<(int X, int Y)> doors)
    {
        if (traps.Count == 0) return;
        var chains = LabyrinthTrials.SpikeChains([.. traps.Select(t => (t.X, t.Y))], levers, doors);
        var placed = new List<SpikeTrap>();
        for (int i = 0; i < traps.Count; i++)
        {
            uint id = conn.NextObjectId++;
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(traps[i].Type),
                LabyrinthTrials.SpikeTrapBlob(traps[i].X, traps[i].Y)), null);
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
            placed.Add(new(id, traps[i], chains[i].Chain, chains[i].Order, chains[i].Checker));
        }
        Console.WriteLine($"[gateway] {remote} trial spike traps: {placed.Count} placed in {chains.Max(c => c.Chain) + 1} chain(s)");
        _ = RunSpikeTraps(remote, stream, ciphers, conn, placed);
    }

    sealed record SpikeTrap(uint Id, AreaPopulation.RoomObject Trap, int Chain, int Order, bool Checker)
    {
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
        var chains = traps.GroupBy(t => t.Chain).ToDictionary(g => g.Key, g => g.ToList());
        double start = conn.Clock.Elapsed.TotalSeconds + 1;
        foreach (var chain in chains.Values) due.Enqueue((chain[0], Wave), start);
        async Task Fire(SpikeTrap t, double at)
        {
            (t.Fired, t.HasHit) = (at, false);
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
                            double step = LabyrinthTrials.SpikeStep(t.Checker);
                            foreach (var c in chain) due.Enqueue((c, Fired), at + c.Order * step);
                            due.Enqueue((t, Wave), at + LabyrinthTrials.SpikeWaveSeconds(chain.Max(c => c.Order) + 1, step));
                            break;
                        case Fired: await Fire(t, at); break;
                        case Block: await BlockSpikeTrap(remote, stream, ciphers, conn, t, true); break;
                        case Unblock: await BlockSpikeTrap(remote, stream, ciphers, conn, t, false); break;
                    }
                }
                (int X, int Y) here;
                lock (conn.Monsters) here = conn.Walk.Position(now);
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
            if (conn.PlayerLife == 0 || HardcoreProgressBlocked(conn)) return;
            double now = conn.Clock.Elapsed.TotalSeconds;
            if (!spike.Holds(conn.Walk.Position(now))
                || LabyrinthTrials.SpikeDamage(conn.PlayerMaxLife, conn.EnergyShield.Maximum) is not int dealt) return;
            damage = dealt;
            long before = conn.PlayerLife, lifeDamage = DamagePlayerHit(conn, damage, 0, now, CharacterPlayerStats(conn));
            conn.PlayerLife = (uint)Math.Max(0, before - lifeDamage);
            if (conn.PlayerLife == 0) { conn.InvalidateIncomingHits(); ClearAntiquarianOnDeath(conn); }
            RecordHardcoreDeath(conn);
            life = conn.PlayerLife;
        }
        var taken = GameServer.Core.World.DamagePacket.Empty.Add(GameServer.Core.World.DamageType.Physical, new(damage, damage));
        await Send(remote, stream, ciphers, InstanceProtocol.ActionHit(id, 0xffff, PlayerId, flags: MonsterHitFlags(false, false),
            kind: 0x03, code: HitCode(taken)), null);
        await SendPlayerEnergyShield(remote, stream, ciphers, conn);
        if (life == 0)
            await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, [(StatKeys.HideMiniLifeBar, 1)]),
                "0x01b3 player hide_mini_life_bar = 1");
        await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, life),
            $"0x01b1 player hit by trial spike trap 0x{id:x} at ({trap.X}, {trap.Y}) for {damage}, life {life}/{conn.PlayerMaxLife}{(life == 0 ? " (dies)" : "")}");
        if (life == 0) await PlayerDied(remote, stream, ciphers, conn, $"0x0055 death screen ({conn.League ?? "?"} league)");
        else await SpikeBleed(remote, stream, ciphers, conn, damage);
    }

    // The bleed a spike hit leaves on a connection: its damage a second while standing, and until when (Clock seconds).
    sealed class Bleed { public double PerSecond, Until; public ushort Buff; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ClientState, Bleed> Bleeds = new();

    // A spike hit bleeds (LabyrinthTrials.SpikeBleedPerSecond of its damage a second, three times that while the player
    // moves, for SpikeBleedSeconds); only the strongest bleed runs, so a weaker one while another lasts is dropped. Its
    // damage goes to energy shield first, then life, in SpikeBleedTickSeconds ticks, off the traps' loop.
    static async Task SpikeBleed(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, int hit)
    {
        var bleed = Bleeds.GetValue(conn, _ => new());
        double perSecond = hit * LabyrinthTrials.SpikeBleedPerSecond, until;
        ushort buff;
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            if (now < bleed.Until && bleed.PerSecond > perSecond) return;
            bool running = now < bleed.Until;
            (bleed.PerSecond, bleed.Until) = (perSecond, until = now + LabyrinthTrials.SpikeBleedSeconds);
            buff = bleed.Buff = conn.NextBuff++;
            if (running) return;   // the running ticks take the new bleed on
        }
        if (LabyrinthTrials.BleedingBuffRow is ushort row)
            await Send(remote, stream, ciphers, InstanceEntities.AddBuff(PlayerId, buff, row,
                u68: BitConverter.SingleToUInt32Bits((float)LabyrinthTrials.SpikeBleedSeconds)), null);
        _ = Task.Run(async () =>
        {
            ushort shown = buff;
            while (!conn.Closed)
            {
                await Task.Delay(TimeSpan.FromSeconds(SpikeBleedTickSeconds));
                uint life;
                int dealt;
                bool over;
                lock (conn.Monsters)
                {
                    double now = conn.Clock.Elapsed.TotalSeconds;
                    if (conn.PlayerLife == 0 || HardcoreProgressBlocked(conn)) return;
                    double factor = now < conn.Walk.ArrivesAt ? LabyrinthTrials.SpikeBleedMovingFactor : 1;
                    dealt = (int)Math.Round(bleed.PerSecond * factor * SpikeBleedTickSeconds);
                    long lifeDamage = conn.EnergyShield.Damage(dealt, false, now);
                    conn.PlayerLife = (uint)Math.Max(0, conn.PlayerLife - lifeDamage);
                    if (conn.PlayerLife == 0) { conn.InvalidateIncomingHits(); ClearAntiquarianOnDeath(conn); }
                    RecordHardcoreDeath(conn);
                    life = conn.PlayerLife;
                    over = now + SpikeBleedTickSeconds / 2 >= bleed.Until;
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
                if (life == 0) { await PlayerDied(remote, stream, ciphers, conn, $"0x0055 death screen ({conn.League ?? "?"} league)"); return; }
                if (over) break;
            }
            if (!conn.Closed && LabyrinthTrials.BleedingBuffRow is not null)
                await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(PlayerId, shown), null);
        });
    }

    // How often a spike trap's bleed deals its damage (our own number).
    const double SpikeBleedTickSeconds = 0.2;

    // A Bronze Monograph reads at once (live 2026-10-31, the original: no "Read" option to choose, and the voice goes on
    // after the player walks away): S→C 0x84 with its only NPCTalk row's index (0, "Read") opens the talk on that row.
    internal static Task ReadTrialMonograph(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id,
        SpawnedNpc npc) =>
        Send(remote, stream, ciphers, InstanceNpcs.Talk(id, LabyrinthTrials.GlyphTalk), $"0x0084 read trial monograph 0x{id:x} {npc.Npc.Id}");

    // Whether the plaque is lit: the character has the trial's flag, or this instance's plaque was used.
    static bool TrialLit(ClientState conn, LabyrinthTrials.Trial trial)
    {
        if (ClientHasQuestFlag(conn, trial.Flag)) return true;
        lock (conn.Monsters)
            return conn.TrialObjects.Values.Any(t => t.Type == LabyrinthTrials.Plaque && conn.Instance.Transitioned(t.Type, t.X, t.Y));
    }

    // The trial object of this type nearest to a cell, closed or not in this instance.
    static (uint Id, AreaPopulation.RoomObject Object)? NearestTrialObject(ClientState conn, string type, (int X, int Y) at, bool closed)
    {
        lock (conn.Monsters)
            return conn.TrialObjects.Where(t => t.Value.Type == type && (!closed || !conn.Instance.Transitioned(type, t.Value.X, t.Value.Y)))
                .OrderBy(t => (long)(t.Value.X - at.X) * (t.Value.X - at.X) + (long)(t.Value.Y - at.Y) * (t.Value.Y - at.Y))
                .Select(t => ((uint, AreaPopulation.RoomObject)?)(t.Key, t.Value)).FirstOrDefault();
    }
}
