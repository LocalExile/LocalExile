using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Diagnostics;

static partial class QuestobjectsRules
{
    // The Goddess's Guardians (IzaroGoddess): from when Izaro is hurt until the trial ends, circles open at random places of
    // the arena, each with its guardians, which die by themselves.
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
                foreach (var spot in spots)
                {
                    uint effect = conn.NextObjectId++;
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
