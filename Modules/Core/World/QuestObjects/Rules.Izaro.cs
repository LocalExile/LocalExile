using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;

static partial class QuestobjectsRules
{
    // Izaro's arena (IzaroFights): the corridor's gate, his elevator and the arena's doors, then the trial. The gate is shut
    // until the player uses it (UseTrialObject): it opens, he says the trial's prefight line and the way into the arena
    // shows. The arena's doors stand open until the trial starts. As the player comes into the arena room he (the day's
    // weapon, unique, the life the earlier trials left) rises on the elevator saying the arena's mechanic's line, the doors
    // shut, and he is out of the AI and untargetable until the rise has played; then he fights, the life bar showing as he
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
            $"0x02b9 Izaro's elevator 0x{lift:x} at ({elevator.X}, {elevator.Y})");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(lift), null);
        var doors = new List<(uint Id, AreaPopulation.RoomObject Door)>();
        foreach (var door in all.Where(o => o.Type == IzaroFights.ArenaDoor))
        {
            uint id = conn.NextObjectId++;
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(door.Type),
                IzaroFights.Blob(door.Type, door.Model, door.X, door.Y, door.Angle, IzaroFights.DoorOpen)),
                $"0x02b9 Izaro's arena door 0x{id:x} at ({door.X}, {door.Y}), open");
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
            doors.Add((id, door));
        }
        if (all.FirstOrDefault(o => o.Type == IzaroFights.Gate) is { } gate)
        {
            bool open = beaten || conn.Instance.HasState(IzaroFights.GateOpen);
            uint id = conn.NextObjectId++;
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(gate.Type),
                IzaroFights.Blob(gate.Type, gate.Model, gate.X, gate.Y, gate.Angle, open ? IzaroFights.GateOpened : IzaroFights.GateShut)),
                $"0x02b9 Izaro's gate 0x{id:x} at ({gate.X}, {gate.Y}), {(open ? "open" : "shut")}");
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
            if (!open)
            {
                lock (conn.Monsters) conn.TrialObjects[id] = gate;
                CloseIzaroDoor(conn, id, gate);
            }
        }
        Console.WriteLine($"[gateway] {remote} {what} in {conn.Layout.Id}: {(beaten ? "won in this instance" : $"Izaro {trial.Variety} from {trial.StartPercent}% to {trial.LeavesAtPercent}%, mechanic {trial.Mechanic ?? "none"}")}, {doors.Count} door(s)");
        if (beaten) return;
        _ = IzaroTrial(remote, stream, ciphers, conn, layout, trial, elevator, lift, doors);
    }

    // The corridor's gate used (UseTrialObject): it opens for good in this instance, he says the trial's prefight line and
    // the way into the arena shows.
    internal static async Task OpenIzaroGate(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id,
        AreaPopulation.RoomObject gate)
    {
        if (!conn.Instance.SetState(IzaroFights.GateOpen)) return;
        await Send(remote, stream, ciphers, [.. InstanceEntities.TransitionableUpdate(id, IzaroFights.TransitionableIndex, IzaroFights.GateOpened),
            .. InstanceEntities.BlockageUpdate(id, false)], $"0x02ba Izaro's gate 0x{id:x} at ({gate.X}, {gate.Y}) opens");
        OpenIzaroDoor(conn, id);
        lock (conn.Monsters) conn.TrialObjects.Remove(id);
        if (IzaroFights.For(conn.Layout.Id, conn.Seed) is { } trial) await IzaroSays(remote, stream, ciphers, 0, IzaroFights.PrefightLine(trial));
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

    static async Task IzaroTrial(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, AreaLayout layout,
        IzaroFights.Trial trial, AreaPopulation.RoomObject elevator, uint lift, List<(uint Id, AreaPopulation.RoomObject Door)> doors)
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
                    (boss.BusyUntil, boss.Aggro) = (double.PositiveInfinity, false);
                    if (!trial.Last) boss.LifePhases = new([trial.LeavesAtPercent]);
                }
            }
            if (boss is null) return;
            byte transitionable = (byte)variety.ComponentsOrBase.ToList().IndexOf("Transitionable");
            // The rise: the elevator's raise and his Emerge (lift_raise_*), both about 10.7 s, with the mechanic's line; the
            // arena's doors shut.
            await Send(remote, stream, ciphers, InstanceEntities.TransitionableUpdate(lift, IzaroFights.TransitionableIndex, IzaroFights.Raising),
                $"0x02ba Izaro's elevator 0x{lift:x} rises");
            if (Emerges.SkillId is ushort emerge)
                await Send(remote, stream, ciphers, [.. InstanceProtocol.ActionStarted(izaro, elevator.X, elevator.Y, elevator.X, elevator.Y, emerge, 0xffff, conn.MonsterOrderFlags),
                    .. InstanceProtocol.ActionEnded(izaro)], $"0x01ab Izaro 0x{izaro:x} rises on the elevator");
            await IzaroSays(remote, stream, ciphers, izaro, IzaroFights.StartLine(trial));
            foreach (var (id, door) in doors)
            {
                await Send(remote, stream, ciphers, [.. InstanceEntities.TransitionableUpdate(id, IzaroFights.TransitionableIndex, IzaroFights.DoorShut),
                    .. InstanceEntities.BlockageUpdate(id, true)], $"0x02ba Izaro's arena door 0x{id:x} at ({door.X}, {door.Y}) shuts");
                CloseIzaroDoor(conn, id, door);
            }
            await Task.Delay(TimeSpan.FromSeconds(IzaroFights.RiseSeconds));
            if (!Here()) return;
            await Send(remote, stream, ciphers, [.. InstanceEntities.TransitionableUpdate(lift, IzaroFights.TransitionableIndex, IzaroFights.Raised),
                .. InstanceEntities.TransitionableUpdate(izaro, transitionable, IzaroFights.Fighting)], $"0x02ba Izaro 0x{izaro:x} fights");
            lock (conn.Monsters) (boss.BusyUntil, boss.Aggro) = (conn.Clock.Elapsed.TotalSeconds, true);
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
                    await IzaroSays(remote, stream, ciphers, 0, IzaroFights.EndLine(trial));
                    await WinIzaroTrial(remote, stream, ciphers, conn, trial, doors);
                    return;
                }
                if (leaves)
                {
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
            .. InstanceEntities.TransitionableUpdate(lift, IzaroFights.TransitionableIndex, IzaroFights.Lowering)],
            $"0x02ba Izaro 0x{izaro:x} goes down, the elevator lowers");
        await WinIzaroTrial(remote, stream, ciphers, conn, trial, doors);
        await Task.Delay(TimeSpan.FromSeconds(IzaroFights.LowerSeconds));
        lock (conn.Monsters)
        {
            boss.Dead = true;
            if (ReferenceEquals(conn.Monsters.GetValueOrDefault(izaro), boss)) conn.Monsters.Remove(izaro);
        }
        if (!here()) return;
        await Send(remote, stream, ciphers, [.. InstanceEntities.TransitionableUpdate(lift, IzaroFights.TransitionableIndex, IzaroFights.Down),
            .. InstanceProtocol.RemoveObject(izaro)], $"0x02bb Izaro 0x{izaro:x} is gone");
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
                .. InstanceEntities.BlockageUpdate(id, false)], $"0x02ba Izaro's arena door 0x{id:x} at ({door.X}, {door.Y}) opens");
            OpenIzaroDoor(conn, id);
        }
        await UpdateShownTransitions(remote, stream, ciphers, conn, "Izaro's trial is won");
        if (conn.Character is { Length: > 0 } character && LabyrinthDay.IzaroBeaten(character, trial.Number) is { } progress)
            await Send(remote, stream, ciphers, progress, $"0x01cb labyrinth progress: Izaro's trial {trial.Number} won");
        if (trial.Last) await SetQuestFlags(remote, stream, ciphers, conn, IzaroFights.CompleteFlags(trial.Tier));
    }
}
