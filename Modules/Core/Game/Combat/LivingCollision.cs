using System.Net.Sockets;
using GameServer.Core.Diagnostics;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

static partial class Game
{
    internal static async Task RunLivingCollisions(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        int frames = 0;
        try
        {
            while (!conn.Closed && conn.PendingTransfer is null)
            {
                if (!conn.Paused)
                {
                    await CorrectMonsterCollisions(remote, stream, ciphers, conn);
                    await PushOutOfCollision(remote, stream, ciphers, conn, living: true);
                    await ShowFreeze(remote, stream, ciphers, conn);
                    await ReleaseHeldMove(remote, stream, ciphers, conn);
                    await KeepInSync(remote, stream, ciphers, conn);
                }
                PublishPlayerPose(conn);
                if (ServerTrace.Sync) TraceSync(conn);
                if ((frames++ & 1) == 0) await SyncInstancePlayers(remote, stream, ciphers, conn);
                await Task.Delay(50);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        finally { conn.Instance.RemovePlayer(conn.PeerId); }
    }

    // The client does not stop for is_frozen (live 2026-11-01), and virtual_action_speed_+% at -100, which would stop it,
    // divides its action clips by zero. A hit's recovery for as long as the freeze lasts stops it where it stands and lets
    // it go on with its walk after, as the server's walk does.
    internal static async Task ShowFreeze(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (conn.FreezeShown == conn.FreezeGeneration) return;
            conn.FreezeShown = conn.FreezeGeneration;
        }
        await Send(remote, stream, ciphers, [], "0x01d1 player frozen", atGate: () =>
        {
            lock (conn.Monsters)
            {
                double now = conn.Clock.Elapsed.TotalSeconds, left = conn.ElementalAilments.FreezeUntil(now) - now;
                if (left <= 0 || conn.PlayerLife == 0 || conn.LeavingArea || conn.Paused) return null;
                return [.. InstanceProtocol.ActorAnimation(PlayerId, (float)left, PlayerHitRecovery.TakeHitAnimation),
                    .. ResyncPacketLocked(conn, now)];
            }
        });
    }

    // A click that came during a hit's recovery or a freeze: the walk starts when that ends, as the client's does.
    internal static async Task ReleaseHeldMove(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        InstanceProtocol.UseSkillAtLocation use;
        (int X, int Y) from;
        int action;
        using (ProjectileMutation(conn))
        {
            if (conn.HeldMove is not { } held) return;
            double now = ProjectileWorldTime(conn);
            if (PlayerActionBlocked(conn, now, now)) return;
            conn.HeldMove = null;
            if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) return;
            use = held;
            action = Interlocked.Increment(ref conn.ActionCount);
            from = conn.Walk.Order(use.X, use.Y, Math.Max(conn.HitRecovery.Until, now - MonsterAiCadence.StepSeconds));
            conn.ChannelSkill = 0;
        }
        await Send(remote, stream, ciphers, InstanceProtocol.ActionStarted(PlayerId, from.X, from.Y, use.X, use.Y, use.SkillId, use.Slot, use.Flags),
            $"0x01ab held move to ({use.X}, {use.Y}) from ({from.X}, {from.Y})", still: () => PlayerActionCurrent(conn, action));
    }

