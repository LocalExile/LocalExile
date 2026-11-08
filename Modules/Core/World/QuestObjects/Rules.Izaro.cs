using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;

static partial class QuestobjectsRules
{
    // Izaro's arena (IzaroFights): the corridor's gate, his elevator and the arena's doors, then the trial. The gate is shut
    // until the player uses it (UseTrialObject): it opens and the way into the arena
    // shows. The arena's doors stand open until the trial starts. As the player comes into the arena room he (the day's
    // weapon, unique, the life the earlier trials left) rises on the elevator saying the arena's mechanic's line, the doors
    // shut, and he is out of the AI, untargetable and takes no hits (EntranceGrace: area skills reached him, live) until the
    // rise has played; then he fights, the life bar showing as he
    // aggroes. In a trial before the last his life stops at the next third (LifePhaseGate): he stops fighting, says his
    // line, walks to the elevator's middle once his swing is over, stands, turns untargetable (Transitionable 4) and goes
    // down on it (his Hide clip, the elevator's lower) while the doors open. The last trial he dies saying his line, and the
    // tier's flags are set. Either way the arena's ways out show (IzaroFights.Beaten, Game.TransitionFlag) and the run's
    // map loses his icon there. With the trial won in this instance the doors and the gate stand open, the elevator down.
    internal static async Task RunIzaroArena(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        List<AreaPopulation.RoomObject> all)
    {
        if (IzaroFights.For(conn.Layout.Id, conn.Seed) is not { } trial) return;
        string what = $"Izaro's trial {trial.Number} of {trial.Of}";
        var elevator = all.FirstOrDefault(o => o.Type == IzaroFights.Elevator);
        if (elevator is null)
        {
            Console.WriteLine($"[gateway] {remote} {what}: no Elevator_Izaro in {conn.Layout.Id} seed {conn.Seed}");
            return;
        }
        var layout = conn.Layout;
        bool beaten = conn.Instance.HasState(IzaroFights.Beaten);
        uint lift = conn.NextObjectId++;
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(lift, InstanceEntities.ObjectTypeHash(elevator.Type),
            IzaroFights.Blob(elevator.Type, elevator.Model, elevator.X, elevator.Y, elevator.Angle, beaten ? IzaroFights.Down : IzaroFights.Lowered)),
            $"0x02bb Izaro's elevator 0x{lift:x} at ({elevator.X}, {elevator.Y})");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(lift), null);
        var doors = new List<(uint Id, AreaPopulation.RoomObject Door)>();
        foreach (var door in all.Where(o => o.Type == IzaroFights.ArenaDoor))
        {
            uint id = conn.NextObjectId++;
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(door.Type),
                IzaroFights.Blob(door.Type, door.Model, door.X, door.Y, door.Angle, IzaroFights.DoorOpen)),
                $"0x02bb Izaro's arena door 0x{id:x} at ({door.X}, {door.Y}), open");
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
            doors.Add((id, door));
        }
        // The alcoves' railings stand up (a mechanic with monsters lowers them).
        var railings = new List<(uint Id, AreaPopulation.RoomObject At)>();
        foreach (var railing in all.Where(o => o.Type == IzaroFights.Railing))
        {
            uint id = conn.NextObjectId++;
            await Send(remote, stream, ciphers, [.. InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(railing.Type),
                IzaroFights.Blob(railing.Type, railing.Model, railing.X, railing.Y, railing.Angle, IzaroFights.RailingUp)),
                .. InstanceProtocol.EnterWakeZone(id)], $"0x02bb alcove railing 0x{id:x} at ({railing.X}, {railing.Y})");
            CloseIzaroDoor(conn, id, railing);
            railings.Add((id, railing));
        }
        if (all.FirstOrDefault(o => o.Type == IzaroFights.Gate) is { } gate)
        {
            bool open = beaten || conn.Instance.HasState(IzaroFights.GateOpen);
            uint id = conn.NextObjectId++;
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(gate.Type),
                IzaroFights.Blob(gate.Type, gate.Model, gate.X, gate.Y, gate.Angle, open ? IzaroFights.GateOpened : IzaroFights.GateShut)),
                $"0x02bb Izaro's gate 0x{id:x} at ({gate.X}, {gate.Y}), {(open ? "open" : "shut")}");
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
            if (!open)
            {
                lock (conn.Monsters) conn.TrialObjects[id] = gate;
                CloseIzaroDoor(conn, id, gate);
            }
        }
        var mechanics = await PlaceIzaroMechanics(remote, stream, ciphers, conn, trial, all, railings, beaten);
        if (all.FirstOrDefault(o => o.Type == PrefightSpeech) is { } speech && !conn.Instance.HasState(PrefightSaid))
            _ = IzaroPrefight(remote, stream, ciphers, conn, layout, trial, speech);
        Console.WriteLine($"[gateway] {remote} {what} in {conn.Layout.Id}: {(beaten ? "won in this instance" : $"Izaro {trial.Variety} from {trial.StartPercent}% to {trial.LeavesAtPercent}%, mechanic {trial.Mechanic ?? "none"}")}, {doors.Count} door(s)");
        if (beaten) return;
        _ = IzaroTrial(remote, stream, ciphers, conn, layout, trial, elevator, lift, doors, mechanics);
    }

    // The corridor's gate used (UseTrialObject): it opens for good in this instance and the way into the arena shows (no
    // line: the original says none there, live 2026-11-08).
    internal static async Task OpenIzaroGate(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id,
        AreaPopulation.RoomObject gate)
    {
        if (!conn.Instance.SetState(IzaroFights.GateOpen)) return;
        await Send(remote, stream, ciphers, [.. InstanceEntities.TransitionableUpdate(id, IzaroFights.TransitionableIndex, IzaroFights.GateOpened),
            .. InstanceEntities.BlockageUpdate(id, false)], $"0x02bc Izaro's gate 0x{id:x} at ({gate.X}, {gate.Y}) opens");
        OpenIzaroDoor(conn, id);
        lock (conn.Monsters) conn.TrialObjects.Remove(id);
        await UpdateShownTransitions(remote, stream, ciphers, conn, "Izaro's gate opens");
    }

    // A shut door's cells on the server's walk map (its hull, as the client's TriggerableBlockage closes it), and opened.
    static void CloseIzaroDoor(ClientState conn, uint id, AreaPopulation.RoomObject door)
    {
        if (conn.Layout.Walkable is not { } walk) return;
        var (halfX, halfY) = LabyrinthTrials.DoorHull(door.Model);
        lock (conn.Monsters)
        {
            conn.DoorBarriers[id] = LabyrinthTrials.HullCells(door.X, door.Y, door.Angle, halfX, halfY).Where(c => walk(c.X, c.Y)).ToHashSet();
            conn.UpdateBarrier();
        }
    }

    // An elevator's state change, with its blockage: closed on the client (0x1c6) and on the server's walk map while it moves.
    static byte[] LiftState(ClientState conn, uint id, AreaPopulation.RoomObject lift, byte state)
    {
        bool moving = IzaroFights.LiftMoving(state);
        if (moving && conn.Layout.Walkable is { } walk)
        {
            int r = IzaroFights.LiftRadius(lift.Type);
            var cells = new HashSet<(int X, int Y)>();
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                    if (dx * dx + dy * dy <= r * r && walk(lift.X + dx, lift.Y + dy)) cells.Add((lift.X + dx, lift.Y + dy));
            lock (conn.Monsters)
            {
                conn.DoorBarriers[id] = cells;
                conn.UpdateBarrier();
            }
        }
        else OpenIzaroDoor(conn, id);
        return [.. InstanceEntities.TransitionableUpdate(id, IzaroFights.TransitionableIndex, state), .. InstanceEntities.BlockageUpdate(id, moving)];
    }

    static void OpenIzaroDoor(ClientState conn, uint id)
    {
        lock (conn.Monsters)
        {
            conn.DoorBarriers.Remove(id);
            conn.UpdateBarrier();
        }
    }

    // What Izaro says (S→C 0x1c7), from his object or (0) globally.
    static async Task IzaroSays(string remote, NetworkStream stream, ConnectionCiphers ciphers, uint from, string? line)
    {
        if (line is null) return;
        if (IzaroFights.Speech(from, line) is { } speech) await Send(remote, stream, ciphers, speech, $"0x01c7 Izaro says {line}");
        else Console.WriteLine($"[gateway] {remote} Izaro's line {line}: not in NPCTextAudio");
    }

    // The room before the arena's speech trigger (server only: IzaroSpeech/BossPrefight): as the player comes into its
    // room he says the trial's prefight line (Izaro<n>_Prefight), once per instance (the original, live).
    const string PrefightSpeech = "Metadata/Terrain/Labyrinth/Objects/IzaroSpeech/BossPrefight";
    static readonly string PrefightSaid = Quests.InstanceFlag("IzaroPrefight");

    static async Task IzaroPrefight(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, AreaLayout layout,
        IzaroFights.Trial trial, AreaPopulation.RoomObject speech)
    {
        bool Here() => !conn.Closed && !conn.LeavingArea && ReferenceEquals(conn.Layout, layout);
        var room = ServerLevels.For(conn.Layout.Id, conn.Seed)?.Rooms
            .FirstOrDefault(r => speech.X >= r.X && speech.X < r.X + r.Width && speech.Y >= r.Y && speech.Y < r.Y + r.Height);
        if (room is null) return;
        try
        {
            await Task.Delay(1000);
            while (Here())
            {
                (int X, int Y) at;
                lock (conn.Monsters) at = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
                if (at.X >= room.X && at.X < room.X + room.Width && at.Y >= room.Y && at.Y < room.Y + room.Height)
                {
                    if (conn.Instance.SetState(PrefightSaid))
                        await IzaroSays(remote, stream, ciphers, 0, $"Izaro{Math.Clamp(trial.Number, 1, 3)}_Prefight");
                    return;
                }
                await Task.Delay(200);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    // His life's floor in a trial before the last (cannot_drop_below_%_life, S→C 0x1b3; the server's own floor is
    // LifePhaseGate): the boss bar draws UniqueBossFrameImmuneOverlay once his life is at it (3.29.3b: 0x14028ec50 on each life
    // change, key 0xca8), as the original dims it while he leaves (live). is_hidden_monster (0x14028ed50's other way to
    // the overlay), cannot_be_damaged, base_cannot_be_damaged and the invisible cannot_be_damaged buff did not change the
    // bar (live 2026-11-08).
    static byte[] IzaroLifeFloor(uint izaro, IzaroFights.Trial trial) =>
        !trial.Last && Stats.Key("cannot_drop_below_%_life") is uint key ? InstanceEntities.StatsUpdate(izaro, [(key, trial.LeavesAtPercent)]) : [];

    // His second line at a trial's end (the arena's mechanic's, IzaroFinish…), after the first has played (the original:
    // both, one after the other).
    static async Task IzaroSaysLater(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, string? line, Func<bool> here)
    {
        if (line is null) return;
        await Task.Delay(TimeSpan.FromSeconds(IzaroFights.LineSeconds));
        if (!here()) return;
        try { await IzaroSays(remote, stream, ciphers, 0, line); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    static async Task IzaroTrial(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, AreaLayout layout,
        IzaroFights.Trial trial, AreaPopulation.RoomObject elevator, uint lift, List<(uint Id, AreaPopulation.RoomObject Door)> doors,
        IzaroArena mechanics)
    {
        string what = $"Izaro's trial {trial.Number} of {trial.Of}";
        bool Here() => !conn.Closed && !conn.LeavingArea && ReferenceEquals(conn.Layout, layout);
        try
        {
            // The arena: the placed room the elevator is in (else 60 cells round it).
            var arena = ServerLevels.For(conn.Layout.Id, conn.Seed)?.Rooms
                .FirstOrDefault(r => elevator.X >= r.X && elevator.X < r.X + r.Width && elevator.Y >= r.Y && elevator.Y < r.Y + r.Height);
            bool InArena((int X, int Y) p) => arena is { } r
                ? p.X >= r.X && p.X < r.X + r.Width && p.Y >= r.Y && p.Y < r.Y + r.Height
                : (p.X - elevator.X) * (double)(p.X - elevator.X) + (p.Y - elevator.Y) * (double)(p.Y - elevator.Y) <= 60 * 60;
            while (Here())
            {
                (int X, int Y) at;
                lock (conn.Monsters) at = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
                if (InArena(at)) break;
                await Task.Delay(200);
            }
            if (!Here()) return;
            if (Monsters.Find(trial.Variety) is not { Supported: true } variety)
            {
                Console.WriteLine($"[gateway] {remote} {what}: {trial.Variety} cannot be created");
                return;
            }
            byte level = (byte)Math.Clamp(ServerLevels.AreaLevel(conn.Layout.Id), 1, 100);
            uint izaro = await SpawnMonster(remote, stream, ciphers, conn, variety, level, MonsterRarity.Unique, elevator.X, elevator.Y,
                placement: MonsterPlacement.Established, lifePercent: trial.StartPercent);
            SpawnedMonster? boss;
            lock (conn.Monsters)
            {
                boss = conn.Monsters.GetValueOrDefault(izaro);
                if (boss is not null)
                {
                    (boss.BusyUntil, boss.Aggro, boss.EntranceGrace) = (double.PositiveInfinity, false, true);
                    if (!trial.Last) boss.LifePhases = new([trial.LeavesAtPercent]);
                }
            }
            if (boss is null) return;
            byte transitionable = (byte)variety.ComponentsOrBase.ToList().IndexOf("Transitionable");
            // The rise: the elevator's raise and his Emerge (lift_raise_*), both about 10.7 s, with the mechanic's line; the
            // arena's doors shut. No life bar while he rises (the original, live): the boss bar waits for the fight
            // (EntranceGrace holds the AI's), the mini bar over him is hidden.
            await Send(remote, stream, ciphers, [.. LiftState(conn, lift, elevator, IzaroFights.Raising), .. IzaroLifeFloor(izaro, trial),
                .. InstanceEntities.StatsUpdate(izaro, [(StatKeys.HideMiniLifeBar, 1)])],
                $"0x02bc Izaro's elevator 0x{lift:x} rises, his life's floor {(trial.Last ? 0 : trial.LeavesAtPercent)}%");
            if (Emerges.SkillId is ushort emerge)
                await Send(remote, stream, ciphers, [.. InstanceProtocol.ActionStarted(izaro, elevator.X, elevator.Y, elevator.X, elevator.Y, emerge, 0xffff, conn.MonsterOrderFlags),
                    .. InstanceProtocol.ActionEnded(izaro)], $"0x01ab Izaro 0x{izaro:x} rises on the elevator");
            await IzaroSays(remote, stream, ciphers, izaro, IzaroFights.StartLine(trial));
            RiseIzaroMechanics(remote, stream, ciphers, conn, mechanics, izaro, boss, Here);
            foreach (var (id, door) in doors)
            {
                await Send(remote, stream, ciphers, [.. InstanceEntities.TransitionableUpdate(id, IzaroFights.TransitionableIndex, IzaroFights.DoorShut),
                    .. InstanceEntities.BlockageUpdate(id, true)], $"0x02bc Izaro's arena door 0x{id:x} at ({door.X}, {door.Y}) shuts");
                CloseIzaroDoor(conn, id, door);
            }
            await Task.Delay(TimeSpan.FromSeconds(IzaroFights.RiseSeconds));
            if (!Here()) return;
            await Send(remote, stream, ciphers, [.. LiftState(conn, lift, elevator, IzaroFights.Raised),
                .. InstanceEntities.TransitionableUpdate(izaro, transitionable, IzaroFights.Fighting),
                .. InstanceEntities.StatsUpdate(izaro, [(StatKeys.HideMiniLifeBar, 0)])], $"0x02bc Izaro 0x{izaro:x} fights");
            lock (conn.Monsters)
            {
                (boss.BusyUntil, boss.Aggro, boss.EntranceGrace, boss.BossBarShown) = (conn.Clock.Elapsed.TotalSeconds, true, false, true);
                conn.BossBarsSeen.Add(izaro);
            }
            await ShowBossLifeBar(remote, stream, ciphers, izaro, boss, conn);
            await StartIzaroMechanics(remote, stream, ciphers, conn, mechanics, izaro, boss, Here);
            // The fight, until he dies (the last trial) or his life reaches the trial's end (the others).
            while (Here())
            {
                bool dead, leaves = false;
                int action = 0;
                double free = 0;
                lock (conn.Monsters)
                {
                    dead = boss.Dead || !ReferenceEquals(conn.Monsters.GetValueOrDefault(izaro), boss);
                    if (!dead && boss.LifePhases is { } gate && gate.Begin(boss.Life, boss.MaxLife))
                    {
                        leaves = true;
                        free = boss.BusyUntil;   // the end of his swing, if one is on
                        (boss.BusyUntil, boss.Aggro) = (double.PositiveInfinity, false);
                        boss.SwingGeneration = null;
                        action = ++boss.ActionGeneration;
                    }
                }
                if (dead)
                {
                    if (!trial.Last) return;   // gone some other way: the trial is not won
                    Console.WriteLine($"[gateway] {remote} {what}: Izaro 0x{izaro:x} dies");
                    string? finish = await EndIzaroMechanics(remote, stream, ciphers, conn);
                    await IzaroSays(remote, stream, ciphers, 0, IzaroFights.EndLine(trial));
                    _ = IzaroSaysLater(remote, stream, ciphers, conn, finish, Here);
                    await WinIzaroTrial(remote, stream, ciphers, conn, trial, doors);
                    return;
                }
                if (leaves)
                {
                    string? finish = await EndIzaroMechanics(remote, stream, ciphers, conn);
                    _ = IzaroSaysLater(remote, stream, ciphers, conn, finish, Here);
                    await IzaroLeaves(remote, stream, ciphers, conn, trial, elevator, lift, doors, izaro, boss, action, free, transitionable, Here);
                    return;
                }
                await Task.Delay(200);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] {remote} {what} stopped: {ServerLog.Describe(ex)}"); }
    }

    // A trial before the last ends: his line, his swing played out, the walk to the elevator's middle, a stand, then down on
    // the elevator while the doors open; he is gone when it is down.
    static async Task IzaroLeaves(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroFights.Trial trial,
        AreaPopulation.RoomObject elevator, uint lift, List<(uint Id, AreaPopulation.RoomObject Door)> doors, uint izaro, SpawnedMonster boss,
        int action, double free, byte transitionable, Func<bool> here)
    {
        Console.WriteLine($"[gateway] {remote} Izaro's trial {trial.Number}: Izaro 0x{izaro:x} at {boss.Life}/{boss.MaxLife} life leaves");
        await IzaroSays(remote, stream, ciphers, izaro, IzaroFights.EndLine(trial));
        double said = conn.Clock.Elapsed.TotalSeconds;
        // An order during his swing is lost on the client (live: he stood where he was): wait for its end.
        double swing = Math.Min(free, said + IzaroFights.SwingWaitSeconds) - said;
        if (swing > 0) await Task.Delay(TimeSpan.FromSeconds(swing + 0.1));
        if (!here()) return;
        double arrives = conn.Clock.Elapsed.TotalSeconds;
        await Send(remote, stream, ciphers, InstanceProtocol.ActionEnded(izaro), null);
        await SendMonsterAction(remote, stream, ciphers, conn, izaro, boss, action, () =>
        {
            double at = conn.Clock.Elapsed.TotalSeconds;
            var from = boss.Walk.Position(at);
            boss.Walk.Order(elevator.X, elevator.Y, at);
            arrives = boss.Walk.ArrivesAt;
            return InstanceProtocol.ActionStarted(izaro, from.X, from.Y, elevator.X, elevator.Y, IzaroFights.MoveSkill, conn.NextActionSequence(), conn.MonsterOrderFlags);
        }, $"0x01ab Izaro 0x{izaro:x} walks to the elevator's middle ({elevator.X}, {elevator.Y})");
        // On the elevator, he stands a moment (and the line plays out) before it lowers.
        double wait = Math.Max(arrives + IzaroFights.StandSeconds, said + IzaroFights.LineSeconds) - conn.Clock.Elapsed.TotalSeconds;
        if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(Math.Min(wait, 15)));
        if (!here()) return;
        await Send(remote, stream, ciphers, [.. InstanceEntities.TransitionableUpdate(izaro, transitionable, IzaroFights.Defeated),
            .. InstanceProtocol.AnimationUpdate(izaro, 0, IzaroFights.LowerClip(trial.Weapon), 1),
            .. LiftState(conn, lift, elevator, IzaroFights.Lowering)],
            $"0x02bc Izaro 0x{izaro:x} goes down, the elevator lowers");
        await WinIzaroTrial(remote, stream, ciphers, conn, trial, doors);
        await Task.Delay(TimeSpan.FromSeconds(IzaroFights.LowerSeconds));
        lock (conn.Monsters)
        {
            boss.Dead = true;
            if (ReferenceEquals(conn.Monsters.GetValueOrDefault(izaro), boss)) conn.Monsters.Remove(izaro);
        }
        if (!here()) return;
        await Send(remote, stream, ciphers, [.. LiftState(conn, lift, elevator, IzaroFights.Down),
            .. InstanceProtocol.RemoveObject(izaro)], $"0x02bd Izaro 0x{izaro:x} is gone");
    }

    // The trial won: the instance keeps it, the doors open, the ways out show, the run's map loses his icon there, and the
    // last trial sets the tier's flags.
    static async Task WinIzaroTrial(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroFights.Trial trial,
        List<(uint Id, AreaPopulation.RoomObject Door)> doors)
    {
        if (!conn.Instance.SetState(IzaroFights.Beaten)) return;
        foreach (var (id, door) in doors)
        {
            await Send(remote, stream, ciphers, [.. InstanceEntities.TransitionableUpdate(id, IzaroFights.TransitionableIndex, IzaroFights.DoorOpen),
                .. InstanceEntities.BlockageUpdate(id, false)], $"0x02bc Izaro's arena door 0x{id:x} at ({door.X}, {door.Y}) opens");
            OpenIzaroDoor(conn, id);
        }
        await UpdateShownTransitions(remote, stream, ciphers, conn, "Izaro's trial is won");
        if (conn.Character is { Length: > 0 } character && LabyrinthDay.IzaroBeaten(character, trial.Number) is { } progress)
            await Send(remote, stream, ciphers, progress, $"0x01cb labyrinth progress: Izaro's trial {trial.Number} won");
        if (trial.Last) await SetQuestFlags(remote, stream, ciphers, conn, IzaroFights.CompleteFlags(trial.Tier));
    }
}
