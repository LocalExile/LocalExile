using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;

static partial class QuestobjectsRules
{
    // A portal of Izaro's (IzaroPortals): the arena's own, on a small elevator (Lift), or one an earlier trial left (Carried:
    // the effect it was left as), in an alcove behind Railing. It is out of the AI: RunIzaroPortal brings its guardians.
    sealed class PortalSlot(IzaroEffects.Effect effect, uint id, SpawnedMonster monster, byte transitionable)
    {
        public readonly IzaroEffects.Effect Effect = effect;
        public readonly uint Id = id;
        public readonly SpawnedMonster Monster = monster;
        public readonly byte Transitionable = transitionable;
        public (uint Id, AreaPopulation.RoomObject At)? Lift;
        public string? Carried;
        public uint? Railing;
        public ushort Buff;
    }

    static bool PortalDead(ClientState conn, PortalSlot p) =>
        p.Monster.Dead || p.Monster.Life == 0 || !ReferenceEquals(conn.Monsters.GetValueOrDefault(p.Id), p.Monster);

    // The arena's portals, one on each small elevator, down in its hole (a new one is in IzaroPortal.ot's state 5).
    static async Task PlaceIzaroPortals(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena)
    {
        var portals = IzaroEffects.Of(IzaroPortals.Mechanic, arena.Trial.Tier, returning: false);
        for (int i = 0; i < Math.Min(portals.Count, arena.Lifts.Count); i++)
            if (await SpawnIzaroPortal(remote, stream, ciphers, conn, portals[i], FacingCentre(arena, arena.Lifts[i].At)) is { } slot)
            {
                slot.Lift = arena.Lifts[i];
                arena.Portals.Add(slot);
            }
    }

    // It takes no hits until it is up (the original, live: what rises can be hit only when it has risen); a returning one
    // never does (IzaroPortal2.ot: base_cannot_be_damaged). Not kept with the instance (the arena places it again).
    static async Task<PortalSlot?> SpawnIzaroPortal(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        IzaroEffects.Effect effect, AreaPopulation.RoomObject at)
    {
        if (Monsters.Find(effect.Variety) is not { Supported: true } variety)
        {
            Console.WriteLine($"[gateway] {remote} Izaro's portal {effect.Id}: {effect.Variety} cannot be created");
            return null;
        }
        byte level = (byte)Math.Clamp(ServerLevels.AreaLevel(conn.Layout.Id), 1, 100);
        uint id = await SpawnMonster(remote, stream, ciphers, conn, variety, level, MonsterRarity.Normal, at.X, at.Y, rotation: at.Angle,
            scripted: true, placement: MonsterPlacement.Established);
        PortalSlot slot;
        lock (conn.Monsters)
        {
            if (conn.Monsters.GetValueOrDefault(id) is not { } m) return null;
            (m.EntranceGrace, m.SummonerId, m.NoRewards) = (true, 0, true);
            slot = new PortalSlot(effect, id, m, (byte)variety.ComponentsOrBase.ToList().IndexOf("Transitionable"));
        }
        if (effect.Returning)
            await Send(remote, stream, ciphers, [.. InstanceEntities.TransitionableUpdate(id, slot.Transitionable, IzaroPortals.ReturnedOpen),
                .. InstanceProtocol.AnimationUpdate(id, 0, IzaroPortals.ReturnedIdleClip, 1)], $"0x02bc {effect.Id} 0x{id:x} stands open at ({at.X}, {at.Y})");
        return slot;
    }

