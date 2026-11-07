using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

// The instance's monsters are its own (InstanceWorld): every connection in it reads and hits the same objects, under the
// instance's lock, on the instance's clock. One connection's loops step them (the runner: AI, collisions, wake zone)
// until the instance runs them itself; every client is shown them (SyncInstanceMonsters) under the same ids: a client
// that comes later gets them where they are, with the life they have, and whoever leaves takes nothing with them.
static partial class Game
{
    // The instance's monster ids start here (InstanceWorld.NewMonsterId); below it are each client's own objects and the
    // players' minions.
    const uint SharedMonsterIds = 0x08000000;

    // A connection outside any instance (a test's) runs its own monsters; in an instance, the world's runner does.
    internal static bool IsMonsterRunner(ClientState conn) =>
        conn.Instance is not { } instance || instance.World.Runner is null || ReferenceEquals(instance.World.Runner, conn);

    static bool RunnerLive(InstanceWorld world) =>
        world.Runner is { } r && !r.Closed && !r.LeavingArea && InGame.ContainsKey(r);

    // At PopulateArea: this connection runs the monsters when nobody live does; true when the area's packs are still to
    // be placed (the first connection in, or one after a restart, from the instance's save).
    internal static bool ClaimMonsterHost(string remote, ClientState conn)
    {
        var world = conn.Instance.World;
        lock (world.Monsters)
        {
            if (!RunnerLive(world)) BecomeRunnerLocked(remote, conn, world);
            if (!world.Populated)
            {
                world.Populated = true;
                return true;
            }
        }
        Console.WriteLine($"[gateway] {remote} monsters: the instance's own ({world.MonsterOrigins.Count}), run by {CharacterName(world.Runner!)}");
        return false;
    }

    // Under the instance's lock: this connection's loops step the monsters from now on (their ailments and collisions).
    // Their walks get its collision: the last runner's view of the bodies went with it.
    static void BecomeRunnerLocked(string remote, ClientState conn, InstanceWorld world)
    {
        var old = world.Runner;
        world.Runner = conn;
        if (old is null || ReferenceEquals(old, conn)) return;
        double now = conn.Clock.Elapsed.TotalSeconds;
        int count = 0;
        foreach (var (id, m) in world.Monsters.Concat(world.DormantMonsters).OrderBy(e => e.Key))
        {
            if (!world.MonsterOrigins.ContainsKey(id)) continue;
            var at = m.Walk.Position(now);
            m.Walk = conn.NewMonsterWalk(id, at.X, at.Y, m.Walk.Speed, Math.Clamp((m.Variety?.ObjectSize ?? 1) - 1, -1, 127),
                m.Rarity == MonsterRarity.Unique);
            (m.Aggro, m.Leg, m.LastTarget) = (false, null, null);
            count++;
        }
        Console.WriteLine($"[gateway] {remote} monsters: {CharacterName(conn)} runs the instance's {count} now ({CharacterName(old)} left)");
    }

    // Under the instance's lock, in this connection's AI pass: whether it acts for the monster now. A monster with no
    // live target takes this player when in its reach; another player's takes this one only when nearer by
    // TargetMargin and in its reach, so it does not flip between two standing side by side. A dead, gone or other-area
    // target leaves it to whoever is near.
    internal static bool MonsterTargetsLocked(ClientState conn, uint id, SpawnedMonster m, (int X, int Y) at, double d, double now)
    {
        // Only a client that was shown the monster can be sent its actions and component updates (SharedKills.ToOthers).
        if (conn.Instance is not null && m.Target is null && !conn.MonsterViews.ContainsKey(id)) return false;
        var t = m.Target;
        bool live = t is not null && !t.Closed && !t.LeavingArea && t.PlayerLife > 0 && ReferenceEquals(t.Instance, conn.Instance);
        if (live && ReferenceEquals(t, conn))
        {
            if (!m.Aggro && d > m.AggroRange) m.Target = null; // gave up: free for whoever comes
            return true;
        }
        // In its reach, or still chasing (the AI gives up beyond twice its aggro range, as before).
        double reach = Math.Max(1, m.AggroRange) * (m.Aggro && !live ? 2 : 1);
        if (conn.PlayerLife == 0 || d > reach || conn.Instance is not null && !conn.MonsterViews.ContainsKey(id)) return false;
        if (live)
        {
            var there = t!.Walk.Position(now);
            double dt = Math.Sqrt((double)(at.X - there.X) * (at.X - there.X) + (double)(at.Y - there.Y) * (at.Y - there.Y));
            if (d + TargetMargin >= dt) return false;
        }
        // From another player: what it did for that one ends (its next pass orders after this one).
        if (t is not null)
        {
            (m.LastTarget, m.SwingGeneration) = (null, null);
            m.ActionGeneration++;
        }
        m.Target = conn;
        return true;
    }

