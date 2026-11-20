using System.Diagnostics;
using GameServer.Core.Diagnostics;
using GameServer.Core.Protocol.Instance;

public partial class Program
{
    // Birth waits precede gameplay/send gates. Projection runs once at the existing gate; only neutral records wait
    // for a frame. The existing module lifetime check is retained, without introducing a new reload contract.
    public static async Task<bool> SendRecords(Player conn, IReadOnlyList<WorldObject> requires,
        Func<WorldAdmission, OutputBatch?> atGate, Func<bool>? still = null, IReadOnlyList<WorldObject>? creates = null,
        IWorldAdmissionRules? rules = null, bool quest = false)
    {
        var output = conn.WorldOutput;
        WorldObject[] dependencies = [.. requires], reservations = creates is null ? [] : [.. creates];
        foreach (var target in dependencies.Concat(reservations)) output.CheckOwner(target);
        var admission = new WorldAdmission(output, reservations);
        bool admitted = false;
        try
        {
            if (dependencies.Intersect(reservations).Any())
                throw new ArgumentException("An output batch cannot wait on its own unadmitted birth.");
            if (!await output.AwaitBirths(dependencies)) return false;
            var wire = conn.Session;
            var gate = SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
            if (quest) await conn.QuestGate.WaitAsync();
            try
            {
                await gate.WaitAsync();
                try
                {
                    if (conn.Closed || conn.LeavingArea || output.Closed) return false;
                    if (dependencies.Any(t => !output.IsReady(t))) return false;
                    if (rules is not null && (!ReferenceEquals(GameLogicOrNull, rules) || !rules.WorldAdmissionActive)) return false;
                    if (ProjectileOutputGuard.Value is { } guard && !guard()) return false;
                    if (still is not null && !still()) return false;
                    if (atGate(admission) is not { } batch) return false;
                    var immediate = output.Admit(batch, admission, output.Started && conn.LockstepClient);
                    admitted = true;
                    admission.Finish(true);
                    // No module callback is retained in the frame queue or invoked while flushing it.
                    atGate = null!; still = null; rules = null;
                    foreach (var packet in immediate)
                        await WritePacket(wire.Remote, wire.Stream, wire.Ciphers, packet.Bytes, packet.Log, packet);
                    output.Written(immediate.Select(p => p.Creates).OfType<WorldObject>().ToArray(),
                        immediate.Select(p => p.Removes).OfType<WorldObject>().ToArray(), frameEnded: false);
                    return true;
                }
                catch { if (admitted) FailWorldOutput(conn); throw; }
                finally { gate.Release(); }
            }
            finally { if (quest) conn.QuestGate.Release(); }
        }
        finally { if (!admitted) admission.Finish(false); }
    }

    public static Task<bool> SendRecords(Player conn, OutputBatch batch, Func<bool>? still = null) =>
        SendRecords(conn, batch.Records.Where(r => r.Kind == OutputRecord.RecordKind.World).Select(r => r.Object!).Distinct().ToArray(),
            _ => batch, still, batch.Records.Where(r => r.Kind == OutputRecord.RecordKind.Create).Select(r => r.Object!).ToArray());

    /// <summary>One of the area's records (GameArea.Output) for this connection's player: a record about an object goes, on a
    /// lockstep client, into the frame once the client has the object (as SendWorld); anything else goes out at once.</summary>
    public static async Task SendAreaRecord(Player conn, AreaOutput.Record record)
    {
        var wire = conn.Session;
        if (record.Object is uint id && conn.LockstepClient && record.Bytes.Length >= 12)
        {
            var order = new WorldOrder(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(record.Bytes.AsSpan(6)),
                System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(record.Bytes.AsSpan(10)));
            await SendWorld(conn, conn.WorldOutput.PublishedObject(id), order, () => record.Bytes, record.Log);
        }
        else await Send(wire.Remote, wire.Stream, wire.Ciphers, record.Bytes, record.Log);
    }

    public static Task<bool> SendWorld(Player conn, WorldObject target, WorldOrder order,
        Func<byte[]?> atGate, string? log = null, Func<bool>? still = null) =>
        SendRecords(conn, [target], _ => atGate() is { } bytes ? new(OutputRecord.World(target, order, bytes, log)) : null, still);

    // Differs from GGG: it never sends 0x2b8 (14 236 frames) and relies on the client's default, which is not traced; ours
    // says the length it paces by. Every connection runs these frames: the client alone picks its Network Mode, and a
    // predictive one ignores 0x2b7 (07 §6). The area's driver (AreaFrames) ends them for every connection in the area at
    // once. What the game does at a frame end is asked of the rules loaded then, so after a game module reload the new
    // rules take over at the next frame; the driver runs on an execution context of its own, since the caller's async-local
    // values (the game module's) would ride along for the area's life.
    public static void StartWorldFrames(Player conn)
    {
        var (remote, stream, ciphers) = conn.Session;
        if (ExecutionContext.IsFlowSuppressed())
        {
            _ = Task.Run(() => conn.Frames.Join(remote, stream, ciphers, conn));
            return;
        }
        using (ExecutionContext.SuppressFlow())
            _ = Task.Run(() => conn.Frames.Join(remote, stream, ciphers, conn));
    }

