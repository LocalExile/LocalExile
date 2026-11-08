using System.Net.Sockets;
using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;

static partial class QuestobjectsRules
{
    // The mechanics of Izaro's arena on a connection (IzaroStatues, IzaroGargoyles): the small elevators, the alcoves'
    // railings, the arena's own mechanic and what the run's earlier trials left (IzaroCarried), with Izaro once he fights.
    // Kept per connection (a module reload drops it).
    sealed class IzaroArena(IzaroFights.Trial trial)
    {
        public readonly IzaroFights.Trial Trial = trial;
        public uint Izaro;
        public SpawnedMonster? Boss;
        public bool Over, AllGargoylesDeadSaid, AllPortalsDeadSaid;
        // Guardians a portal has begun to bring and that are not created yet (IzaroPortals.SummonLimit counts them).
        public int GuardiansComing;
        public readonly List<(uint Id, AreaPopulation.RoomObject At)> Lifts = [], Railings = [];
        public readonly List<StatueSlot> Statues = [];
        public readonly List<GargoyleSlot> Gargoyles = [];
        public readonly List<PortalSlot> Portals = [];
        // The statues an earlier trial left on: their "…2" effects, with the buff on Izaro.
        public readonly List<(IzaroEffects.Effect Effect, ushort Buff)> Returned = [];
    }

    static readonly ConditionalWeakTable<ClientState, IzaroArena> IzaroArenas = new();

    // The arena's small elevators (lowered), its mechanic on them, and what the run's earlier trials left: in the arena's
    // alcoves (by the trial that left it: the first alcove from the top of the map for the first trial) and on the ledges
    // of the room before it (the right one, largest x, for the first trial: the original, live).
    static async Task<IzaroArena> PlaceIzaroMechanics(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        IzaroFights.Trial trial, List<AreaPopulation.RoomObject> all, List<(uint Id, AreaPopulation.RoomObject At)> railings, bool beaten)
    {
        var arena = new IzaroArena(trial);
        arena.Railings.AddRange(railings);
        IzaroArenas.AddOrUpdate(conn, arena);
        foreach (var at in all.Where(o => o.Type == IzaroStatues.SmallElevator))
        {
            uint lift = conn.NextObjectId++;
            await Send(remote, stream, ciphers, [.. InstanceEntities.CreateObject(lift, InstanceEntities.ObjectTypeHash(at.Type),
                IzaroFights.Blob(at.Type, at.Model, at.X, at.Y, at.Angle, IzaroFights.Lowered)), .. InstanceProtocol.EnterWakeZone(lift)],
                $"0x02bb small elevator 0x{lift:x} at ({at.X}, {at.Y})");
            arena.Lifts.Add((lift, at));
        }
        if (!beaten && IzaroEffects.InArena(trial, IzaroStatues.Mechanic)) await PlaceIzaroStatues(remote, stream, ciphers, conn, arena);
        if (!beaten && IzaroEffects.InArena(trial, IzaroGargoyles.Mechanic)) await PlaceIzaroGargoyles(remote, stream, ciphers, conn, arena);
        if (!beaten && IzaroEffects.InArena(trial, IzaroPortals.Mechanic)) await PlaceIzaroPortals(remote, stream, ciphers, conn, arena);
        await PlaceIzaroCarried(remote, stream, ciphers, conn, arena, all, beaten);
        return arena;
    }

