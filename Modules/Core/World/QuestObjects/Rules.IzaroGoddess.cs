using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;

static partial class QuestobjectsRules
{
    // The Goddess of Justice (IzaroGoddess) on Izaro's back, from his rise: out of the AI and of every hit for good, no bar,
    // no rewards. Not kept with the instance (the arena places her with him).
    static async Task SpawnIzaroGoddess(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena,
        uint izaro, byte level, (int X, int Y) at)
    {
        if (Monsters.Find(IzaroGoddess.Variety) is not { Supported: true } variety)
        {
            Console.WriteLine($"[gateway] {remote} the Goddess of Justice ({IzaroGoddess.Variety}) cannot be created");
            return;
        }
        uint id = await SpawnMonster(remote, stream, ciphers, conn, variety, level, MonsterRarity.Unique, at.X, at.Y,
            placement: MonsterPlacement.Established, extraStats: [(StatKeys.HideMiniLifeBar, 1)], attachTo: (izaro, IzaroGoddess.Bone));
        lock (conn.Monsters)
        {
            if (conn.Monsters.GetValueOrDefault(id) is not { } goddess) return;
            (goddess.BusyUntil, goddess.Aggro, goddess.EntranceGrace, goddess.NoRewards) = (double.PositiveInfinity, false, true, true);
            (arena.Goddess, arena.GoddessMonster) = (id, goddess);
        }
    }

    // Where the gargoyles' beams end: on her (the original, live), else on him.
    static uint BeamEnd(IzaroArena arena) => arena.Goddess != 0 ? arena.Goddess : arena.Izaro;

    // Izaro has died: she turns slowly and flies up (her death clip, live), and is gone after it.
    static async Task IzaroGoddessFliesUp(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena)
    {
        if (arena.GoddessMonster is not { } goddess) return;
        uint id = arena.Goddess;
        var layout = conn.Layout;
        await Send(remote, stream, ciphers, [.. InstanceProtocol.ActionEnded(id), .. InstanceProtocol.AnimationUpdate(id, 0, IzaroGoddess.DeathClip, 1)],
            $"0x02bc the Goddess of Justice 0x{id:x} flies up");
        await Task.Delay(TimeSpan.FromSeconds(IzaroGoddess.DeathSeconds));
        if (conn.Closed || conn.LeavingArea || !ReferenceEquals(conn.Layout, layout)) return;
        await Send(remote, stream, ciphers, IzaroGoddessGone(conn, arena), $"0x02bd the Goddess of Justice 0x{id:x} is gone");
    }

    // Her removal, with Izaro's when he leaves (she rides down on him).
    static byte[] IzaroGoddessGone(ClientState conn, IzaroArena arena)
    {
        if (arena.GoddessMonster is not { } goddess) return [];
        uint id = arena.Goddess;
        lock (conn.Monsters)
        {
            goddess.Dead = true;
            if (ReferenceEquals(conn.Monsters.GetValueOrDefault(id), goddess)) conn.Monsters.Remove(id);
            (arena.Goddess, arena.GoddessMonster) = (0, null);
        }
        return InstanceProtocol.RemoveObject(id);
    }