    // The arena's portals rise one at a time, the first with Izaro (the original, live).
    static async Task RaiseIzaroPortals(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena,
        Func<bool> here)
    {
        try
        {
            var order = arena.Portals.Where(p => p.Lift is not null).OrderBy(_ => Random.Shared.Next()).ToList();
            for (int k = 0; k < order.Count; k++)
            {
                if (k > 0) await Task.Delay(TimeSpan.FromSeconds(IzaroPortals.RiseInterval));
                var p = order[k];
                lock (conn.Monsters) if (arena.Over || !here()) return;
                await Send(remote, stream, ciphers, [.. LiftState(conn, p.Lift!.Value.Id, p.Lift.Value.At, IzaroFights.Raising),
                    .. InstanceEntities.TransitionableUpdate(p.Id, p.Transitionable, IzaroPortals.Raising),
                    .. InstanceProtocol.AnimationUpdate(p.Id, 0, IzaroPortals.AscendClip, 1)], $"0x02bc {p.Effect.Id} 0x{p.Id:x} rises");
                _ = IzaroPortalRisen(remote, stream, ciphers, conn, arena, p, here);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] {remote} Izaro's portals stopped: {ServerLog.Describe(ex)}"); }
    }

    // Up, it can be hit and, while the trial lasts, brings its guardians.
    static async Task IzaroPortalRisen(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena,
        PortalSlot p, Func<bool> here)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(IzaroStatues.RiseSeconds));
            if (!here()) return;
            lock (conn.Monsters) p.Monster.EntranceGrace = false;
            await Send(remote, stream, ciphers, [.. LiftState(conn, p.Lift!.Value.Id, p.Lift.Value.At, IzaroFights.Raised),
                .. InstanceEntities.TransitionableUpdate(p.Id, p.Transitionable, IzaroPortals.Raised)], $"0x02bc {p.Effect.Id} 0x{p.Id:x} up");
            await RunIzaroPortal(remote, stream, ciphers, conn, arena, p, here);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] {remote} Izaro's portal {p.Effect.Id} stopped: {ServerLog.Describe(ex)}"); }
    }

    // A portal's guardians, until it is destroyed or the trial ends (it then stays, bringing no more, and those it brought
    // fight on: the original, live), while fewer than IzaroPortals.SummonLimit of its own live. Its action plays nothing on
    // the client (as the Immolator's), so its actor's clip is played.
    static async Task RunIzaroPortal(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena,
        PortalSlot p, Func<bool> here)
    {
        if (p.Monster.Attack?.Summon is not { } summon || Monsters.Find(summon.Variety.ToString()) is not { Supported: true } guardian)
        {
            Console.WriteLine($"[gateway] {remote} Izaro's portal {p.Effect.Id} has no guardian it can bring");
            return;
        }
        double every = p.Effect.Returning ? summon.CooldownSeconds : IzaroPortals.SummonSeconds;
        bool Stopped() { lock (conn.Monsters) return arena.Over || !here() || PortalDead(conn, p); }
        try
        {
            while (!Stopped())
            {
                int living;
                lock (conn.Monsters) living = conn.Monsters.Values.Count(s => s.SummonerId == p.Id && !s.Dead && s.Life > 0);
                if (living >= IzaroPortals.SummonLimit)
                {
                    await Task.Delay(TimeSpan.FromSeconds(every));
                    continue;
                }
                await Send(remote, stream, ciphers, InstanceProtocol.ActorAnimation(p.Id, (float)summon.CastSeconds, SummonAnimation), null);
                await Task.Delay(TimeSpan.FromSeconds(summon.CastSeconds / 2));
                if (Stopped()) break;
                var (at, to) = IzaroPortalWayOut(conn, arena, p, summon.Offset / WorldUnits.PerCell);
                uint id = await SpawnMonster(remote, stream, ciphers, conn, guardian, p.Monster.Level, MonsterRarity.Normal, at.X, at.Y, log: false,
                    placement: MonsterPlacement.Established, owned: (_, s) =>
                    {
                        (s.SummonerId, s.NoRewards) = (p.Id, summon.NoRewards);
                        s.BusyUntil = double.PositiveInfinity;
                    });
                if (id != 0) await WalkOutOfIzaroPortal(remote, stream, ciphers, conn, id, to);
                Console.WriteLine($"[gateway] {remote} {p.Effect.Id} 0x{p.Id:x} brings {guardian.Name} from ({at.X}, {at.Y}) to ({to.X}, {to.Y}), {living + 1} of its own alive");
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.2, every - summon.CastSeconds / 2)));
            }
            bool allDead;
            lock (conn.Monsters)
            {
                var own = arena.Portals.Where(o => o.Carried is null).ToList();
                allDead = !arena.Over && here() && own.Count > 0 && !arena.AllPortalsDeadSaid && own.All(o => PortalDead(conn, o));
                if (allDead) arena.AllPortalsDeadSaid = true;
            }
            if (allDead) await IzaroSays(remote, stream, ciphers, arena.Izaro, IzaroPortals.AllDeadLine);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] {remote} Izaro's portal {p.Effect.Id} stopped: {ServerLog.Describe(ex)}"); }
    }

    // A guardian comes out of its portal (the original, live): it starts at the portal's edge and walks the skill's offset
    // towards the arena's middle, the way the portal faces, a little to one side so they do not end on one cell.
    static ((int X, int Y) At, (int X, int Y) To) IzaroPortalWayOut(ClientState conn, IzaroArena arena, PortalSlot p, double ahead)
    {
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            var from = p.Monster.Walk.Position(now);
            var towards = arena.Centre is { } centre ? (centre.X, centre.Y) : conn.Walk.Position(now);
            double dx = towards.Item1 - from.X, dy = towards.Item2 - from.Y, length = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            (dx, dy) = (dx / length, dy / length);
            int edge = (p.Monster.Variety?.ObjectSize ?? 3) + 1, aside = Random.Shared.Next(-IzaroPortals.ExitSpread, IzaroPortals.ExitSpread + 1);
            (int X, int Y) at = ((int)Math.Round(from.X + dx * edge), (int)Math.Round(from.Y + dy * edge));
            (int X, int Y) to = ((int)Math.Round(at.X + dx * ahead - dy * aside), (int)Math.Round(at.Y + dy * ahead + dx * aside));
            return (at, conn.Layout.WalkableFor(to.X, to.Y, 0) && !conn.Barrier.Contains(to) ? to : at);
        }
    }

    // Until it arrives it does nothing else; then the AI has it.
    static async Task WalkOutOfIzaroPortal(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, (int X, int Y) to)
    {
        SpawnedMonster? m;
        int action;
        lock (conn.Monsters)
        {
            m = conn.Monsters.GetValueOrDefault(id);
            if (m is null) return;
            action = ++m.ActionGeneration;
        }
        await SendMonsterAction(remote, stream, ciphers, conn, id, m, action, () =>
        {
            double at = conn.Clock.Elapsed.TotalSeconds;
            var from = m.Walk.Position(at);
            m.Walk.Order(to.X, to.Y, at);
            m.BusyUntil = m.Walk.ArrivesAt;
            return InstanceProtocol.ActionStarted(id, from.X, from.Y, to.X, to.Y, IzaroFights.MoveSkill, conn.NextActionSequence(), conn.MonsterOrderFlags);
        });
    }

    // The earlier trials' portals: their alcoves' railings lower, their buffs go on Izaro and they bring their guardians.
    static async Task ReleaseIzaroPortals(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena,
        Func<bool> here)
    {
        var returned = arena.Portals.Where(p => p.Carried is not null).ToList();
        foreach (uint railing in returned.Select(p => p.Railing).OfType<uint>().Distinct())
            await LowerIzaroRailing(remote, stream, ciphers, conn, railing);
        foreach (var p in returned)
        {
            ushort buff;
            lock (conn.Monsters)
            {
                p.Buff = buff = conn.NextBuff++;
                UpdateIzaroStats(arena);
            }
            await Send(remote, stream, ciphers, InstanceEntities.AddBuff(arena.Izaro, buff, p.Effect.Buff, p.Id), $"0x01c1 Izaro 0x{arena.Izaro:x} has {p.Effect.Id}");
            _ = RunIzaroPortal(remote, stream, ciphers, conn, arena, p, here);
        }
    }
}