    // What the run's earlier trials left, where the room brings it back (its "…2" secret).
    static async Task PlaceIzaroCarried(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena,
        List<AreaPopulation.RoomObject> all, bool beaten)
    {
        if (conn.Character is not { Length: > 0 } character) return;
        var carried = LabyrinthDay.Carried(character).Where(c => c.Trial < arena.Trial.Number).ToList();
        if (carried.Count == 0) return;
        // The alcoves: each railing with the markers nearest it, top of the map (least y) first.
        var markers = all.Where(o => o.Type == IzaroFights.Marker).ToList();
        var alcoves = arena.Railings.OrderBy(r => r.At.Y).Select(r => (Railing: r.Id, Markers: markers
            .Where(m => arena.Railings.MinBy(o => Distance(o.At, m)).Id == r.Id).ToList())).ToList();
        // The ledges: the room's entry markers in threes, right (largest x) first.
        var ledges = new List<List<AreaPopulation.RoomObject>>();
        foreach (var m in all.Where(o => o.Type == IzaroFights.LedgeMarker))
            if (ledges.FirstOrDefault(l => Distance(l[0], m) <= 40) is { } near) near.Add(m);
            else ledges.Add([m]);
        ledges = [.. ledges.OrderByDescending(l => l.Average(m => m.X))];
        int placed = 0;
        foreach (var (id, from) in carried.OrderBy(c => c.Trial))
        {
            if (IzaroEffects.ReturningOf(id) is not { } effect || !IzaroEffects.Returns(arena.Trial, effect.Mechanic)) continue;
            var alcove = beaten ? default : alcoves.ElementAtOrDefault(from - 1);
            var inAlcove = alcove.Markers?.ElementAtOrDefault(effect.Element);
            var onLedge = ledges.ElementAtOrDefault(from - 1)?.ElementAtOrDefault(effect.Element);
            if (effect.Mechanic == IzaroStatues.Mechanic)
            {
                foreach (var at in new[] { inAlcove, onLedge }.OfType<AreaPopulation.RoomObject>())
                {
                    uint doodad = conn.NextObjectId++;
                    await Send(remote, stream, ciphers, [.. InstanceEntities.CreateObject(doodad, InstanceEntities.ObjectTypeHash(effect.Type),
                        IzaroStatues.Blob(effect, at.X, at.Y, at.Angle, IzaroStatues.DoodadOn)), .. InstanceProtocol.EnterWakeZone(doodad)],
                        $"0x02bb {effect.Id} 0x{doodad:x} at ({at.X}, {at.Y}), left by trial {from}");
                }
                if (inAlcove is not null) arena.Returned.Add((effect, 0));
            }
            else if (effect.Mechanic == IzaroGargoyles.Mechanic)
            {
                if (inAlcove is { } at && await SpawnIzaroGargoyle(remote, stream, ciphers, conn, effect, at, id, alcove.Railing) is { } slot)
                    arena.Gargoyles.Add(slot);
                if (onLedge is { } ledge) await SpawnIzaroGargoyle(remote, stream, ciphers, conn, effect, ledge, id, null);
            }
            else if (effect.Mechanic == IzaroPortals.Mechanic)
            {
                if (inAlcove is { } at && await SpawnIzaroPortal(remote, stream, ciphers, conn, effect, at) is { } slot)
                {
                    (slot.Carried, slot.Railing) = (id, alcove.Railing);
                    arena.Portals.Add(slot);
                }
                if (onLedge is { } ledge) await SpawnIzaroPortal(remote, stream, ciphers, conn, effect, ledge);
            }
            placed++;
        }
        if (placed > 0) Console.WriteLine($"[gateway] {remote} Izaro's trial {arena.Trial.Number}: {placed} thing(s) the earlier trials left");
    }

