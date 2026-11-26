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
    internal static async Task RunIzaroArena(Player player,
        List<AreaPopulation.RoomObject> all)
    {
        if (IzaroFights.For(player.Layout.Id, player.Seed) is not { } trial) return;
        string what = $"Izaro's trial {trial.Number} of {trial.Of}";
        var elevator = all.FirstOrDefault(o => o.Type == IzaroFights.Elevator);
        if (elevator is null)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} {what}: no Elevator_Izaro in {player.Layout.Id} seed {player.Seed}");
            return;
        }
        var layout = player.Layout;
        bool beaten = player.Instance.HasState(IzaroFights.Beaten);
        uint lift = player.TakeObjectId();
        await Send(player, InstanceEntities.CreateObject(lift, InstanceEntities.ObjectTypeHash(elevator.Type),
            IzaroFights.Blob(elevator.Type, elevator.Model, elevator.X, elevator.Y, elevator.Angle, beaten ? IzaroFights.Down : IzaroFights.Lowered)),
            $"0x02bb Izaro's elevator 0x{lift:x} at ({elevator.X}, {elevator.Y})");
        await Send(player, InstanceProtocol.EnterWakeZone(lift), null);
        var doors = new List<(uint Id, AreaPopulation.RoomObject Door)>();
        foreach (var door in all.Where(o => o.Type == IzaroFights.ArenaDoor))
        {
            uint id = player.TakeObjectId();
            await Send(player, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(door.Type),
                IzaroFights.Blob(door.Type, door.Model, door.X, door.Y, door.Angle, IzaroFights.DoorOpen)),
                $"0x02bb Izaro's arena door 0x{id:x} at ({door.X}, {door.Y}), open");
            await Send(player, InstanceProtocol.EnterWakeZone(id), null);
            doors.Add((id, door));
        }
        // The alcoves' railings stand up (a mechanic with monsters lowers them).
        var railings = new List<(uint Id, AreaPopulation.RoomObject At)>();
        foreach (var railing in all.Where(o => o.Type == IzaroFights.Railing))
        {
            uint id = player.TakeObjectId();
            await Send(player, [.. InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(railing.Type),
                IzaroFights.Blob(railing.Type, railing.Model, railing.X, railing.Y, railing.Angle, IzaroFights.RailingUp)),
                .. InstanceProtocol.EnterWakeZone(id)], $"0x02bb alcove railing 0x{id:x} at ({railing.X}, {railing.Y})");
            CloseIzaroDoor(player, id, railing);
            railings.Add((id, railing));
        }
        if (all.FirstOrDefault(o => o.Type == IzaroFights.Gate) is { } gate)
        {
            bool open = beaten || player.Instance.HasState(IzaroFights.GateOpen);
            uint id = player.TakeObjectId();
            await Send(player, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(gate.Type),
                IzaroFights.Blob(gate.Type, gate.Model, gate.X, gate.Y, gate.Angle, open ? IzaroFights.GateOpened : IzaroFights.GateShut)),
                $"0x02bb Izaro's gate 0x{id:x} at ({gate.X}, {gate.Y}), {(open ? "open" : "shut")}");
            await Send(player, InstanceProtocol.EnterWakeZone(id), null);
            if (!open)
            {
                player.TrialObjects[id] = gate;
                CloseIzaroDoor(player, id, gate);
            }
        }
        var mechanics = await PlaceIzaroMechanics(player, trial, all, railings, beaten);
        if (all.FirstOrDefault(o => o.Type == PrefightSpeech) is { } speech && !player.Instance.HasState(PrefightSaid))
            _ = IzaroPrefight(player, layout, trial, speech);
        Console.WriteLine($"[gateway] {player.Connection.Remote} {what} in {player.Layout.Id}: {(beaten ? "won in this instance" : $"Izaro {trial.Variety} from {trial.StartPercent}% to {trial.LeavesAtPercent}%, mechanic {trial.Mechanic ?? "none"}")}, {doors.Count} door(s)");
        if (beaten) return;
        _ = IzaroTrial(player, layout, trial, elevator, lift, doors, mechanics);
    }

    // The corridor's gate used (UseTrialObject): it opens for good in this instance and the way into the arena shows (no
    // line: the original says none there, live 2026-11-08).
    internal static async Task OpenIzaroGate(Player player, uint id,
        AreaPopulation.RoomObject gate)
    {
        if (!player.Instance.SetState(IzaroFights.GateOpen)) return;
        await Send(player, [.. InstanceEntities.TransitionableUpdate(id, IzaroFights.TransitionableIndex, IzaroFights.GateOpened),
            .. InstanceEntities.BlockageUpdate(id, false)], $"0x02bc Izaro's gate 0x{id:x} at ({gate.X}, {gate.Y}) opens");
        OpenIzaroDoor(player, id);
        player.TrialObjects.Remove(id);
        await UpdateShownTransitions(player, "Izaro's gate opens");
    }

    // A shut door's cells on the server's walk map (its hull, as the client's TriggerableBlockage closes it), and opened.
    static void CloseIzaroDoor(Player player, uint id, AreaPopulation.RoomObject door)
    {
        if (player.Layout.Walkable is not { } walk) return;
        var (halfX, halfY) = LabyrinthTrials.DoorHull(door.Model);
        player.DoorBarriers[id] = LabyrinthTrials.HullCells(door.X, door.Y, door.Angle, halfX, halfY).Where(c => walk(c.X, c.Y)).ToHashSet();
        player.UpdateBarrier();
    }

    // An elevator's state change, with its blockage: closed on the client (0x1c6) and on the server's walk map while it moves.
    static byte[] LiftState(Player player, uint id, AreaPopulation.RoomObject lift, byte state)
    {
        bool moving = IzaroFights.LiftMoving(state);
        if (moving && player.Layout.Walkable is { } walk)
        {
            int r = IzaroFights.LiftRadius(lift.Type);
            var cells = new HashSet<(int X, int Y)>();
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                    if (dx * dx + dy * dy <= r * r && walk(lift.X + dx, lift.Y + dy)) cells.Add((lift.X + dx, lift.Y + dy));
            player.DoorBarriers[id] = cells;
            player.UpdateBarrier();
        }
        else OpenIzaroDoor(player, id);
        return [.. InstanceEntities.TransitionableUpdate(id, IzaroFights.TransitionableIndex, state), .. InstanceEntities.BlockageUpdate(id, moving)];
    }

    static void OpenIzaroDoor(Player player, uint id)
    {
        player.DoorBarriers.Remove(id);
        player.UpdateBarrier();
    }

    // He walks through his minions (the original, live): the client's Pathfinding reads this stat for its blocker filter
    // (0x141ea3618, as a Quartz Flask's, 13-items.md); without it they stopped his model while the server's walk went on.
    static IReadOnlyList<(uint Key, int Value)>? IzaroPhases =>
        Stat.VirtualPhaseThroughObjects is var phases ? [(phases, 1)] : null;

    // What Izaro says (S→C 0x1c7), from his object or (0) globally.
    static async Task IzaroSays(Player player, uint from, string? line)
    {
        if (line is null) return;
        if (IzaroFights.Speech(from, line) is { } speech) await Send(player, speech, $"0x01c7 Izaro says {line}");
        else Console.WriteLine($"[gateway] {player.Connection.Remote} Izaro's line {line}: not in NPCTextAudio");
    }

    // The room before the arena's speech trigger (server only: IzaroSpeech/BossPrefight): as the player comes into its
    // room he says the trial's prefight line (Izaro<n>_Prefight), once per instance (the original, live).
    const string PrefightSpeech = "Metadata/Terrain/Labyrinth/Objects/IzaroSpeech/BossPrefight";
    static readonly string PrefightSaid = Quests.InstanceFlag("IzaroPrefight");

    static async Task IzaroPrefight(Player player, AreaLayout layout,
        IzaroFights.Trial trial, AreaPopulation.RoomObject speech)
    {
        bool Here() => !player.Closed && !player.LeavingArea && ReferenceEquals(player.Layout, layout);
        var room = ServerLevels.For(player.Layout.Id, player.Seed)?.Rooms
            .FirstOrDefault(r => speech.X >= r.X && speech.X < r.X + r.Width && speech.Y >= r.Y && speech.Y < r.Y + r.Height);
        if (room is null) return;
        try
        {
            await AreaDelay(player, 1);
            while (Here())
            {
                (int X, int Y) at;
                at = player.Walk.Position(AreaTime(player));
                if (at.X >= room.X && at.X < room.X + room.Width && at.Y >= room.Y && at.Y < room.Y + room.Height)
                {
                    if (player.Instance.SetState(PrefightSaid))
                        await IzaroSays(player, 0, $"Izaro{Math.Clamp(trial.Number, 1, 3)}_Prefight");
                    return;
                }
                await AreaDelay(player, 0.2);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    // His life's floor in a trial before the last (cannot_drop_below_%_life, S→C 0x1b3; the server's own floor is
    // LifePhaseGate): the boss bar draws UniqueBossFrameImmuneOverlay once his life is at it (3.29.3c: 0x14028f1e0 on each life
    // change, key 0xca8), as the original dims it while he leaves (live). is_hidden_monster (the client's other way to
    // the overlay), cannot_be_damaged, base_cannot_be_damaged and the invisible cannot_be_damaged buff did not change the
    // bar (live 2026-11-08).
    static void IzaroLifeFloor(Player player, uint izaro, IzaroFights.Trial trial)
    {
        if (!trial.Last) global::Game.SetStats(player, izaro, [(Stat.CannotDropBelowPctLife, trial.LeavesAtPercent)]);
    }

    // The Izaros this instance still has: the arena spawns his variety alone, so any monster of it is a leftover from an
    // earlier entry. He is retired rather than forgotten: dead, out of life so no hit lands on the body, with his actions
    // stopped, out of the instance's monsters and origins, and every player in the instance is told he is gone.
    static List<uint> LeftoverIzaro(Player player, IzaroFights.Trial trial)
    {
        var ids = new List<uint>();
        foreach (var (id, monster) in player.Area.Monsters)
        {
            // The Goddess of Justice on his back goes with him, or she would stay attached to a removed parent.
            if (monster.Variety?.Id != trial.Variety && monster.Variety?.Id != IzaroGoddess.Variety) continue;
            monster.Dead = true;
            monster.Life = 0;
            monster.ActionGeneration++;
            monster.NoRewards = true;   // retired, not killed: the fight that owned him is not won (IzaroTrial)
            ids.Add(id);
        }
        foreach (uint id in ids)
        {
            player.Area.Monsters.Remove(id);
            player.Instance.World.MonsterOrigins.TryRemove(id, out _);
        }
        return ids;
    }

    // His second line at a trial's end (the arena's mechanic's, IzaroFinish…), after the first has played (the original:
    // both, one after the other).
    static async Task IzaroSaysLater(Player player, string? line, Func<bool> here)
    {
        if (line is null) return;
        await AreaDelay(player, IzaroFights.LineSeconds);
        if (!here()) return;
        try { await IzaroSays(player, 0, line); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    static async Task IzaroTrial(Player player, AreaLayout layout,
        IzaroFights.Trial trial, AreaPopulation.RoomObject elevator, uint lift, List<(uint Id, AreaPopulation.RoomObject Door)> doors,
        IzaroArena mechanics)
    {
        string what = $"Izaro's trial {trial.Number} of {trial.Of}";
        bool Here() => !player.Closed && !player.LeavingArea && ReferenceEquals(player.Layout, layout);
        try
        {
            // The arena: the placed room the elevator is in (else 60 cells round it).
            var arena = ServerLevels.For(player.Layout.Id, player.Seed)?.Rooms
                .FirstOrDefault(r => elevator.X >= r.X && elevator.X < r.X + r.Width && elevator.Y >= r.Y && elevator.Y < r.Y + r.Height);
            bool InArena((int X, int Y) p) => arena is { } r
                ? p.X >= r.X && p.X < r.X + r.Width && p.Y >= r.Y && p.Y < r.Y + r.Height
                : (p.X - elevator.X) * (double)(p.X - elevator.X) + (p.Y - elevator.Y) * (double)(p.Y - elevator.Y) <= 60 * 60;
            while (Here())
            {
                (int X, int Y) at;
                at = player.Walk.Position(AreaTime(player));
                if (InArena(at)) break;
                await AreaDelay(player, 0.2);
            }
            if (!Here()) return;
            if (Monsters.Find(trial.Variety) is not { Supported: true } variety)
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} {what}: {trial.Variety} cannot be created");
                return;
            }
            byte level = (byte)Math.Clamp(ServerLevels.AreaLevel(player.Layout.Id), 1, 100);
            // An Izaro already up in this instance (a player who died and came back through the corridor, or a second
            // connection into the same instance) is taken down first: one Izaro, one life bar, one set of shutting doors
            // and one death belong to one fight (live 2026-11-11: two of him, both chasing the player).
            // Highest id first: the Goddess on his back was created after him and goes before him, as when he leaves.
            foreach (uint stale in LeftoverIzaro(player, trial).OrderByDescending(i => i))
            {
                await Send(player, InstanceProtocol.RemoveObject(stale), $"0x02bd Izaro 0x{stale:x} is gone");
                // The others in the instance see him too: each gets its own bytes (a send encrypts them in place).
                foreach (var peer in player.Instance.Presence.PlayersExcept(player.Id))
                {
                    if (peer is not { HasClient: true, Closed: false, LeavingArea: false } other) continue;
                    await Send(other, InstanceProtocol.RemoveObject(stale), $"0x02bd Izaro 0x{stale:x} is gone");
                }
            }
            uint izaro = await SpawnMonster(player, variety, level, MonsterRarity.Unique, elevator.X, elevator.Y,
                placement: MonsterPlacement.Established, lifePercent: trial.StartPercent, ownedLooks: variety.Weapons, extraStats: IzaroPhases,
                movement: IzaroFights.Movement);
            SpawnedMonster? boss;
            boss = player.Area.Monsters.GetValueOrDefault(izaro);
            if (boss is not null)
            {
                (boss.BusyUntil, boss.Aggro, boss.EntranceGrace, boss.ScriptHeld) = (double.PositiveInfinity, false, true, true);
                if (!trial.Last) boss.LifePhases = new([trial.LeavesAtPercent]);
            }
            if (boss is null) return;
            await SpawnIzaroGoddess(player, mechanics, izaro, level, (elevator.X, elevator.Y));
            byte transitionable = (byte)variety.ComponentsOrBase.ToList().IndexOf("Transitionable");
            // The rise: the elevator's raise and his Emerge (lift_raise_*), both about 10.7 s, with the mechanic's line; the
            // arena's doors shut. No life bar while he rises (the original, live): the boss bar waits for the fight
            // (EntranceGrace holds the AI's), the mini bar over him is hidden.
            IzaroLifeFloor(player, izaro, trial);
            global::Game.SetStats(player, izaro, [(Stat.HideMiniLifeBar, 1)]);
            await Send(player, [.. LiftState(player, lift, elevator, IzaroFights.Raising)],
                $"0x02bc Izaro's elevator 0x{lift:x} rises, his life's floor {(trial.Last ? 0 : trial.LeavesAtPercent)}%");
            if (Emerges.SkillId is ushort emerge)
                await Send(player, [.. InstanceProtocol.ActionStarted(izaro, elevator.X, elevator.Y, elevator.X, elevator.Y, emerge, 0xffff, player.MonsterOrderFlags),
                    .. InstanceProtocol.ActionEnded(izaro), .. IzaroGoddessEmerges(mechanics, elevator.X, elevator.Y, player)],
                    $"0x01ab Izaro 0x{izaro:x} rises on the elevator{(mechanics.Goddess != 0 ? ", the Goddess of Justice on his back" : "")}");
            await IzaroSays(player, izaro, IzaroFights.StartLine(trial));
            RiseIzaroMechanics(player, mechanics, izaro, boss, Here);
            foreach (var (id, door) in doors)
            {
                await Send(player, [.. InstanceEntities.TransitionableUpdate(id, IzaroFights.TransitionableIndex, IzaroFights.DoorShut),
                    .. InstanceEntities.BlockageUpdate(id, true)], $"0x02bc Izaro's arena door 0x{id:x} at ({door.X}, {door.Y}) shuts");
                CloseIzaroDoor(player, id, door);
            }
            await AreaDelay(player, IzaroFights.RiseSeconds);
            if (!Here()) return;
            await Send(player, [.. LiftState(player, lift, elevator, IzaroFights.Raised),
                .. InstanceEntities.TransitionableUpdate(izaro, transitionable, IzaroFights.Fighting)], $"0x02bc Izaro 0x{izaro:x} fights");
            global::Game.SetStats(player, izaro, [(Stat.HideMiniLifeBar, 0)]);
            (boss.BusyUntil, boss.Aggro, boss.EntranceGrace, boss.BossBarShown, boss.ScriptHeld) = (AreaTime(player), true, false, true, false);
            BossBars(player).Add(izaro);
            await ShowBossLifeBar(izaro, boss, player);
            await StartIzaroMechanics(player, mechanics, izaro, boss, Here);
            _ = IzaroSkills.Run(player, izaro, boss, Here);
            // The fight, until he dies (the last trial) or his life reaches the trial's end (the others).
            while (Here())
            {
                bool dead, retired, leaves = false;
                int action = 0;
                double free = 0;
                dead = boss.Dead || !ReferenceEquals(player.Area.Monsters.GetValueOrDefault(izaro), boss);
                retired = boss.NoRewards;   // taken down for another entry's fight (LeftoverIzaro)
                if (!dead && boss.LifePhases is { } gate && gate.Begin(boss.Life, boss.MaxLife))
                {
                    leaves = true;
                    free = boss.BusyUntil;   // the end of his swing, if one is on
                    (boss.BusyUntil, boss.Aggro, boss.ScriptHeld) = (double.PositiveInfinity, false, true);
                    boss.SwingGeneration = null;
                    action = ++boss.ActionGeneration;
                }
                if (dead)
                {
                    // Gone some other way: the trial is not won. A retired fight's mechanics stop with it.
                    if (retired) mechanics.Over = true;
                    if (!trial.Last || retired) return;
                    Console.WriteLine($"[gateway] {player.Connection.Remote} {what}: Izaro 0x{izaro:x} dies");
                    string? finish = await EndIzaroMechanics(player);
                    _ = IzaroGoddessFliesUp(player, mechanics);
                    await IzaroSays(player, 0, IzaroFights.EndLine(trial));
                    _ = IzaroSaysLater(player, finish, Here);
                    await WinIzaroTrial(player, trial, doors);
                    return;
                }
                if (leaves)
                {
                    string? finish = await EndIzaroMechanics(player);
                    _ = IzaroSaysLater(player, finish, Here);
                    await IzaroLeaves(player, trial, elevator, lift, doors, izaro, boss, action, free, transitionable, Here);
                    return;
                }
                await AreaDelay(player, 0.2);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!player.Closed) Console.WriteLine($"[gateway] {player.Connection.Remote} {what} stopped: {ServerLog.Describe(ex)}"); }
    }

    // A trial before the last ends: his line, his swing played out, the walk to the elevator's middle, a stand, then down on
    // the elevator while the doors open; he is gone when it is down.
    static async Task IzaroLeaves(Player player, IzaroFights.Trial trial,
        AreaPopulation.RoomObject elevator, uint lift, List<(uint Id, AreaPopulation.RoomObject Door)> doors, uint izaro, SpawnedMonster boss,
        int action, double free, byte transitionable, Func<bool> here)
    {
        Console.WriteLine($"[gateway] {player.Connection.Remote} Izaro's trial {trial.Number}: Izaro 0x{izaro:x} at {boss.Life}/{boss.MaxLife} life leaves");
        await IzaroSays(player, izaro, IzaroFights.EndLine(trial));
        double said = AreaTime(player);
        // An order during his swing is lost on the client (live: he stood where he was): wait for its end.
        double swing = Math.Min(free, said + IzaroFights.SwingWaitSeconds) - said;
        if (swing > 0) await AreaDelay(player, swing + 0.1);
        if (!here()) return;
        double arrives = AreaTime(player);
        await Send(player, InstanceProtocol.ActionEnded(izaro), null);
        await SendMonsterAction(player, izaro, boss, action, () =>
        {
            double at = AreaTime(player);
            var from = boss.Walk.Position(at);
            boss.Walk.Order(elevator.X, elevator.Y, at);
            arrives = boss.Walk.ArrivesAt;
            return InstanceProtocol.ActionStarted(izaro, from.X, from.Y, elevator.X, elevator.Y, IzaroFights.MoveSkill, player.NextActionSequence(), player.MonsterOrderFlags);
        }, $"0x01ab Izaro 0x{izaro:x} walks to the elevator's middle ({elevator.X}, {elevator.Y})");
        // On the elevator, he stands a moment (and the line plays out) before it lowers.
        double wait = Math.Max(arrives + IzaroFights.StandSeconds, said + IzaroFights.LineSeconds) - AreaTime(player);
        if (wait > 0) await AreaDelay(player, Math.Min(wait, 15));
        if (!here()) return;
        await Send(player, [.. InstanceEntities.TransitionableUpdate(izaro, transitionable, IzaroFights.Defeated),
            .. InstanceProtocol.AnimationUpdate(izaro, 0, IzaroFights.LowerClip(trial.Weapon), 1),
            .. LiftState(player, lift, elevator, IzaroFights.Lowering)],
            $"0x02bc Izaro 0x{izaro:x} goes down, the elevator lowers");
        await WinIzaroTrial(player, trial, doors);
        await AreaDelay(player, IzaroFights.LowerSeconds);
        boss.Dead = true;
        if (ReferenceEquals(player.Area.Monsters.GetValueOrDefault(izaro), boss)) player.Area.Monsters.Remove(izaro);
        byte[] goddess = IzaroArenas.TryGetValue(player, out var arena) ? IzaroGoddessGone(player, arena) : [];
        if (!here()) return;
        await Send(player, [.. LiftState(player, lift, elevator, IzaroFights.Down), .. goddess,
            .. InstanceProtocol.RemoveObject(izaro)], $"0x02bd Izaro 0x{izaro:x} is gone");
    }

    // The trial won: the instance keeps it, the doors open, the ways out show, the run's map loses his icon there, and the
    // last trial sets the tier's flags.
    static async Task WinIzaroTrial(Player player, IzaroFights.Trial trial,
        List<(uint Id, AreaPopulation.RoomObject Door)> doors)
    {
        if (!player.Instance.SetState(IzaroFights.Beaten)) return;
        foreach (var (id, door) in doors)
        {
            await Send(player, [.. InstanceEntities.TransitionableUpdate(id, IzaroFights.TransitionableIndex, IzaroFights.DoorOpen),
                .. InstanceEntities.BlockageUpdate(id, false)], $"0x02bc Izaro's arena door 0x{id:x} at ({door.X}, {door.Y}) opens");
            OpenIzaroDoor(player, id);
        }
        await UpdateShownTransitions(player, "Izaro's trial is won");
        if (player.Character.Name is { Length: > 0 } character && LabyrinthDay.IzaroBeaten(character, trial.Number) is { } progress)
            await Send(player, progress, $"0x01cb labyrinth progress: Izaro's trial {trial.Number} won");
        if (trial.Last) await SetQuestFlags(player, IzaroFights.CompleteFlags(trial.Tier));
    }
}