    // The Goddess's Guardians (IzaroGoddess): from when Izaro is hurt until the trial ends, her mortars open circles at random
    // places of the arena, each with its guardians, which die by themselves.
    static async Task RunIzaroGuardians(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena,
        Func<bool> here)
    {
        if (Monsters.Find(IzaroGoddess.Guardian) is not { Supported: true } guardian)
        {
            Console.WriteLine($"[gateway] {remote} the Goddess's guardian {IzaroGoddess.Guardian} cannot be created");
            return;
        }
        var layout = conn.Layout;
        try
        {
            while (true)
            {
                lock (conn.Monsters)
                {
                    if (arena.Over || !here() || arena.Boss is not { } boss) return;
                    if (boss.Life * 100L <= boss.MaxLife * (long)IzaroGoddess.HurtPercent) break;
                }
                await Task.Delay(500);
            }
            while (true)
            {
                List<(int X, int Y)> spots;
                lock (conn.Monsters)
                {
                    if (arena.Over || !here()) return;
                    spots = IzaroGuardianSpots(conn, arena);
                }
                // Her mortars fly first, each circle opens where one lands.
                double[] flights = arena.GoddessMonster is { } goddess
                    ? await GoddessOfJustice.ThrowGuardians(remote, stream, ciphers, conn, arena.Goddess, goddess, spots) : new double[spots.Count];
                for (int i = 0; i < spots.Count; i++) _ = OpenIzaroCircle(remote, stream, ciphers, conn, arena, guardian, spots[i], flights[i], layout);
                Console.WriteLine($"[gateway] {remote} the Goddess opens {spots.Count} circle(s): {string.Join(", ", spots)}");
                await Task.Delay(TimeSpan.FromSeconds(IzaroGoddess.EverySeconds));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] {remote} the Goddess's guardians stopped: {ServerLog.Describe(ex)}"); }
    }

    // Random floor of the arena out of the skill's minimum range of Izaro, none on another. Under the Monsters lock.
    static List<(int X, int Y)> IzaroGuardianSpots(ClientState conn, IzaroArena arena)
    {
        var spots = new List<(int X, int Y)>();
        if (arena.Centre is not { } centre || arena.Boss is not { } boss) return spots;
        var from = boss.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        for (int tries = 0; tries < 200 && spots.Count < IzaroGoddess.Circles; tries++)
        {
            (int X, int Y) at = (centre.X + Random.Shared.Next(-IzaroGoddess.ArenaReach, IzaroGoddess.ArenaReach + 1),
                centre.Y + Random.Shared.Next(-IzaroGoddess.ArenaReach, IzaroGoddess.ArenaReach + 1));
            if (!conn.Layout.WalkableFor(at.X, at.Y, 2) || conn.Barrier.Contains(at)) continue;
            if (Math.Sqrt((double)(at.X - from.X) * (at.X - from.X) + (double)(at.Y - from.Y) * (at.Y - from.Y)) < IzaroGoddess.MinimumRange) continue;
            if (spots.Any(s => Math.Abs(s.X - at.X) + Math.Abs(s.Y - at.Y) < 20)) continue;
            spots.Add(at);
        }
        return spots;
    }

    // A circle where her mortar lands, its guardians in it.
    static async Task OpenIzaroCircle(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, IzaroArena arena,
        MonsterCatalog.Variety guardian, (int X, int Y) spot, double after, AreaLayout layout)
    {
        try
        {
            if (after > 0) await Task.Delay(TimeSpan.FromSeconds(after));
            if (conn.Closed || conn.LeavingArea || !ReferenceEquals(conn.Layout, layout)) return;
            uint effect = conn.TakeObjectId();
            await Send(remote, stream, ciphers, [.. InstanceEntities.CreateObject(effect, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                new EffectObject(spot.X, spot.Y, IzaroGoddess.Impact).Blob()), .. InstanceProtocol.EnterWakeZone(effect)], null);
            _ = RemoveIzaroCircle(remote, stream, ciphers, conn, layout, effect);
            for (int k = 0; k < IzaroGoddess.GuardiansInACircle; k++)
            {
                uint id = await SpawnMonster(remote, stream, ciphers, conn, guardian, (byte)Math.Clamp(ServerLevels.AreaLevel(conn.Layout.Id), 1, 100),
                    MonsterRarity.Normal, spot.X, spot.Y, log: false, owned: (_, s) => (s.SummonerId, s.NoRewards) = (arena.Izaro, true));
                if (id != 0) _ = ExpireIzaroGuardian(remote, stream, ciphers, conn, layout, id);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] {remote} the Goddess's circle stopped: {ServerLog.Describe(ex)}"); }
    }

    static async Task RemoveIzaroCircle(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, AreaLayout layout, uint effect)
    {
        await Task.Delay(TimeSpan.FromSeconds(IzaroGoddess.ImpactSeconds));
        if (conn.Closed || conn.LeavingArea || !ReferenceEquals(conn.Layout, layout)) return;
        try { await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(effect), null); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    // IzaroSkeleton.ot's DiesAfterTime, outside its client block, so the server's: it goes when its time is up, and so does
    // the corpse of one killed before that (the user's call: they would pile up in the arena otherwise).
    static async Task ExpireIzaroGuardian(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, AreaLayout layout, uint id)
    {
        await Task.Delay(TimeSpan.FromSeconds(IzaroGoddess.GuardianSeconds));
        if (conn.Closed || conn.LeavingArea || !ReferenceEquals(conn.Layout, layout)) return;
        lock (conn.Monsters)
        {
            if (conn.Monsters.GetValueOrDefault(id) is not { } m) return;
            (m.Life, m.Dead) = (0, true);
            conn.Monsters.Remove(id);
        }
        try { await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), null); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }
}
