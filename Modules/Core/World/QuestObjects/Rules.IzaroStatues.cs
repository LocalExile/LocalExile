using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;

static partial class QuestobjectsRules
{
    // Izaro's Toggle Statues on a connection (IzaroStatues): the arena's small elevators, its statues and the doodads of
    // the statues an earlier trial left on, with Izaro once he fights. Kept per connection (a module reload drops it).
    sealed class IzaroArena
    {
        public uint Izaro;
        public SpawnedMonster? Boss;
        public bool Over;
        public readonly List<StatueSlot> Statues = [];
        public readonly List<(IzaroStatues.Statue Statue, uint Buff)> Returned = [];
    }

    sealed class StatueSlot(IzaroStatues.Statue statue, uint id, uint lift, AreaPopulation.RoomObject at)
    {
        public readonly IzaroStatues.Statue Statue = statue;
        public readonly uint Id = id, Lift = lift;
        public readonly AreaPopulation.RoomObject At = at;
        public byte State = IzaroStatues.Lowered;
        public ushort Buff;
        public int Generation;
        // Still counts as on when the trial ends: on, or not risen yet (the wiki).
        public bool Carried => State is IzaroStatues.On or IzaroStatues.Lowered or IzaroStatues.Raising;
    }

    static readonly ConditionalWeakTable<ClientState, IzaroArena> IzaroArenas = new();

