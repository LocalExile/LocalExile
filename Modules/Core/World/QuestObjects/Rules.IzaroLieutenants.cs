using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;

static partial class QuestobjectsRules
{
    // A lieutenant of Izaro's (IzaroLieutenants): the arena's own, on a small elevator (Lift), or one an earlier trial left
    // (Carried: the effect it was left as), in an alcove behind Railing. It does nothing until it is up (or comes out of the
    // alcove); a returning one's buff is on Izaro while Buff is set.
    sealed class LieutenantSlot(IzaroEffects.Effect effect, uint id, SpawnedMonster monster, byte transitionable)
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

    // LieutenantSkeletonBoss*.ot's Transitionable: 2 raising, 3 raised and active. Its on_transition_to_2 is the server's:
    // the server plays the rise (Art/Models/MONSTERS/skeleton/rig.amd: 175 lift_raise_bow, 176 lift_raise_sword_shield,
    // both 10.7 s), as the gargoyles'.
    const byte LieutenantRaising = 2, LieutenantRaised = 3;
    const uint LieutenantBowRaiseClip = 175, LieutenantRaiseClip = 176;

    static uint LieutenantRaise(LieutenantSlot l) =>
        l.Effect.Variety.Contains("Ranged", StringComparison.Ordinal) ? LieutenantBowRaiseClip : LieutenantRaiseClip;

    static bool LieutenantDead(ClientState conn, LieutenantSlot l) =>
        l.Monster.Dead || l.Monster.Life == 0 || !ReferenceEquals(conn.Monsters.GetValueOrDefault(l.Id), l.Monster);

    // The arena's lieutenants, one on each small elevator, down in its hole.
    static async Task PlaceIzaroLieutenants(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena)
    {
        var lieutenants = IzaroEffects.Of(IzaroLieutenants.Mechanic, arena.Trial.Tier, returning: false);
        for (int i = 0; i < Math.Min(lieutenants.Count, arena.Lifts.Count); i++)
            if (await SpawnIzaroLieutenant(remote, stream, ciphers, conn, lieutenants[i], FacingCentre(arena, arena.Lifts[i].At)) is { } slot)
            {
                slot.Lift = arena.Lifts[i];
                arena.Lieutenants.Add(slot);
            }
    }

    // Out of the AI and taking no hits until it is up (what rises can be hit only once it has risen, live). Not kept with
    // the instance (the arena places it again).
    static async Task<LieutenantSlot?> SpawnIzaroLieutenant(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        IzaroEffects.Effect effect, AreaPopulation.RoomObject at)
    {
        if (Monsters.Find(effect.Variety) is not { Supported: true } variety)
        {
            Console.WriteLine($"[gateway] {remote} Izaro's lieutenant {effect.Id}: {effect.Variety} cannot be created");
            return null;
        }
        byte level = (byte)Math.Clamp(ServerLevels.AreaLevel(conn.Layout.Id), 1, 100);
        // With its weapons in its Inventories (as Izaro's: without them it swung empty-handed, live 2026-11-11).
        uint id = await SpawnMonster(remote, stream, ciphers, conn, variety, level, MonsterRarity.Unique, at.X, at.Y, rotation: at.Angle,
            placement: MonsterPlacement.Established, ownedLooks: variety.Weapons);
        lock (conn.Monsters)
        {
            if (conn.Monsters.GetValueOrDefault(id) is not { } m) return null;
            (m.BusyUntil, m.Aggro, m.EntranceGrace, m.SummonerId) = (double.PositiveInfinity, false, true, 0);
            return new LieutenantSlot(effect, id, m, (byte)variety.ComponentsOrBase.ToList().IndexOf("Transitionable"));
        }
    }

    // It fights: out of its rise (seconds), into the AI, with its skills (IzaroSkills) between the AI's swings.
    static void WakeIzaroLieutenant(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, LieutenantSlot l,
        double seconds, Func<bool> here)
    {
        lock (conn.Monsters)
        {
            if (LieutenantDead(conn, l)) return;
            (l.Monster.BusyUntil, l.Monster.Aggro, l.Monster.EntranceGrace) = (conn.Clock.Elapsed.TotalSeconds + seconds, true, false);
        }
        _ = IzaroSkills.Run(remote, stream, ciphers, conn, l.Id, l.Monster, here);
    }