    // Game_TRACE=sync: where the server has the player and the monsters now, on the QPC clock of
    // scripts/re/frida/trace-sync.js. scripts/re/sync_report.py compares the two.
    internal static void TraceSync(ClientState conn)
    {
        if (!conn.PlayerSent) return;
        var line = new System.Text.StringBuilder();
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            var p = conn.Walk.Position(now);
            line.Append($"[sync] qpc={System.Diagnostics.Stopwatch.GetTimestamp()} peer={conn.PeerId:x} speed={conn.Walk.Speed:0.###} ");
            line.Append($"moving={(conn.Walk.Moving(now) ? 1 : 0)} p={p.X},{p.Y} m=");
            foreach (var (id, m) in conn.Monsters)
            {
                if (m.Dead || m.Life <= 0) continue;
                var at = m.Walk.Position(now);
                line.Append($"{id:x}:{at.X},{at.Y};");
            }
        }
        Console.WriteLine(line);
    }

    internal static void PublishPlayerPose(ClientState conn)
    {
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea) { conn.Instance.RemovePlayer(conn.PeerId); return; }
            if (conn.PeerTemplate is null || !conn.PlayerSent) return;
            RefreshPeerScale(conn, CharacterPlayerStats(conn)[ActorScale.Final]);
        }
    }

    // Called under the owner's lock. Replace only final scale in an immutable template, retaining creation stats,
    // equipment and skills. Observers read published snapshots and never lock another connection's simulation.
    internal static void RefreshPeerScale(ClientState conn, int scale)
    {
        if (conn.PeerTemplate is not { } template) return;
        if ((template.Stats ?? []).SingleOrDefault(s => s.Key == ActorScale.Final).Value != scale)
            conn.PeerTemplate = template = template with
            { Stats = [.. (template.Stats ?? []).Where(s => s.Key != ActorScale.Final), (ActorScale.Final, scale)] };
        if (!conn.PlayerSent || conn.Closed || conn.LeavingArea || conn.Instance is null) return;
        var p = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        conn.Instance.PublishPlayer(new(conn.PeerId, conn.PeerType, template, p.X, p.Y, conn.PlayerLife, conn));
    }

    internal static async Task SyncInstancePlayers(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var ids = conn.Instance.PlayersExcept(conn.PeerId).Select(p => p.Id)
            .Concat(conn.SeenPlayers.Keys).Distinct().ToArray();
        foreach (uint id in ids)
            await Send(remote, stream, ciphers, [], null, atGate: () =>
            {
                if (conn.Closed || conn.LeavingArea) return null;
                var packets = new List<byte[]>();
                // Read after waiting: queued creation/change must not restore an older scale or departed owner.
                if (conn.Instance.Player(id) is not { } peer)
                {
                    if (conn.SeenPlayers.Remove(id)) packets.Add(InstanceProtocol.RemoveObject(id));
                    return packets.SelectMany(p => p).ToArray();
                }
                bool exists = conn.SeenPlayers.TryGetValue(peer.Id, out var previous);
                if (!exists || previous!.Life == 0 && peer.Life > 0)
                {
                    if (exists) packets.Add(InstanceProtocol.RemoveObject(peer.Id));
                    var player = peer.Template with { GridX = peer.X, GridY = peer.Y, Life = peer.Life,
                        Skills = [], QuestFlags = [], LeagueQuestFlags = [] };
                    packets.Add(InstanceEntities.CreateObject(peer.Id, peer.Type, player.Blob()));
                    packets.Add(InstanceProtocol.EnterWakeZone(peer.Id));
                }
                else
                {
                    if ((previous!.X, previous.Y) != (peer.X, peer.Y))
                        packets.Add(Math.Max(Math.Abs(previous.X - peer.X), Math.Abs(previous.Y - peer.Y)) > 20
                            ? InstanceProtocol.Teleport(peer.Id, peer.X, peer.Y, 0x7fffffff)
                            : InstanceProtocol.MoveTo(peer.Id, peer.X, peer.Y, .1f, 0x7fffffff));
                    if (previous.Life != peer.Life)
                        packets.Add(InstanceProtocol.LifeUpdate(peer.Id, peer.Life));
                    int scale = (peer.Template.Stats ?? []).SingleOrDefault(s => s.Key == ActorScale.Final).Value;
                    int before = (previous.Template.Stats ?? []).SingleOrDefault(s => s.Key == ActorScale.Final).Value;
                    if (scale != before)
                        packets.Add(InstanceEntities.StatsUpdate(peer.Id, [(ActorScale.Final, scale)]));
                }
                conn.SeenPlayers[peer.Id] = peer;
                return packets.SelectMany(p => p).ToArray();
            });
    }

    internal static Task CorrectMonsterCollisions(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        using (ProjectileMutation(conn, phase: "collision"))
        {
            double now = ProjectileWorldTime(conn);
            // Every living body's cell and size once per frame (kept current as bodies are moved below), instead of
            // recomputing each other monster's position for every monster: the overlap test was O(n²) walk queries.
            using var frame = conn.BeginMonsterBlockerFrame(now);
            var bodies = frame.Bodies;
            int index = 0;
            foreach (var (id, m) in conn.Monsters)
            {
                if (!LivingCollision.Blocks(m)) { m.CollisionPosition = null; continue; }
                int self = index++;
                var current = (bodies[self].X, bodies[self].Y);
                var previous = m.CollisionPosition ??= current;
                if (current == previous) continue;
                int size = bodies[self].Size;
                var blockers = conn.MonsterBlockers(id);
                bool Floor(int x, int y) => conn.Layout.WalkableFor(x, y, size) && !conn.Barrier.Contains((x, y));
                // The client stops a walker at the first cell a body blocks (0x141e32710, 0x141e47a40) and nothing ever
                // moves a monster that stands. One that started inside a body walks out of it.
                var stop = ClientPathfinder.ObjectBlocks(blockers, previous.X, previous.Y, size + 1, size + 4) ? current
                    : LivingCollision.Sweep(previous, current, size, Floor, blockers);
                if (stop != current)
                {
                    m.Walk.Teleport(stop.X, stop.Y, now);
                    frame.Move(id, stop.X, stop.Y);
                    // A chaser's next order paths it again; a Move's client does so itself when its walk ends short.
                    if (m.LastTarget is { } target && m is not { Hostile: true, Attack.Ranged: false }) m.Walk.Order(target.X, target.Y, now);
                }
                m.CollisionPosition = stop;
            }
        }
        return Task.CompletedTask;
    }
}
