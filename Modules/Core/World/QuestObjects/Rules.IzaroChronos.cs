using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;

static partial class QuestobjectsRules
{
    // An idol of Izaro's Chrono Statues (IzaroChronos): the arena's own, on a small elevator (Lift), or one an earlier trial
    // left (Carried: the effect it was left as), in an alcove. It is out of the AI; IzaroIdols runs it while Up.
    sealed class IdolSlot(IzaroEffects.Effect effect, uint id, SpawnedMonster monster, byte transitionable)
    {
        public readonly IzaroEffects.Effect Effect = effect;
        public readonly uint Id = id;
        public readonly SpawnedMonster Monster = monster;
        public readonly byte Transitionable = transitionable;
        public (uint Id, AreaPopulation.RoomObject At)? Lift;
        public string? Carried;
        public ushort Buff;
        public bool Up;
    }

    // The arena's idols, one on each small elevator, down in its hole.
    static async Task PlaceIzaroIdols(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena)
    {
        var idols = IzaroEffects.Of(IzaroChronos.Mechanic, arena.Trial.Tier, returning: false);
        for (int i = 0; i < Math.Min(idols.Count, arena.Lifts.Count); i++)
            if (await SpawnIzaroIdol(remote, stream, ciphers, conn, idols[i], FacingCentre(arena, arena.Lifts[i].At)) is { } slot)
            {
                slot.Lift = arena.Lifts[i];
                arena.Idols.Add(slot);
            }
    }

    // It takes no hits until it is up (what rises can be hit only once it has risen, live) and cannot die: its life stops
    // at the gate's 1 % (set_cannot_die). A returning one takes none (IzaroChronosStatuePhase2.ot: base_cannot_be_damaged).
    // Not kept with the instance (the arena places it again).
    static async Task<IdolSlot?> SpawnIzaroIdol(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        IzaroEffects.Effect effect, AreaPopulation.RoomObject at)
    {
        if (Monsters.Find(effect.Variety) is not { Supported: true } variety)
        {
            Console.WriteLine($"[gateway] {remote} Izaro's idol {effect.Id}: {effect.Variety} cannot be created");
            return null;
        }
        byte level = (byte)Math.Clamp(ServerLevels.AreaLevel(conn.Layout.Id), 1, 100);
        uint id = await SpawnMonster(remote, stream, ciphers, conn, variety, level, MonsterRarity.Normal, at.X, at.Y, rotation: at.Angle,
            scripted: true, placement: MonsterPlacement.Established);
        IdolSlot slot;
        lock (conn.Monsters)
        {
            if (conn.Monsters.GetValueOrDefault(id) is not { } m) return null;
            (m.BusyUntil, m.Aggro, m.EntranceGrace, m.SummonerId, m.NoRewards) = (double.PositiveInfinity, false, true, 0, true);
            m.StunImmuneUntil = double.PositiveInfinity;
            m.LifePhases = new([1]);
            slot = new IdolSlot(effect, id, m, (byte)variety.ComponentsOrBase.ToList().IndexOf("Transitionable"));
        }
        if (effect.Returning)
            await Send(remote, stream, ciphers, InstanceEntities.TransitionableUpdate(id, slot.Transitionable, IzaroChronos.ReturnedStanding),
                $"0x02bc {effect.Id} 0x{id:x} stands at ({at.X}, {at.Y})");
        return slot;
    }

