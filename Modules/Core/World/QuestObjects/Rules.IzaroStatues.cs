using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;

static partial class QuestobjectsRules
{
    // Izaro's Toggle Statues (IzaroStatues): one on each small elevator, lowered, until he fights; then they rise one at a
    // time and a click turns one off for a while.
    sealed class StatueSlot(IzaroEffects.Effect statue, uint id, (uint Id, AreaPopulation.RoomObject At) lift)
    {
        public readonly IzaroEffects.Effect Statue = statue;
        public readonly uint Id = id, Lift = lift.Id;
        public readonly AreaPopulation.RoomObject At = lift.At;
        public byte State = IzaroStatues.Lowered;
        public ushort Buff;
        public int Generation;
        // Still counts as on when the trial ends: on, or not risen yet (the wiki).
        public bool Carried => State is IzaroStatues.On or IzaroStatues.Lowered or IzaroStatues.Raising;
    }

    static async Task PlaceIzaroStatues(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena)
    {
        var statues = IzaroEffects.Of(IzaroStatues.Mechanic, arena.Trial.Tier, returning: false);
        for (int i = 0; i < Math.Min(statues.Count, arena.Lifts.Count); i++)
        {
            var (statue, lift) = (statues[i], arena.Lifts[i]);
            uint id = conn.NextObjectId++;
            await Send(remote, stream, ciphers, [.. InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(statue.Type),
                IzaroStatues.Blob(statue, lift.At.X, lift.At.Y, lift.At.Angle, IzaroStatues.Lowered)), .. InstanceProtocol.EnterWakeZone(id)],
                $"0x02b9 {statue.Id} 0x{id:x} under ({lift.At.X}, {lift.At.Y})");
            arena.Statues.Add(new StatueSlot(statue, id, lift));
            lock (conn.Monsters) conn.TrialObjects[id] = lift.At with { Type = statue.Type };
        }
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
            UpdateIzaroStats(arena);
        }
        List<byte> bytes = [.. InstanceEntities.TransitionableUpdate(slot.Id, IzaroStatues.StatueTransitionable, on ? IzaroStatues.On : IzaroStatues.Off)];
        if (on) bytes.AddRange(InstanceEntities.AddBuff(arena.Izaro, buff, slot.Statue.Buff, slot.Id));
        else if (old != 0) bytes.AddRange(InstanceEntities.RemoveBuff(arena.Izaro, old));
        await Send(remote, stream, ciphers, [.. bytes], $"0x02ba {slot.Statue.Id} 0x{slot.Id:x} {(on ? "on" : "off")}");
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

    // The trial is over: the risen statues go down with their elevators.
    static async Task LowerIzaroStatues(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, List<StatueSlot> risen)
    {
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