    public static async Task<double> BeginWorldFrames(Player conn, double frameSeconds)
    {
        var gate = SendGates.GetValue(conn.Session.Ciphers, _ => new(1, 1));
        await gate.WaitAsync();
        try
        {
            lock (conn.Monsters)
            {
                ObjectDisposedException.ThrowIf(conn.Closed || conn.WorldOutput.Closed, conn.WorldOutput);
                // Frames start on the instance clock's own grid (whole frame lengths), so every connection of an instance
                // ends its frames at the same instants, as the clients of one stepped world do: what one of them is sent
                // in a frame can then reach the others exactly one frame later (Player.FrameFrom).
                double first = Math.Ceiling(conn.Clock.Elapsed.TotalSeconds / frameSeconds) * frameSeconds;
                conn.WorldOutput.Begin(conn.ServerFrame);
                if (conn.LockstepClient) conn.LockstepFrame = (first, first + frameSeconds);
                return first;
            }
        }
        finally { gate.Release(); }
    }

    public static async Task<bool> EndWorldFrame(Player conn, (double Start, double End) next)
    {
        var wire = conn.Session;
        var gate = SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
        await gate.WaitAsync();
        bool committed = false;
        try
        {
            WorldOutput.Frame frame;
            lock (conn.Monsters)
            {
                if (conn.Closed || conn.WorldOutput.Closed || conn.Paused) return false;
                frame = conn.WorldOutput.TakeFrame();
                conn.ServerFrame++;
                if (conn.LockstepClient) conn.LockstepFrame = next;
                committed = true;
            }
            conn.TurnFrame();
            long writing = Stopwatch.GetTimestamp();
            int bytes = 0;
            // The frame's records and its end in one write.
            var packets = new List<(byte[], string?, WorldOutput.Packet?)>(frame.Packets.Count + 1);
            foreach (var packet in frame.Packets)
            {
                bytes += packet.Bytes.Length;
                packets.Add((packet.Bytes, packet.Log, packet));
            }
            packets.Add((InstanceProtocol.FrameEnd(), null, null));
            await WritePackets(wire.Remote, wire.Stream, wire.Ciphers, packets);
            conn.WorldOutput.Written(frame.Births, frame.Removals, frameEnded: true);
            conn.Frames.Wrote(frame.Packets.Count, bytes, Stopwatch.GetElapsedTime(writing).TotalMilliseconds);
            return true;
        }
        catch { if (committed) FailWorldOutput(conn); throw; }
        finally { gate.Release(); }
    }

    /// <summary>While paused, what the server sends the player (a passive point's stats, an item's) still reaches a lockstep
    /// client, which applies world records only in the frames it runs: they go out in a frame of 0 ms (S→C 0x2b8 0, the
    /// records, 0x2b7, then 0x2b8 back to the frame length). The client runs a frame its time already covers at once, and
    /// a 0 ms frame is covered by no time at all (0x14218a2a0), so its records apply and the world does not move. Not on
    /// the frame timeline: it ends no frame of the clock's.</summary>
    public static async Task EndPausedFrame(Player conn)
    {
        var wire = conn.Session;
        var gate = SendGates.GetValue(wire.Ciphers, _ => new(1, 1));
        await gate.WaitAsync();
        bool committed = false;
        try
        {
            WorldOutput.Frame frame;
            lock (conn.Monsters)
            {
                if (conn.Closed || conn.WorldOutput.Closed || !conn.Paused || !conn.WorldOutput.Started) return;
                frame = conn.WorldOutput.TakeFrame();
                if (frame.Packets.Count == 0) return;
                committed = true;
            }
            List<(byte[], string?, WorldOutput.Packet?)> packets = [(InstanceProtocol.FrameLength(0), "0x02b8 frame length 0 ms (paused)", null)];
            foreach (var packet in frame.Packets) packets.Add((packet.Bytes, packet.Log, packet));
            packets.Add((InstanceProtocol.FrameEnd(), null, null));
            packets.Add((InstanceProtocol.FrameLength(FrameMs), null, null));
            await WritePackets(wire.Remote, wire.Stream, wire.Ciphers, packets);
            conn.WorldOutput.Written(frame.Births, frame.Removals, frameEnded: true);
        }
        catch { if (committed) FailWorldOutput(conn); throw; }
        finally { gate.Release(); }
    }

    static void FailWorldOutput(Player conn)
    {
        conn.CloseReason ??= "World output failed; its cipher stream cannot be retried";
        conn.Closed = true;
        conn.Session.Stream.Close();
    }
}