    // The arena's idols rise together with Izaro (the original, live); up, each fills and casts.
    static async Task RaiseIzaroIdols(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena,
        Func<bool> here)
    {
        try
        {
            var own = arena.Idols.Where(i => i.Lift is not null).ToList();
            List<byte> bytes = [];
            lock (conn.Monsters)
            {
                if (arena.Over || !here()) return;
                foreach (var i in own)
                    bytes.AddRange([.. LiftState(conn, i.Lift!.Value.Id, i.Lift.Value.At, IzaroFights.Raising),
                        .. InstanceEntities.TransitionableUpdate(i.Id, i.Transitionable, IzaroChronos.Rising),
                        .. InstanceProtocol.AnimationUpdate(i.Id, 0, IzaroChronos.LiftRaiseClip, 1)]);
            }
            await Send(remote, stream, ciphers, [.. bytes], $"0x02bc Izaro's {own.Count} idols rise");
            await Task.Delay(TimeSpan.FromSeconds(IzaroStatues.RiseSeconds));
            bytes = [];
            lock (conn.Monsters)
            {
                if (arena.Over || !here()) return;
                foreach (var i in own)
                {
                    (i.Monster.EntranceGrace, i.Up) = (false, true);
                    bytes.AddRange([.. LiftState(conn, i.Lift!.Value.Id, i.Lift.Value.At, IzaroFights.Raised),
                        .. InstanceEntities.TransitionableUpdate(i.Id, i.Transitionable, IzaroChronos.Raised)]);
                }
            }
            await Send(remote, stream, ciphers, [.. bytes], "0x02bc Izaro's idols up");
            foreach (var i in own) RunIzaroIdol(remote, stream, ciphers, conn, arena, i, IzaroChronos.FillSeconds, here);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] {remote} Izaro's idols stopped: {ServerLog.Describe(ex)}"); }
    }

    static void RunIzaroIdol(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena, IdolSlot i,
        double fillSeconds, Func<bool> here)
    {
        bool Up() { lock (conn.Monsters) return here() && !arena.Over && i.Up; }
        _ = IzaroIdols.Run(remote, stream, ciphers, conn, i.Id, i.Monster, i.Effect.Element, fillSeconds, Up);
    }

    // The earlier trials' idols in the alcoves: their buffs go on Izaro and they cast their circles (the returning ones
    // fill at their own rate).
    static async Task ReleaseIzaroIdols(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena,
        Func<bool> here)
    {
        foreach (var i in arena.Idols.Where(i => i.Carried is not null))
        {
            ushort buff;
            lock (conn.Monsters)
            {
                i.Buff = buff = conn.NextBuff++;
                i.Up = true;
                UpdateIzaroStats(arena);
            }
            await Send(remote, stream, ciphers, InstanceEntities.AddBuff(arena.Izaro, buff, i.Effect.Buff, i.Id), $"0x01c1 Izaro 0x{arena.Izaro:x} has {i.Effect.Id}");
            RunIzaroIdol(remote, stream, ciphers, conn, arena, i, IzaroChronos.ReturnedFillSeconds, here);
        }
    }

    // Whether an idol is left on him as the trial ends (ours: while it glows). Under the Monsters lock.
    static bool IdolLeft(IdolSlot i) => i.Up && i.Monster.MaxLife > 0 && i.Monster.Life * 100L > i.Monster.MaxLife * (long)IzaroChronos.LeftAbovePercent;

    // He leaves: the arena's idols stop and go down with their elevators (the original, live).
    static async Task LowerIzaroIdols(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, List<IdolSlot> risen)
    {
        if (risen.Count == 0) return;
        List<byte> bytes = [];
        lock (conn.Monsters)
            foreach (var i in risen)
            {
                i.Up = false;
                i.Monster.ActionGeneration++;
                bytes.AddRange([.. InstanceProtocol.ActionEnded(i.Id), .. InstanceEntities.TransitionableUpdate(i.Id, i.Transitionable, IzaroChronos.Lowering),
                    .. InstanceProtocol.AnimationUpdate(i.Id, 0, IzaroChronos.LiftLowerClip, 1), .. LiftState(conn, i.Lift!.Value.Id, i.Lift.Value.At, IzaroFights.Lowering)]);
            }
        await Send(remote, stream, ciphers, [.. bytes], $"0x02bc Izaro's {risen.Count} idols go down");
        var layout = conn.Layout;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(IzaroStatues.LowerSeconds));
            if (conn.Closed || conn.LeavingArea || !ReferenceEquals(conn.Layout, layout)) return;
            try
            {
                await Send(remote, stream, ciphers, [.. risen.SelectMany(i => (byte[])[.. InstanceEntities.TransitionableUpdate(i.Id, i.Transitionable, IzaroChronos.Lowered),
                    .. LiftState(conn, i.Lift!.Value.Id, i.Lift.Value.At, IzaroFights.Down)])], null);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        });
    }
}
