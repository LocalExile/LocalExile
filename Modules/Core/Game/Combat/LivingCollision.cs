using System.Net.Sockets;
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
                }
                PublishPlayerPose(conn);
                if ((frames++ & 1) == 0) await SyncInstancePlayers(remote, stream, ciphers, conn);
                await Task.Delay(50);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        finally { conn.Instance.RemovePlayer(conn.PeerId); }
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
                    // The beach wake-up is the owner's alone: observers get no action to end that stance with.
                    var player = peer.Template with { GridX = peer.X, GridY = peer.Y, Life = peer.Life,
                        Skills = [], QuestFlags = [], LeagueQuestFlags = [], WashedUp = false, Rotation = 0 };
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

    // The client steps every walker a cell at a time and checks each step (0x141e32710): the cell's walk value must be at
    // least the walker's size + 1, and no other body may be within reach (0x141e47a40 → 0x141e47ca0: the Chebyshev distance
    // less that body's size below the walker's size + 1). A blocked step ends the walk where it stands, without a new path
    // (callback 0x1401d81f4, not the segment handler: live 2026-11-01). A walk whose path began on a blocked cell steps
    // through bodies until its first free step (WalksThrough); terrain still stops it. A path cut short by the search is
    // walked to its end and then paths again from there at once (0x141e7bec0 → 0x141e7b920: 213 of 213 live). The player's
    // walk is the same code. The client moves nobody for any other reason, so each pass replays the steps taken since the
    // last one, the player's and every monster's in time order, each against the cells the others hold at that moment.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ClientState, System.Runtime.CompilerServices.StrongBox<double>> SteppedUntil = new();
    const uint PlayerKey = 0;

    internal static Task CorrectMonsterCollisions(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        using (ProjectileMutation(conn, phase: "collision"))
        {
            double now = ProjectileWorldTime(conn);
            var stepped = SteppedUntil.GetValue(conn, _ => new(now));
            double from = Math.Min(stepped.Value, now);
            stepped.Value = now;
            var monsters = new Dictionary<uint, SpawnedMonster>();
            var cells = new Dictionary<uint, (int X, int Y)>();
            var sizes = new Dictionary<uint, int>();
            foreach (var (id, m) in conn.Monsters)
            {
                if (!LivingCollision.Blocks(m)) { m.CollisionPosition = null; continue; }
                monsters[id] = m;
                sizes[id] = LivingCollision.Size(m);
                cells[id] = m.Walk.Position(from);
            }
            var player = conn.Walk;
            bool playerWalks = conn.PlayerSent && conn.PlayerLife > 0;
            if (playerWalks)
            {
                sizes[PlayerKey] = player.Size;
                cells[PlayerKey] = player.Position(from);
            }
            var others = new List<ClientPathfinder.Blocker>(conn.StationaryBlockers);
            foreach (var peer in conn.Instance.PlayersExcept(conn.PeerId))
                if (peer.Life > 0) others.Add(new(peer.X, peer.Y, LivingCollision.PlayerSize));
            bool Blocked(uint self, (int X, int Y) cell)
            {
                var bodies = new List<ClientPathfinder.Blocker>(others);
                foreach (var (id, at) in cells)
                    // Phasing (Quartz Flask): the player and the monsters pass each other.
                    if (id != self && !(conn.Phasing && (id == PlayerKey || self == PlayerKey))) bodies.Add(new(at.X, at.Y, sizes[id]));
                return ClientPathfinder.ObjectBlocks(bodies, cell.X, cell.Y, sizes[self] + 1, sizes[self] + 4);
            }
            var pending = cells.Keys.ToDictionary(id => id, _ => from);
            for (int round = 0; round < 8 && pending.Count > 0; round++)
            {
                var steps = new List<(double At, uint Id, (int X, int Y) Cell)>();
                foreach (var (id, since) in pending)
                    foreach (var (at, cell) in id == PlayerKey ? player.Steps(since, now) : monsters[id].Walk.Steps(since, now))
                        steps.Add((at, id, cell));
                steps.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.Id.CompareTo(b.Id));
                var stopped = new HashSet<uint>();
                foreach (var (at, id, cell) in steps)
                {
                    if (stopped.Contains(id)) continue;
                    bool isPlayer = id == PlayerKey;
                    bool floor = isPlayer ? player.Floor(cell.X, cell.Y)
                        : conn.Layout.WalkableFor(cell.X, cell.Y, sizes[id]) && !conn.Barrier.Contains(cell);
                    // The player's bounded escape through estimated bodies (PlayerWalk.Walk) is not checked against them.
                    bool body = floor && !(isPlayer && player.EscapingCrowd(at)) && Blocked(id, cell);
                    bool through = isPlayer ? player.WalksThrough : monsters[id].Walk.WalksThrough;
                    if (!floor || body && !through)
                    {
                        if (isPlayer) player.StopAt(at);
                        else monsters[id].Walk.Teleport(cells[id].X, cells[id].Y, at);
                        stopped.Add(id);
                        continue;
                    }
                    if (!body)
                    {
                        if (isPlayer) player.WalksThrough = false;
                        else monsters[id].Walk.WalksThrough = false;
                    }
                    cells[id] = cell;
                }
                pending.Clear();
                foreach (var id in cells.Keys)
                {
                    if (stopped.Contains(id)) continue;
                    if (id == PlayerKey)
                    {
                        if (player.Reached || player.ArrivesAt > now) continue;
                        double at = player.ArrivesAt;
                        player.Repath(at);
                        pending[id] = at;
                    }
                    else
                    {
                        var walk = monsters[id].Walk;
                        if (walk.Reached || walk.ArrivesAt > now) continue;
                        double at = walk.ArrivesAt;
                        walk.Order(walk.Target.X, walk.Target.Y, at);
                        pending[id] = at;
                    }
                }
            }
            foreach (var (id, m) in monsters) m.CollisionPosition = m.Walk.Position(now);
        }
        return Task.CompletedTask;
    }
}