    const double TargetMargin = 15;

    // Dormant bodies were created and woken too. Keep their views eligible for the rise's component update,
    // without adding them to RunMonsters or giving them an AI action. Under the instance's monster lock.
    static uint[] AwakeMonsterIds(ClientState conn) =>
        [.. conn.Monsters.Concat(conn.DormantMonsters).OrderBy(e => e.Key).Where(e => !e.Value.Dead && conn.MonsterViews.ContainsKey(e.Key)
            && !conn.AsleepMonsters.Contains(e.Key)).Select(e => e.Key)];

    // Every frame: the living monsters in this client's wake zone (sorted) and those its loops act for, for the mirror of
    // their messages to the other clients; and this player's minions, for theirs.
    internal static void PublishAwakeMonsters(ClientState conn)
    {
        var world = conn.Instance.World;
        bool runner = IsMonsterRunner(conn);
        uint[] awake, run;
        lock (world.Monsters)
        {
            // Only the runner's client puts monsters to sleep (KeepWakeZone); the others have every one they were shown.
            awake = AwakeMonsterIds(conn);
            // Its targets' actions; a monster with none (a scripted boss, an idle one) acts on the runner's loops.
            run = [.. world.Monsters.Where(e => world.MonsterOrigins.ContainsKey(e.Key)
                && (ReferenceEquals(e.Value.Target, conn) || runner && e.Value.Target is null)).Select(e => e.Key)];
        }
        conn.AwakeMonsters = awake; // SortedDictionary: already in order
        conn.RunMonsters = run;
        if (conn.Instance.Presence.PlayersExcept(conn.PeerId).Length > 0)
        {
            var minions = new List<SharedMonster>();
            lock (world.Monsters)
            {
                double now = conn.Clock.Elapsed.TotalSeconds;
                foreach (var (id, m) in world.Monsters)
                    if (conn.MinionOrigins.TryGetValue(id, out var origin) && !m.Dead && m.Life > 0)
                    {
                        var at = m.Walk.Position(now);
                        minions.Add(new(id, origin, m, at.X, at.Y, m.Walk.Destination, m.Life));
                    }
            }
            conn.PublishedMinions = [.. minions];
        }
        Program.Outgoing ??= MirrorSharedMonsters;
    }

    // At disconnect: this player's minions leave the instance with them.
    internal static void RemoveLeavingMinionsLocked(ClientState left)
    {
        foreach (uint id in left.MinionOrigins.Keys) left.Instance.World.Monsters.Remove(id);
    }

    // Everything a client is told about a shared monster that only that client would otherwise see goes to every other
    // client in the instance as well (Program.Outgoing, at the send gate): the actions of the monsters this connection
    // runs (0x1ab and 0x1ac: swings, area skills, walks; 0x1d1 clips) and, from any client, what its own hits do to one
    // (0x1b2 and 0x1d1 hit reactions, 0x1c1 / 0x1c2 buffs: ailments, curses, rarity glow; 0x1b3 stats). Buff instances are
    // the object's own (the elemental ailments use the same ones on every monster), so they go as they are. An action's
    // target, the local player there (PlayerId), becomes this player as the others know them (PeerId). A client with the
    // monster out of its wake zone gets nothing: a message wakes a sleeping object (see SyncSharedMonsters).
    static readonly AsyncLocal<bool> Mirroring = new();