    static double Distance(AreaPopulation.RoomObject a, AreaPopulation.RoomObject b) =>
        Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));

    // Izaro rises: the arena's gargoyles rise with him, and its first statue or portal (the original, live).
    static void RiseIzaroMechanics(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena,
        uint izaro, SpawnedMonster boss, Func<bool> here)
    {
        arena.Izaro = izaro;
        arena.Boss = boss;
        if (arena.Gargoyles.Any(g => g.Carried is null)) _ = RaiseIzaroGargoyles(remote, stream, ciphers, conn, arena, here);
        if (arena.Statues.Count > 0) _ = RaiseIzaroStatues(remote, stream, ciphers, conn, arena, here);
        if (arena.Portals.Any(p => p.Carried is null)) _ = RaiseIzaroPortals(remote, stream, ciphers, conn, arena, here);
    }

    // Izaro fights: the earlier trials' statues' buffs go on him, their gargoyles come out and their portals open up.
    static async Task StartIzaroMechanics(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena,
        uint izaro, SpawnedMonster boss, Func<bool> here)
    {
        arena.Izaro = izaro;
        arena.Boss = boss;
        for (int i = 0; i < arena.Returned.Count; i++)
        {
            ushort buff;
            lock (conn.Monsters) buff = conn.NextBuff++;
            arena.Returned[i] = (arena.Returned[i].Effect, buff);
            await Send(remote, stream, ciphers, InstanceEntities.AddBuff(izaro, buff, arena.Returned[i].Effect.Buff, izaro),
                $"0x01c1 Izaro 0x{izaro:x} has {arena.Returned[i].Effect.Id}");
        }
        lock (conn.Monsters) UpdateIzaroStats(arena);
        await ReleaseIzaroGargoyles(remote, stream, ciphers, conn, arena, here);
        await ReleaseIzaroPortals(remote, stream, ciphers, conn, arena, here);
        if (arena.Gargoyles.Count > 0) _ = WatchIzaroGargoyles(remote, stream, ciphers, conn, arena, here);
    }

    // What is on Izaro as his encounter stats: the statues on and the earlier trials', the gargoyles giving their buff.
    // Under the Monsters lock.
    static void UpdateIzaroStats(IzaroArena arena)
    {
        if (arena.Boss is not { } boss) return;
        var on = arena.Statues.Where(s => s.State == IzaroStatues.On).Select(s => s.Statue)
            .Concat(arena.Returned.Select(r => r.Effect))
            .Concat(arena.Gargoyles.Where(g => g.Buff != 0).Select(g => g.Effect))
            .Concat(arena.Portals.Where(p => p.Buff != 0).Select(p => p.Effect)).ToList();
        boss.EncounterStats = on.Count == 0 ? null : IzaroEffects.Sum(on);
    }

    // The trial ends: what of the arena's mechanic is still on stays on Izaro for the run's later trials (the earlier
    // trials' gargoyles killed here are gone), the risen statues go down with their elevators. His line after the end
    // line: the arena's mechanic's (IzaroFinish…), or null.
    static async Task<string?> EndIzaroMechanics(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (!IzaroArenas.TryGetValue(conn, out var arena)) return null;
        string? finish = null;
        List<StatueSlot> risen;
        lock (conn.Monsters)
        {
            if (arena.Over) return null;
            arena.Over = true;
            var trial = arena.Trial;
            var statues = arena.Statues.Where(s => s.Carried).Select(s => s.Statue).ToList();
            var gargoyles = arena.Gargoyles.Where(g => g.Carried is null && !GargoyleDead(conn, g)).Select(g => g.Effect).ToList();
            var killed = arena.Gargoyles.Where(g => g.Carried is not null && GargoyleDead(conn, g)).Select(g => g.Carried!).ToList();
            var portals = arena.Portals.Where(p => p.Carried is null && !PortalDead(conn, p)).Select(p => p.Effect).ToList();
            var left = statues.Concat(gargoyles).Concat(portals).Select(e => e.Id).ToList();
            if (conn.Character is { Length: > 0 } character)
            {
                if (!trial.Last && left.Count > 0) LabyrinthDay.Carry(character, trial.Number, left);
                if (killed.Count > 0) LabyrinthDay.Uncarry(character, killed);
            }
            if (left.Count > 0) Console.WriteLine($"[gateway] {remote} Izaro's trial {trial.Number} left on him: {string.Join(", ", left)}");
            if (arena.Statues.Count > 0) finish = IzaroStatues.FinishLine(statues);
            else if (arena.Gargoyles.Any(g => g.Carried is null)) finish = IzaroGargoyles.FinishLine(gargoyles.Count);
            else if (arena.Portals.Any(p => p.Carried is null)) finish = IzaroPortals.FinishLine(portals.Count);
            risen = [.. arena.Statues.Where(s => s.State is IzaroStatues.On or IzaroStatues.Off)];
            foreach (var s in risen) s.State = IzaroStatues.Lowering;
        }
        await LowerIzaroStatues(remote, stream, ciphers, conn, risen);
        await EndIzaroPortals(remote, stream, ciphers, conn, arena);
        return finish;
    }
}