    // The arena's lieutenants rise one at a time, the first with Izaro (the original, live; the interval is the user's choice).
    static async Task RaiseIzaroLieutenants(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena,
        Func<bool> here)
    {
        try
        {
            var order = arena.Lieutenants.Where(l => l.Lift is not null).OrderBy(_ => Random.Shared.Next()).ToList();
            for (int k = 0; k < order.Count; k++)
            {
                if (k > 0) await Task.Delay(TimeSpan.FromSeconds(IzaroLieutenants.RiseInterval));
                var l = order[k];
                lock (conn.Monsters) if (arena.Over || !here()) return;
                await Send(remote, stream, ciphers, [.. LiftState(conn, l.Lift!.Value.Id, l.Lift.Value.At, IzaroFights.Raising),
                    .. InstanceEntities.TransitionableUpdate(l.Id, l.Transitionable, LieutenantRaising),
                    .. InstanceProtocol.AnimationUpdate(l.Id, 0, LieutenantRaise(l), 1)], $"0x02bc {l.Effect.Id} 0x{l.Id:x} rises");
                _ = IzaroLieutenantRisen(remote, stream, ciphers, conn, l, here);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] {remote} Izaro's lieutenants stopped: {ServerLog.Describe(ex)}"); }
    }

    // Up, it fights, even once Izaro has left (as the gargoyles).
    static async Task IzaroLieutenantRisen(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, LieutenantSlot l, Func<bool> here)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(IzaroStatues.RiseSeconds));
            if (!here()) return;
            await Send(remote, stream, ciphers, [.. LiftState(conn, l.Lift!.Value.Id, l.Lift.Value.At, IzaroFights.Raised),
                .. InstanceEntities.TransitionableUpdate(l.Id, l.Transitionable, LieutenantRaised)], $"0x02bc {l.Effect.Id} 0x{l.Id:x} up");
            WakeIzaroLieutenant(remote, stream, ciphers, conn, l, 0, here);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] {remote} Izaro's lieutenant {l.Effect.Id} stopped: {ServerLog.Describe(ex)}"); }
    }

    // The earlier trials' lieutenants: their alcoves' railings lower, their buffs go on Izaro and they come out.
    static async Task ReleaseIzaroLieutenants(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena,
        Func<bool> here)
    {
        var carried = arena.Lieutenants.Where(l => l.Carried is not null).ToList();
        foreach (uint railing in carried.Select(l => l.Railing).OfType<uint>().Distinct())
            await LowerIzaroRailing(remote, stream, ciphers, conn, railing);
        foreach (var l in carried)
        {
            ushort buff;
            lock (conn.Monsters)
            {
                if (LieutenantDead(conn, l)) continue;
                l.Buff = buff = conn.NextBuff++;
                UpdateIzaroStats(arena);
            }
            await Send(remote, stream, ciphers, InstanceEntities.AddBuff(arena.Izaro, buff, l.Effect.Buff, l.Id), $"0x01c1 Izaro 0x{arena.Izaro:x} has {l.Effect.Id}");
            WakeIzaroLieutenant(remote, stream, ciphers, conn, l, IzaroFights.RailingSeconds, here);
        }
    }

    // While he fights: a returning lieutenant killed takes its buff off Izaro; when the last of the arena's own dies he says so.
    static async Task WatchIzaroLieutenants(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena,
        Func<bool> here)
    {
        try
        {
            bool said = false;
            while (here())
            {
                List<(LieutenantSlot L, ushort Buff)> gone = [];
                bool allDead = false;
                lock (conn.Monsters)
                {
                    if (arena.Over) return;
                    foreach (var l in arena.Lieutenants.Where(l => l.Buff != 0 && LieutenantDead(conn, l)))
                    {
                        gone.Add((l, l.Buff));
                        l.Buff = 0;
                    }
                    if (gone.Count > 0) UpdateIzaroStats(arena);
                    var own = arena.Lieutenants.Where(l => l.Carried is null).ToList();
                    if (!said && own.Count > 0 && own.All(l => LieutenantDead(conn, l))) allDead = said = true;
                }
                foreach (var (l, buff) in gone)
                    await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(arena.Izaro, buff),
                        $"0x01c2 Izaro 0x{arena.Izaro:x} loses {l.Effect.Id}: lieutenant 0x{l.Id:x} is dead");
                if (allDead) await IzaroSays(remote, stream, ciphers, arena.Izaro, IzaroLieutenants.AllDeadLine);
                await Task.Delay(200);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] {remote} Izaro's lieutenants' watch stopped: {ServerLog.Describe(ex)}"); }
    }
}