    internal static void MirrorSharedMonsters(ConnectionCiphers ciphers, byte[] bytes)
    {
        if (Mirroring.Value || bytes.Length < 12) return;
        ushort message = (ushort)(bytes[0] << 8 | bytes[1]);
        if (message is InstanceProtocol.ActionStartedId or 0x1ac
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(2)) == PlayerId)
        {
            MirrorPlayerAction(ciphers, bytes);
            return;
        }
        if (message == InstanceProtocol.ActionHitId
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(2)) == PlayerId)
        {
            MirrorPlayerHit(ciphers, bytes);
            return;
        }
        if (message is InstanceEntities.CreateObjectId or InstanceProtocol.EnterWakeZoneId or InstanceProtocol.TeleportId
            or InstanceProtocol.MoveToId or InstanceProtocol.AnimationUpdateId or InstanceProtocol.RemoveObjectId
            && MirrorProjectiles(ciphers, bytes)) return;
        if (message is not (InstanceProtocol.ActionStartedId or 0x1ac or InstanceProtocol.ActorAnimationId
            or InstanceProtocol.AnimationUpdateId or InstanceEntities.AddBuffId or InstanceEntities.RemoveBuffId)) return;
        uint id = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(2));
        ClientState? conn = null;
        foreach (var c in InGame.Keys) if (ReferenceEquals(c.Wire.Ciphers, ciphers)) { conn = c; break; }
        // The instance's monsters' own messages go where they are made (BroadcastMonsterMessage); here only the players'
        // minions', whose ids each other client shows under its own.
        if (id >= SharedMonsterIds || conn is null || conn.Closed || conn.LeavingArea || !Mirrorable(conn, bytes, id)) return;
        MirrorMinion(conn, bytes, id);
    }

    // An instance monster's action (a swing, an area skill, a summon's clip, not a walk: SyncInstanceMonsters sends
    // those) or hit reaction, made for this connection's client: to every other client in the instance that has it awake.
    // Its target, the local player there (PlayerId), is this player as the others know them (PeerId).
    internal static void BroadcastMonsterMessage(ClientState conn, uint id, byte[] bytes)
    {
        if (conn.Instance is null || !conn.Instance.World.MonsterOrigins.ContainsKey(id) || !Mirrorable(conn, bytes, id)) return;
        var others = conn.Instance.Presence.PlayersExcept(conn.PeerId);
        if (others.Length == 0) return;
        ushort message = (ushort)(bytes[0] << 8 | bytes[1]);
        byte[] copy = (byte[])bytes.Clone();
        // 0x1ab: u16 id, u32 object, u32 0, u16 header, u16 flags (0x80: a u32 target object follows).
        if (message == InstanceProtocol.ActionStartedId && copy.Length >= 18 && (copy[13] & 0x80) != 0
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(14)) == PlayerId)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(14), conn.PeerId);
        Mirroring.Value = true;
        try
        {
            foreach (var peer in others)
            {
                if (peer.Owner is not { Outbox: { } outbox } other || other.Closed || other.LeavingArea) continue;
                if (Array.BinarySearch(other.AwakeMonsters, id) < 0) continue;
                _ = outbox((byte[])copy.Clone(), null);
            }
        }
        finally { Mirroring.Value = false; }
    }

    // A player's own action confirmed to their client (the 0x1ab echo of a skill use, Messages.cs; its 0x1ac end): on
    // the other clients the player's object, as they know it (PeerId), plays it too. Walks are left: SyncInstancePlayers
    // shows them, and a second order would restart them. The sequence is the owner's prediction; elsewhere it is a
    // server action (0xffff).
    static void MirrorPlayerAction(ConnectionCiphers ciphers, byte[] bytes)
    {
        ClientState? conn = null;
        foreach (var c in InGame.Keys) if (ReferenceEquals(c.Wire.Ciphers, ciphers)) { conn = c; break; }
        if (conn is null || conn.Closed || conn.LeavingArea) return;
        var others = conn.Instance.Presence.PlayersExcept(conn.PeerId);
        if (others.Length == 0) return;
        byte[] copy = (byte[])bytes.Clone();
        if ((copy[0] << 8 | copy[1]) == InstanceProtocol.ActionStartedId)
        {
            if (copy.Length < 18) return;
            int at = 14 + ((copy[13] & 0x80) != 0 ? 4 : 0);
            for (int i = 0; i < 4 && at < copy.Length; i++)
                at += copy[at] switch { < 0x80 => 1, < 0xc0 => 2, < 0xe0 => 3, < 0xf0 => 4, _ => 5 };
            if (at + 4 > copy.Length) return;
            ushort skill = (ushort)(copy[at] << 8 | copy[at + 1]);
            bool move = SkillsFor(conn).ActionName(skill) == "Move";
            conn.PeerActing = !move;
            if (move) return;
            copy[at + 2] = 0xff; copy[at + 3] = 0xff;
            // Anything after it (an 0x1ac in the same tick) is the player's own too.
            for (int next = at + 4; next + 12 <= copy.Length; next += 12)
                if ((copy[next] << 8 | copy[next + 1]) == 0x1ac) System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(next + 2), conn.PeerId);
                else return;
        }
        else
        {
            if (copy.Length != 12 || !conn.PeerActing) return;
            conn.PeerActing = false;
        }
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(2), conn.PeerId);
        Mirroring.Value = true;
        try
        {
            foreach (var peer in others)
            {
                if (peer.Owner is not { Outbox: { } outbox } other || other.Closed || other.LeavingArea) continue;
                bool knows;
                try { knows = other.SeenPlayers.ContainsKey(conn.PeerId); } catch (InvalidOperationException) { knows = false; }
                if (knows) _ = outbox((byte[])copy.Clone(), null);
            }
        }
        finally { Mirroring.Value = false; }
    }

    // A player's hit on an instance monster (0x1af: u16 id, object, u32 0, u16 0, u16 sequence, u8, u8, u32 target, u16
    // flags, u8 kind, then by kind: bit1 (u16 when flags 0x400) u8 u8, bit2 u16, bit3 u16). One message exactly, or none.
    static void MirrorPlayerHit(ConnectionCiphers ciphers, byte[] bytes)
    {
        if (bytes.Length < 23) return;
        byte kind = bytes[22];
        int length = 23;
        if ((kind & 0x02) != 0) length += 2 + (((bytes[20] << 8 | bytes[21]) & 0x400) != 0 ? 2 : 0);
        if ((kind & 0x04) != 0) length += 2;
        if ((kind & 0x08) != 0) length += 2;
        if (length != bytes.Length) return;
        uint target = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16));
        ClientState? conn = null;
        foreach (var c in InGame.Keys) if (ReferenceEquals(c.Wire.Ciphers, ciphers)) { conn = c; break; }
        if (conn is null || conn.Closed || conn.LeavingArea || !conn.Instance.World.MonsterOrigins.ContainsKey(target)) return;
        byte[] copy = (byte[])bytes.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(2), conn.PeerId);
        copy[12] = 0xff; copy[13] = 0xff;
        Mirroring.Value = true;
        try
        {
            foreach (var peer in conn.Instance.Presence.PlayersExcept(conn.PeerId))
                if (peer.Owner is { Outbox: { } outbox } other && !other.Closed && !other.LeavingArea
                    && Array.BinarySearch(other.AwakeMonsters, target) >= 0)
                    _ = outbox((byte[])copy.Clone(), null);
        }
        finally { Mirroring.Value = false; }
    }

    // Whether a blob can go to another client as it is: only messages of a layout read exactly here, all for the one
    // object. Another client reads every message by its own layout, so one it reads otherwise shifts the whole stream:
    // a Pride aura on a monster (0x1c1 row 1331, from the local player) carries no values for the player who has the aura
    // and three for any other, and the other client read the next message from the middle ("Unable to deserialise packet
    // with pid 221. Last PID was 449", live 2026-11-04). A buff from the local player (an aura, an ailment, a curse) or with
    // anything optional stays its own; a removal goes only after an addition that went.
    static bool Mirrorable(ClientState conn, byte[] bytes, uint id)
    {
        var buffs = new List<(ushort Instance, bool Add)>();
        for (int at = 0; at < bytes.Length;)
        {
            if (bytes.Length - at < 12 || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 2)) != id) return false;
            ushort message = (ushort)(bytes[at] << 8 | bytes[at + 1]);
            int length;
            switch (message)
            {
                case 0x1ac: length = 12; break;
                case InstanceProtocol.ActorAnimationId: length = 18; break;
                case InstanceProtocol.AnimationUpdateId: length = 21; break;
                case InstanceEntities.RemoveBuffId:
                    length = 14;
                    if (at + 14 > bytes.Length) return false;
                    buffs.Add(((ushort)(bytes[at + 12] << 8 | bytes[at + 13]), false));
                    break;
                case InstanceEntities.AddBuffId:
                    length = 27;
                    if (at + 27 > bytes.Length || bytes[at + 22] != 0
                        || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 16)) == PlayerId
                        || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 23)) != 0) return false;
                    buffs.Add(((ushort)(bytes[at + 12] << 8 | bytes[at + 13]), true));
                    break;
                case InstanceProtocol.ActionStartedId:
                {
                    if (bytes.Length - at < 18) return false;
                    int end = at + 14 + ((bytes[at + 13] & 0x80) != 0 ? 4 : 0);
                    for (int i = 0; i < 4; i++)
                    {
                        if (end >= bytes.Length) return false;
                        end += bytes[end] switch { < 0x80 => 1, < 0xc0 => 2, < 0xe0 => 3, < 0xf0 => 4, _ => 5 };
                    }
                    length = end + 4 - at;
                    if (at + length <= bytes.Length && (bytes[end] << 8 | bytes[end + 1]) == 0x2909) return false;
                    break;
                }
                default: return false;
            }
            if (at + length > bytes.Length) return false;
            at += length;
        }
        foreach (var (instance, add) in buffs)
            if (!add && !conn.MirroredBuffs.ContainsKey((id, instance))) return false;
        foreach (var (instance, add) in buffs)
            if (add) conn.MirroredBuffs[(id, instance)] = 0; else conn.MirroredBuffs.TryRemove((id, instance), out _);
        return true;
    }

    // A player's minion's action, hit reaction or buff: to each client that shows it, under its id there.
    static void MirrorMinion(ClientState conn, byte[] bytes, uint id)
    {
        if (!conn.MinionOrigins.ContainsKey(id) || bytes.Length != MessageLength(bytes)) return;
        Mirroring.Value = true;
        try
        {
            foreach (var peer in conn.Instance.Presence.PlayersExcept(conn.PeerId))
            {
                if (peer.Owner is not { Outbox: { } outbox } other || other.Closed || other.LeavingArea
                    || !other.MirroredObjects.TryGetValue((conn.PeerId, id), out uint theirs)) continue;
                byte[] copy = (byte[])bytes.Clone();
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(2), theirs);
                // An action aimed at the local player there means this player.
                if (copy.Length >= 18 && copy[0] == 0x01 && copy[1] == 0xab && (copy[13] & 0x80) != 0
                    && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(copy.AsSpan(14)) == PlayerId)
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(14), conn.PeerId);
                _ = outbox(copy, null);
            }
        }
        finally { Mirroring.Value = false; }
    }

    // Another client's frame: the other players' minions created, moved, hurt and removed here, under ids of this client.
    internal static async Task SyncPeerMinions(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var shown = new Dictionary<(uint, uint), (ClientState Owner, SharedMonster M)>();
        foreach (var peer in conn.Instance.Presence.PlayersExcept(conn.PeerId))
            if (peer.Owner is { } owner && !owner.Closed && !owner.LeavingArea)
                foreach (var m in owner.PublishedMinions) shown[(owner.PeerId, m.Id)] = (owner, m);
        if (shown.Count == 0 && conn.SeenMinions.Count == 0) return;
        Mirroring.Value = true;
        await Send(remote, stream, ciphers, [], null, atGate: () =>
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea || !conn.PlayerSent) return null;
            var packets = new List<byte[]>();
            foreach (var gone in conn.SeenMinions.Keys.Where(k => !shown.ContainsKey(k)).ToList())
            {
                conn.SeenMinions.Remove(gone);
                if (conn.MirroredObjects.TryRemove(gone, out uint was)) packets.Add(InstanceProtocol.RemoveObject(was));
            }
            foreach (var (key, (owner, m)) in shown)
            {
                if (!conn.SeenMinions.TryGetValue(key, out var before))
                {
                    uint id = (uint)Interlocked.Increment(ref conn.NextMirroredObject);
                    conn.MirroredObjects[key] = id;
                    var o = m.Origin;
                    lock (conn.Monsters)
                        if (conn.MonsterTypes.Add((o.Row, o.Level))) packets.Add(InstanceEntities.MonsterTypes([(o.Row, o.Level)]));
                    packets.Add(InstanceEntities.CreateObject(id, o.Type, (o.Object with { GridX = m.X, GridY = m.Y, Life = m.Life }).Blob()));
                    packets.Add(InstanceProtocol.EnterWakeZone(id));
                    if (m.Destination != (m.X, m.Y))
                        packets.Add(InstanceProtocol.ActionStarted(id, m.X, m.Y, m.Destination.X, m.Destination.Y, 0x2909, 0xffff, 0x0408));
                }
                else if (conn.MirroredObjects.TryGetValue(key, out uint id))
                {
                    if (m.Destination != before.Destination && m.Destination != (m.X, m.Y))
                        packets.Add(InstanceProtocol.ActionStarted(id, m.X, m.Y, m.Destination.X, m.Destination.Y, 0x2909, 0xffff, 0x0408));
                    else if (m.Destination == (m.X, m.Y) && Math.Max(Math.Abs(before.X - m.X), Math.Abs(before.Y - m.Y)) > 20)
                        packets.Add(InstanceProtocol.Teleport(id, m.X, m.Y, 0x7fffffff));
                    if (before.Life != m.Life) packets.Add(InstanceProtocol.LifeUpdate(id, m.Life));
                }
                conn.SeenMinions[key] = m;
            }
            return packets.Count == 0 ? null : packets.SelectMany(p => p).ToArray();
        });
    }

    // The first message's length when the blob is one of Mirrorable's (any other: -1).
    static int MessageLength(byte[] bytes)
    {
        ushort message = (ushort)(bytes[0] << 8 | bytes[1]);
        if (message != InstanceProtocol.ActionStartedId)
            return message switch { 0x1ac => 12, InstanceProtocol.ActorAnimationId => 18, InstanceProtocol.AnimationUpdateId => 21,
                InstanceEntities.RemoveBuffId => 14, InstanceEntities.AddBuffId => 27, _ => -1 };
        int end = 14 + ((bytes[13] & 0x80) != 0 ? 4 : 0);
        for (int i = 0; i < 4 && end < bytes.Length; i++) end += bytes[end] switch { < 0x80 => 1, < 0xc0 => 2, < 0xe0 => 3, < 0xf0 => 4, _ => 5 };
        return end + 4;
    }

    // A projectile object a connection's runtime shows (a monster's shot, a continuation, a lobbed mortar: Projectiles.cs)
    // on every other client in the instance, under an id of that client's own: created, woken, gliding, stopped, playing
    // its clips and removed with it. The messages are split by their fixed layouts; a blob with anything else is left.
    // True when the bytes were a projectile's.
    internal static bool MirrorProjectiles(ConnectionCiphers ciphers, byte[] bytes)
    {
        var parts = new List<(int At, int Length, uint Id, ushort Message)>();
        for (int at = 0; at < bytes.Length;)
        {
            if (bytes.Length - at < 12) return false;
            ushort message = (ushort)(bytes[at] << 8 | bytes[at + 1]);
            int length = message switch
            {
                InstanceProtocol.EnterWakeZoneId or InstanceProtocol.RemoveObjectId => 12,
                InstanceProtocol.TeleportId => 28,
                InstanceProtocol.MoveToId => 29,
                InstanceProtocol.AnimationUpdateId => 21,
                InstanceEntities.CreateObjectId when bytes.Length - at >= 18 => 18 + (bytes[at + 16] << 8 | bytes[at + 17]),
                _ => -1,
            };
            if (length < 0 || at + length > bytes.Length) return false;
            parts.Add((at, length, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 2)), message));
            at += length;
        }
        ClientState? conn = null;
        foreach (var c in InGame.Keys) if (ReferenceEquals(c.Wire.Ciphers, ciphers)) { conn = c; break; }
        if (conn is null) return false;
        try
        {
            foreach (var p in parts)
                if (p.Message == InstanceEntities.CreateObjectId ? !conn.ProjectileObjects.Contains(p.Id) : !conn.MirroredProjectiles.ContainsKey(p.Id))
                    return false;
        }
        catch (InvalidOperationException) { return false; } // the runtime changed its set meanwhile: this blob stays its own
        foreach (var p in parts) if (p.Message == InstanceEntities.CreateObjectId) conn.MirroredProjectiles[p.Id] = 0;
        Mirroring.Value = true;
        try
        {
            foreach (var peer in conn.Instance.Presence.PlayersExcept(conn.PeerId))
            {
                if (peer.Owner is not { Outbox: { } outbox } other || other.Closed || other.LeavingArea) continue;
                var copy = new List<byte>(bytes.Length);
                foreach (var p in parts)
                {
                    var key = (conn.PeerId, p.Id);
                    uint id;
                    if (p.Message == InstanceEntities.CreateObjectId)
                        other.MirroredObjects[key] = id = (uint)Interlocked.Increment(ref other.NextMirroredObject);
                    else if (!other.MirroredObjects.TryGetValue(key, out id)) continue; // created before this client came
                    if (p.Message == InstanceProtocol.RemoveObjectId) other.MirroredObjects.TryRemove(key, out _);
                    var part = bytes.AsSpan(p.At, p.Length).ToArray();
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(part.AsSpan(2), id);
                    copy.AddRange(part);
                }
                if (copy.Count > 0) _ = outbox([.. copy], null);
            }
        }
        finally { Mirroring.Value = false; }
        foreach (var p in parts) if (p.Message == InstanceProtocol.RemoveObjectId) conn.MirroredProjectiles.TryRemove(p.Id, out _);
        return true;
    }

    // Every client's frame: the instance's monsters as this client should have them. The ones it was not shown are created
    // (where they are, with the life they have; the runner's own spawns were created where they were spawned); the
    // others' walks reach it (the runner's AI sends its own orders), every client gets the life the others' hits left,
    // and the ones gone from the instance go. A monster out of the runner's wake zone gets no order (an order wakes a
    // sleeping object and the next 0x1a9 crashed the client); the others never put one to sleep.
    internal static async Task SyncInstanceMonsters(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var world = conn.Instance.World;
        bool watch;
        lock (world.Monsters)
        {
            if (!RunnerLive(world)) BecomeRunnerLocked(remote, conn, world);
            watch = IsMonsterRunner(conn) && MonsterEmerge.Unwatched(conn);
        }
        // Monsters still in wait whose watcher left: the runner watches them now.
        if (watch) WatchBuried(remote, stream, ciphers, conn);
        bool runner = IsMonsterRunner(conn);
        // What goes to this client here is its own view: nothing of it goes on to the others (MirrorSharedMonsters).
        Mirroring.Value = true;
        var bossBars = new List<(uint Id, SpawnedMonster M)>();
        await Send(remote, stream, ciphers, [], null, atGate: () =>
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea || !conn.PlayerSent) return null;
            var packets = new List<byte[]>();
            lock (world.Monsters)
            {
                double now = conn.Clock.Elapsed.TotalSeconds;
                foreach (uint gone in conn.MonsterViews.Keys.Where(id => !world.Monsters.ContainsKey(id)
                    && !world.DormantMonsters.ContainsKey(id)).ToList())
                {
                    conn.MonsterViews.Remove(gone);
                    if (!runner) packets.Add(InstanceProtocol.RemoveObject(gone));
                }
                foreach (var (id, m) in world.Monsters.Concat(world.DormantMonsters).OrderBy(e => e.Key))
                {
                    if (!world.MonsterOrigins.TryGetValue(id, out var o)) continue; // a player's minion: SyncPeerMinions
                    bool dormant = world.DormantMonsters.ContainsKey(id);
                    uint life = m.Dead ? 0 : m.Life;
                    var at = m.Walk.Position(now);
                    var to = dormant ? at : m.Walk.Destination;
                    if (!conn.MonsterViews.TryGetValue(id, out var seen))
                    {
                        if (life == 0) continue; // died before this client came: nothing to show
                        if (conn.MonsterTypes.Add((o.Row, o.Level))) packets.Add(InstanceEntities.MonsterTypes([(o.Row, o.Level)]));
                        // One that has risen meanwhile is created risen (targetable), not in the wait it was placed in.
                        var shown = o.Object with { GridX = at.X, GridY = at.Y, Life = life };
                        if (!dormant && m.Variety is { } v && !MonsterEmerge.Waiting(conn, id) && shown.TransitionState != MonsterEmerge.RisenState(v)
                            && v.ComponentsOrBase.Contains("Transitionable"))
                            shown = shown with { TransitionState = MonsterEmerge.RisenState(v) };
                        packets.Add(InstanceEntities.CreateObject(id, o.Type, shown.Blob()));
                        packets.Add(InstanceProtocol.EnterWakeZone(id));
                        if (MonsterRarityBuffs.Row(o.Rarity) is ushort glow) packets.Add(InstanceEntities.AddBuff(id, conn.NextBuff++, glow));
                        if (to != at) packets.Add(InstanceProtocol.ActionStarted(id, at.X, at.Y, to.X, to.Y, 0x2909, 0xffff, 0x0408));
                        conn.MonsterViews[id] = new(at, to, life);
                        continue;
                    }
                    if (dormant || conn.AsleepMonsters.Contains(id)) continue;
                    // Its walks: from the AI of its target's connection, which sends that client its own orders.
                    if (!ReferenceEquals(m.Target, conn) && !(runner && m.Target is null))
                    {
                        if (to != seen.To && to != at)
                            packets.Add(InstanceProtocol.ActionStarted(id, at.X, at.Y, to.X, to.Y, 0x2909, 0xffff, 0x0408));
                        else if (to == at && Math.Max(Math.Abs(seen.At.X - at.X), Math.Abs(seen.At.Y - at.Y)) > 20)
                            packets.Add(InstanceProtocol.Teleport(id, at.X, at.Y, 0x7fffffff));
                    }
                    if (seen.Life != life) packets.Add(InstanceProtocol.LifeUpdate(id, life));
                    conn.MonsterViews[id] = new(at, to, life);
                    // A boss the others fight: its life bar here too.
                    if (m.BossBarShown && life > 0 && conn.BossBarsSeen.Add(id)) bossBars.Add((id, m));
                }
                // A rise can follow this create before the next periodic publication. Its queued update must see
                // the just-admitted view; the recipient's send gate orders that update after these create bytes.
                conn.AwakeMonsters = AwakeMonsterIds(conn);
            }
            return packets.Count == 0 ? null : packets.SelectMany(p => p).ToArray();
        });
        foreach (var (id, m) in bossBars) await ShowBossLifeBar(remote, stream, ciphers, id, m, conn);
    }
}