    // The arena's small elevators, its statues (lowered) and the doodads of the statues the run's earlier trials left on.
    static async Task<IzaroArena> PlaceIzaroStatues(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        IzaroFights.Trial trial, List<AreaPopulation.RoomObject> all, bool beaten)
    {
        var arena = new IzaroArena();
        IzaroArenas.AddOrUpdate(conn, arena);
        var lifts = all.Where(o => o.Type == IzaroStatues.SmallElevator).ToList();
        var statues = !beaten && IzaroStatues.InArena(trial) ? IzaroStatues.For(trial.Tier, returning: false) : [];
        for (int i = 0; i < lifts.Count; i++)
        {
            var at = lifts[i];
            uint lift = conn.NextObjectId++;
            await Send(remote, stream, ciphers, [.. InstanceEntities.CreateObject(lift, InstanceEntities.ObjectTypeHash(at.Type),
                IzaroFights.Blob(at.Type, at.Model, at.X, at.Y, at.Angle, IzaroFights.Lowered)), .. InstanceProtocol.EnterWakeZone(lift)],
                $"0x02b9 small elevator 0x{lift:x} at ({at.X}, {at.Y})");
            if (i >= statues.Count) continue;
            var statue = statues[i];
            uint id = conn.NextObjectId++;
            await Send(remote, stream, ciphers, [.. InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(statue.Type),
                IzaroStatues.Blob(statue, at.X, at.Y, at.Angle, IzaroStatues.Lowered)), .. InstanceProtocol.EnterWakeZone(id)],
                $"0x02b9 {statue.Id} 0x{id:x} under ({at.X}, {at.Y})");
            arena.Statues.Add(new StatueSlot(statue, id, lift, at));
            lock (conn.Monsters) conn.TrialObjects[id] = at with { Type = statue.Type };
        }
        // An earlier trial's statues still on stand in the alcove (its markers, by element) and stay on.
        if (!beaten && IzaroStatues.Returns(trial) && conn.Character is { Length: > 0 } character)
        {
            var markers = all.Where(o => o.Type == IzaroStatues.Marker).ToList();
            foreach (var statue in LabyrinthDay.Carried(character).Select(IzaroStatues.Returning).OfType<IzaroStatues.Statue>().OrderBy(s => s.Element))
            {
                if (statue.Element >= markers.Count) continue;
                var at = markers[statue.Element];
                uint id = conn.NextObjectId++;
                await Send(remote, stream, ciphers, [.. InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(statue.Type),
                    IzaroStatues.Blob(statue, at.X, at.Y, at.Angle, IzaroStatues.DoodadOn)), .. InstanceProtocol.EnterWakeZone(id)],
                    $"0x02b9 {statue.Id} 0x{id:x} in the alcove at ({at.X}, {at.Y}), on");
                arena.Returned.Add((statue, 0));
            }
        }
        if (arena.Statues.Count > 0 || arena.Returned.Count > 0)
            Console.WriteLine($"[gateway] {remote} Izaro's statues: {arena.Statues.Count} to rise, {arena.Returned.Count} back from earlier trials");
        return arena;
    }

    // Izaro fights: the earlier trials' statues' buffs go on him, and this arena's statues rise one at a time.
    static async Task StartIzaroStatues(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena,
        uint izaro, SpawnedMonster boss, Func<bool> here)
    {
        arena.Izaro = izaro;
        arena.Boss = boss;
        for (int i = 0; i < arena.Returned.Count; i++)
        {
            ushort buff;
            lock (conn.Monsters) buff = conn.NextBuff++;
            arena.Returned[i] = (arena.Returned[i].Statue, buff);
            await Send(remote, stream, ciphers, InstanceEntities.AddBuff(izaro, buff, arena.Returned[i].Statue.Buff, izaro),
                $"0x01c1 Izaro 0x{izaro:x} has {arena.Returned[i].Statue.Id}");
        }
        UpdateIzaroStatueStats(conn, arena);
        if (arena.Statues.Count == 0) return;
        _ = RaiseIzaroStatues(remote, stream, ciphers, conn, arena, here);
    }

    static async Task RaiseIzaroStatues(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena,
        Func<bool> here)
    {
        try
        {
            var order = arena.Statues.OrderBy(_ => Random.Shared.Next()).ToList();
            for (int k = 0; k < order.Count; k++)
            {
                if (k > 0) await Task.Delay(TimeSpan.FromSeconds(IzaroStatues.RiseInterval));
                var slot = order[k];
                lock (conn.Monsters)
                {
                    if (arena.Over || !here()) return;
                    slot.State = IzaroStatues.Raising;
                }
                await Send(remote, stream, ciphers, [.. LiftState(conn, slot.Lift, slot.At, IzaroFights.Raising),
                    .. InstanceEntities.TransitionableUpdate(slot.Id, IzaroStatues.StatueTransitionable, IzaroStatues.Raising)],
                    $"0x02ba {slot.Statue.Id} 0x{slot.Id:x} rises");
                _ = StatueRisen(remote, stream, ciphers, conn, arena, slot, here);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] {remote} Izaro's statues stopped: {ServerLog.Describe(ex)}"); }
    }

    static async Task StatueRisen(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena,
        StatueSlot slot, Func<bool> here)
    {
        await Task.Delay(TimeSpan.FromSeconds(IzaroStatues.RiseSeconds));
        lock (conn.Monsters) if (arena.Over || !here() || slot.State != IzaroStatues.Raising) return;
        await Send(remote, stream, ciphers, LiftState(conn, slot.Lift, slot.At, IzaroFights.Raised),
            $"0x02ba small elevator 0x{slot.Lift:x} up");
        await TurnIzaroStatue(remote, stream, ciphers, conn, arena, slot, on: true);
    }

    // A statue on (its buff on Izaro) or off.
    static async Task TurnIzaroStatue(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena,
        StatueSlot slot, bool on)
    {
        ushort buff = 0, old;
        lock (conn.Monsters)
        {
            if (arena.Over) return;
            slot.State = on ? IzaroStatues.On : IzaroStatues.Off;
            slot.Generation++;
            old = slot.Buff;
            if (on) slot.Buff = buff = conn.NextBuff++;
            else slot.Buff = 0;
            UpdateIzaroStatueStats(conn, arena);
        }
        List<byte> bytes = [.. InstanceEntities.TransitionableUpdate(slot.Id, IzaroStatues.StatueTransitionable, on ? IzaroStatues.On : IzaroStatues.Off)];
        if (on) bytes.AddRange(InstanceEntities.AddBuff(arena.Izaro, buff, slot.Statue.Buff, slot.Id));
        else if (old != 0) bytes.AddRange(InstanceEntities.RemoveBuff(arena.Izaro, old));
        await Send(remote, stream, ciphers, [.. bytes], $"0x02ba {slot.Statue.Id} 0x{slot.Id:x} {(on ? "on" : "off")}");
    }

    // The statues on and the earlier trials' as Izaro's encounter stats. Under the Monsters lock.
    static void UpdateIzaroStatueStats(ClientState conn, IzaroArena arena)
    {
        if (arena.Boss is not { } boss) return;
        var on = arena.Statues.Where(s => s.State == IzaroStatues.On).Select(s => s.Statue).Concat(arena.Returned.Select(r => r.Statue)).ToList();
        boss.EncounterStats = on.Count == 0 ? null : IzaroStatues.Sum(on);
    }

    // A click on a statue (UseTrialObject): on, it goes off for IzaroStatues.OffSeconds, then on again while the trial lasts.
    internal static async Task UseIzaroStatue(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id)
    {
        if (!IzaroArenas.TryGetValue(conn, out var arena) || arena.Statues.FirstOrDefault(s => s.Id == id) is not { } slot) return;
        if (slot.State != IzaroStatues.On) return;
        await TurnIzaroStatue(remote, stream, ciphers, conn, arena, slot, on: false);
        int generation = slot.Generation;
        var layout = conn.Layout;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(IzaroStatues.OffSeconds));
            if (conn.Closed || conn.LeavingArea || !ReferenceEquals(conn.Layout, layout) || slot.Generation != generation || arena.Over) return;
            try { await TurnIzaroStatue(remote, stream, ciphers, conn, arena, slot, on: true); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        });
    }

    // The trial ends: what is still on (or not risen) stays on Izaro for the run's later trials, and the risen statues go
    // down with their elevators.
    static async Task EndIzaroStatues(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroFights.Trial trial)
    {
        if (!IzaroArenas.TryGetValue(conn, out var arena)) return;
        List<StatueSlot> risen;
        lock (conn.Monsters)
        {
            if (arena.Over) return;
            arena.Over = true;
            var carried = arena.Statues.Where(s => s.Carried).Select(s => s.Statue.Id).ToList();
            if (!trial.Last && carried.Count > 0 && conn.Character is { Length: > 0 } character) LabyrinthDay.Carry(character, carried);
            if (carried.Count > 0) Console.WriteLine($"[gateway] {remote} Izaro's statues left on: {string.Join(", ", carried)}");
            risen = [.. arena.Statues.Where(s => s.State is IzaroStatues.On or IzaroStatues.Off)];
            foreach (var s in risen) s.State = IzaroStatues.Lowering;
        }
        foreach (var s in risen)
            await Send(remote, stream, ciphers, [.. InstanceEntities.TransitionableUpdate(s.Id, IzaroStatues.StatueTransitionable, IzaroStatues.Lowering),
                .. LiftState(conn, s.Lift, s.At, IzaroFights.Lowering)], $"0x02ba {s.Statue.Id} 0x{s.Id:x} goes down");
        if (risen.Count == 0) return;
        var layout = conn.Layout;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(IzaroStatues.LowerSeconds));
            if (conn.Closed || conn.LeavingArea || !ReferenceEquals(conn.Layout, layout)) return;
            try
            {
                foreach (var s in risen)
                    await Send(remote, stream, ciphers, [.. InstanceEntities.TransitionableUpdate(s.Id, IzaroStatues.StatueTransitionable, IzaroStatues.Down),
                        .. LiftState(conn, s.Lift, s.At, IzaroFights.Down)], null);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        });
    }
}
